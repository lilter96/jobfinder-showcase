using System.Text.Json;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Modules.Preparation;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Career;

public sealed class InterviewPrep(IDbContextFactory<JobFinderDbContext> db, JobFinderLlm llm,
    InterviewPreparationRequests requests, InterviewSources sources, InterviewTransitions transitions)
{
    public static InterviewCopilot? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize(json, InterviewJsonContext.Default.InterviewCopilot); }
        catch (JsonException) { return null; }
    }

    public async Task BuildPackAsync(long interviewId, CancellationToken ct)
    {
        if (await requests.RequestAsync(interviewId, ct) is { } id) await requests.WaitAsync(id, ct);
    }

    public Task<ActionResult> SavePositionAsync(InterviewPositionSnapshot snapshot, string text, CancellationToken ct) =>
        sources.SavePositionAsync(snapshot, text, ct);

    public Task<ActionResult> SetTimeAsync(InterviewEditSnapshot snapshot, DateTimeOffset at, CancellationToken ct, ConversationState? prompt = null) =>
        sources.SetTimeAsync(snapshot, at, ct, prompt);

    public async Task<(DebriefResult Result, long ApplicationId)> DebriefAsync(InterviewEditSnapshot snapshot, string headerHash, string story, CancellationToken ct,
        ConversationState? prompt = null)
    {
        prompt = prompt is null ? null : ConversationStore.Freeze(prompt);
        string header;
        await using (var context = await db.CreateDbContextAsync(ct))
        {
            var source = await context.Interviews.AsNoTracking().Include(row => row.Application).ThenInclude(row => row!.Vacancy)
                .SingleOrDefaultAsync(row => row.Id == snapshot.Source.Id, ct);
            if (source is null || !snapshot.Matches(source)) throw new InterviewEditConflictException();
            header = Header(source.Application!.Vacancy!.CompanyName, source.Application.Vacancy.Title, source.Kind);
            if (Fingerprint.Of(header) != headerHash) throw new InterviewEditConflictException();
            if (prompt is not null && (prompt.Mode != CareerChat.DebriefMode || prompt.Id == Guid.Empty || prompt.Draft != story
                || prompt.InterviewHeaderHash != headerHash || !await context.Settings.AnyAsync(row => row.Key == ConversationStore.Key
                    && row.Value == ConversationStore.Capture(prompt), ct))) throw new InterviewEditConflictException();
        }
        var operation = await llm.ForOperationAsync(ct);
        var result = (await operation.DebriefAsync(header, story, ct)).Value;
        var applicationId = await transitions.CompleteDebriefAsync(snapshot, result, ct, Fingerprint.Of(header), prompt);
        return (result, applicationId);
    }

    internal static string Header(string? company, string title, JobFinder.Core.Domain.InterviewKind kind) =>
        $"Компания: {company}; вакансия: {title}; этап: {CareerCards.InterviewLabel(kind)}";
}
