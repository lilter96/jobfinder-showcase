using System.Globalization;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Core.Time;
using JobFinder.Data;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Discovery;
using JobFinder.Pipeline.Modules.Preparation;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed class ReplyProcessingSources(InterviewTransitions transitions, EmployerMessages messages, ReplyDraftSources drafts,
    Notifier notifier, RuntimeSettings settings, LocalClock clock, TimeProvider time)
{
    internal async Task<ReplyProcessingInput> CaptureAsync(JobFinderDbContext context, ReplyProcessingRequest request, InboundReply raw,
        JobFinderLlm operation, CancellationToken ct)
    {
        var vacancy = await EmployerMessageSources.VacancyAsync(context, request.VacancyId, ct) ?? throw new ReplyProcessingSupersededException();
        var app = vacancy.Applications.SingleOrDefault(row => row.Id == request.ApplicationId) ?? throw new ReplyProcessingSupersededException();
        var documents = await context.Documents.AsNoTracking().Where(row => row.Key == ProfileProvider.Key || row.Key == FactsProvider.Key).ToListAsync(ct);
        var profile = documents.SingleOrDefault(row => row.Key == ProfileProvider.Key);
        var facts = documents.SingleOrDefault(row => row.Key == FactsProvider.Key);
        var interviews = await context.Interviews.AsNoTracking().Where(row => row.ApplicationId == app.Id && row.Status == InterviewStatus.Scheduled)
            .OrderBy(row => row.Id).ToListAsync(ct);
        var input = new ReplyProcessingInput
        {
            ApplicationId = app.Id, Vacancy = AssessmentVacancy.From(vacancy), ProfileVersion = profile?.Version ?? 0, FactsVersion = facts?.Version ?? 0,
            Profile = profile is null ? new CandidateProfile() : ProfileProvider.FromJson(profile.Content),
            Facts = (facts is null ? new CandidateFacts() : JsonSerializer.Deserialize(facts.Content, FactsJsonContext.Default.CandidateFacts)
                ?? throw new JsonException("empty captured facts")).WithStandardQuestions(),
            Stories = await ReplyDraftSources.StoriesAsync(context, ct), ContextHash = ReplyDraftSources.ContextHash(vacancy),
            Interviews = interviews.Select(row => InterviewEditSnapshot.Capture(row).Source.Hash()).ToArray(),
            Conversation = ReplyProcessingRequests.Conversation(app.Events.OrderByDescending(row => row.At).Take(8)), Message = raw.Message(),
            CapturedAt = clock.Now, TimeZone = clock.Zone.Id, Operation = operation.CaptureOperation(),
            AutoReply = await settings.GetBoolAsync(RuntimeSettings.ChatAutoReply, true, ct),
        };
        input.Recipe = ReplyProcessingSteps.Recipe(operation, input);
        return input;
    }

    internal async Task<bool> PublishAsync(JobFinderDbContext context, IDbContextOutbox outbox, ReplyProcessingRequest request,
        ReplyProcessingInput input, ReplyProcessingResult result, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is null) throw new InvalidOperationException("reply publication requires an enrolled source transaction");
        if (!await ReplyProcessingRequests.MatchesAsync(context, input, ct)) return false;
        if (request.Mode == ReplyProcessingMode.Preview) return true;
        if (request.Mode == ReplyProcessingMode.Redraft)
            return string.IsNullOrWhiteSpace(result.Analysis.Draft)
                || (await drafts.PublishJoinedAsync(context, input.DraftSnapshot!, result.Analysis, result.Problems, ct)).Ok;

        var raw = ReplyProcessingRequests.ReadRaw(request);
        if (raw.ExternalId is not null && await context.ApplicationEvents.AnyAsync(row => row.Channel == raw.Channel && row.ExternalId == raw.ExternalId
            && (request.Mode != ReplyProcessingMode.Reprocess || row.Id != request.EventId), ct))
            throw new ReplyProcessingSupersededException();
        var analysis = result.Analysis;
        var classification = analysis.Classification switch
        {
            "invitation" => ReplyClass.Invitation, "question" => ReplyClass.Question, "rejection" => ReplyClass.Rejection,
            "offer" => ReplyClass.Offer, "viewed" => ReplyClass.Viewed, _ => ReplyClass.Other,
        };
        var draft = string.IsNullOrWhiteSpace(analysis.Draft) ? null : HumanText.Clean(analysis.Draft.Trim());
        var source = request.Mode == ReplyProcessingMode.Reprocess
            ? await context.ApplicationEvents.SingleAsync(row => row.Id == request.EventId, ct)
            : new ApplicationEvent { ApplicationId = request.ApplicationId, At = raw.At, Kind = EventKind.MessageIn,
                Channel = raw.Channel, Author = raw.Author, ExternalId = raw.ExternalId };
        source.Text = raw.Message();
        source.Classification = classification;
        source.Draft = draft;
        source.DraftStatus = draft is null ? DraftStatus.None : DraftStatus.Pending;
        if (request.Mode == ReplyProcessingMode.Reprocess) source.Revision++;
        else context.ApplicationEvents.Add(source);
        var app = await context.Applications.SingleAsync(row => row.Id == request.ApplicationId, ct);
        if (app.LastInboundAt is null || app.LastInboundAt < raw.At) app.LastInboundAt = raw.At;
        var interviewAt = DateTimeOffset.TryParse(analysis.InterviewAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when) ? when : (DateTimeOffset?)null;
        var kind = analysis.InterviewKind switch
        {
            "technical" => InterviewKind.Technical, "system_design" => InterviewKind.SystemDesign, "coding" => InterviewKind.Coding,
            "final" => InterviewKind.Final, "hr" => InterviewKind.HrScreen, _ => (InterviewKind?)null,
        };
        var target = classification switch
        {
            ReplyClass.Invitation => kind is { } value ? FunnelService.StageFor(value) : ApplicationStage.HrScreen,
            ReplyClass.Question => ApplicationStage.Replied, ReplyClass.Rejection => ApplicationStage.Rejected,
            ReplyClass.Offer => ApplicationStage.Offer, ReplyClass.Viewed => ApplicationStage.Viewed, _ => (ApplicationStage?)null,
        };
        if (target is { } stage)
            await transitions.AdvanceAsync(context, outbox, app, stage, raw.Channel, analysis.Summary, manual: false, ct,
                classification == ReplyClass.Invitation ? interviewAt : null, kind);
        await notifier.SaveJoinedAsync(context, outbox, ct);
        request.EventId = source.Id;
        var vacancy = await EmployerMessageSources.VacancyAsync(context, request.VacancyId, ct) ?? throw new ReplyProcessingSupersededException();
        var view = vacancy.Applications.Single(row => row.Id == request.ApplicationId);
        view.Vacancy = vacancy;
        var decision = raw.YourChat ? new AutoReplyDecision(false, "этот чат с рекрутером вы ведёте сами: только черновик")
            : DialoguePolicy.Decide(analysis, input.Facts, input.AutoReply && await settings.GetBoolAsync(RuntimeSettings.ChatAutoReply, true, ct),
                raw.Channel, raw.At, time.GetUtcNow(), clock.Now, ReplyService.AutoInARow(view.Events.Where(row => row.Id != source.Id)));
        var queued = false;
        if (decision.Send && result.Problems.Count == 0)
        {
            var handoff = await messages.QueueReplyAsync(context, outbox, source.Id, ct);
            queued = handoff.Ok;
            if (!queued) decision = new AutoReplyDecision(false, "автоответ не поставлен в очередь: " + handoff.Message);
        }
        else if (decision.Send) decision = new AutoReplyDecision(false, "черновик не прошёл проверку");
        var local = interviewAt is { } instant ? TimeZoneInfo.ConvertTime(instant, clock.Zone) : (DateTimeOffset?)null;
        var (card, buttons) = CareerCards.Reply(view, source, analysis, local, source.Draft is null ? null : decision.Reason,
            result.Problems, input.Facts, queuedAutomatic: queued);
        notifier.Add(context, NotificationKind.Info, card, buttons, app.Id);
        return true;
    }
}
