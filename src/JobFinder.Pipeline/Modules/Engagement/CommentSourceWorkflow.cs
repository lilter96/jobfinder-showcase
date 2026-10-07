using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Policies;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Core.Time;
using JobFinder.Data;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Engagement;

public sealed class CommentSourceWorkflow(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes,
    CommentPreparationRequests preparations, ServerOwnership ownership, RuntimeSettings settings, EngagementPolicyStore policies,
    Notifier notifier, CommentDeliveryRequests deliveries, LocalClock clock, TimeProvider time)
{
    public async Task RunAsync(Guid id, int version, Guid envelopeId, CancellationToken ct)
    {
        if (id == Guid.Empty || envelopeId == Guid.Empty) throw new UnsupportedCommentSourceException();
        ownership.EnsureOwned();
        await using var read = await db.CreateDbContextAsync(ct);
        CommentSourceRequest? request;
        await using (var transaction = await read.Database.BeginTransactionAsync(ct))
        {
            await CommentSources.LockAsync(read, ct);
            request = await read.CommentSourceRequests.SingleOrDefaultAsync(row => row.Id == id, ct);
            if (request is null || request.Stage is not (CommentSourceStage.Queued or CommentSourceStage.Waiting)
                || request.RetryAt > time.GetUtcNow() || request.ClaimEnvelopeId is { } owner && owner != envelopeId) return;
            ownership.EnsureOwned();
            request.ClaimEnvelopeId = envelopeId;
            await read.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        using var span = Telemetry.StartStage("prepare-source", "comment-source").Tag("request.id", id).Tag("post.id", request.PostId);
        try
        {
            if (version != 1) throw new UnsupportedCommentSourceException();
            var input = CommentSourceInputs.Read(request);
            if (request.Stage == CommentSourceStage.Queued)
            {
                await ChangeAsync(request, async (context, outbox, current) =>
                {
                    if (!await PrerequisitesAsync(context, outbox, current, input, ct)) return;
                    current.PreparationRequestId = await preparations.QueueAsync(context, outbox, input.Preparation, ct, force: input.Replaces is not null);
                    current.Stage = CommentSourceStage.Waiting;
                    var child = context.CommentPreparationRequests.Local.FirstOrDefault(row => row.Id == current.PreparationRequestId)
                        ?? await context.CommentPreparationRequests.SingleAsync(row => row.Id == current.PreparationRequestId, ct);
                    if (child.Stage is CommentPreparationStage.Completed or CommentPreparationStage.Cancelled or CommentPreparationStage.Failed)
                        await outbox.PublishAsync(new PrepareCommentSource { RequestId = current.Id });
                }, ct);
                return;
            }
            var child = await read.CommentPreparationRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == request.PreparationRequestId, ct)
                ?? throw new UnsupportedCommentSourceException();
            if (child.BusinessKey != input.Preparation.Recipe.Hash) throw new UnsupportedCommentSourceException();
            if (child.Stage is CommentPreparationStage.Cancelled or CommentPreparationStage.Failed)
            {
                await StopAsync(request, child.Error ?? "comment preparation stopped", child.Stage == CommentPreparationStage.Cancelled, ct);
                return;
            }
            if (child.Stage != CommentPreparationStage.Completed)
            {
                var next = child.RetryAt > time.GetUtcNow() ? child.RetryAt.Value.AddSeconds(1) : time.GetUtcNow().AddSeconds(2);
                await ChangeAsync(request, (_, outbox, current) => ContinueAsync(outbox, current, next), ct);
                return;
            }
            var result = await preparations.GetAsync(child.Id, ct) ?? throw new UnsupportedCommentSourceException();
            await ChangeAsync(request, async (context, outbox, current) =>
            {
                if (!await PrerequisitesAsync(context, outbox, current, input, ct)) return;
                if (input.Replaces is { } old)
                {
                    var previous = await context.CommentDrafts.SingleOrDefaultAsync(row => row.Id == old.Id, ct);
                    if (previous is null || previous.Status != CommentStatus.Pending || !CommentSources.Matches(previous, old))
                    {
                        Stop(context, current, "черновик изменился во время подготовки новых вариантов", true);
                        return;
                    }
                    previous.Status = CommentStatus.Expired;
                    previous.Revision++;
                    previous.PostId = TextUtil.Truncate(previous.PostId, 100) + "#expired-" + previous.Id;
                    previous.Reason = "заменён новыми вариантами";
                    var cards = await context.Notifications.Where(row => row.Kind == NotificationKind.CommentDraft && row.EntityId == previous.Id).ToListAsync(ct);
                    foreach (var card in cards)
                    {
                        if (card.Status == NotificationStatus.Pending)
                        {
                            card.Status = NotificationStatus.Failed;
                            card.Error = "comment draft retired before notification delivery";
                        }
                        if (card.TelegramMessageId is { } messageId)
                            context.Notifications.Add(new Notification
                            {
                                Kind = NotificationKind.Info, Text = "Устаревшая карточка комментария", Status = NotificationStatus.Pending,
                                CreatedAt = time.GetUtcNow(), EditKeyboardMessageId = messageId, EditParentNotificationId = card.Id,
                                Buttons = [["♻️ Устарело — новые варианты ниже|noop:0:0"]], EntityId = previous.Id,
                            });
                    }
                    await notifier.SaveJoinedAsync(context, outbox, ct);
                }
                else if (await context.CommentDrafts.AnyAsync(row => row.PostId == input.PostId, ct))
                {
                    Stop(context, current, "пост уже обработан", true);
                    return;
                }
                var latest = (await policies.ReadAsync(ct)).Value;
                if (input.Replaces is null)
                {
                    var today = await context.CommentDrafts.CountAsync(CommentPublicationRules.CountsTowardDailyLimit(clock.StartOfTodayUtc(), input.Policy.AutoPublish), ct);
                    var cooldown = time.GetUtcNow().AddDays(-Math.Max(input.Policy.AuthorCooldownDays, latest.AuthorCooldownDays));
                    var author = input.Preparation.Author.ToLowerInvariant();
                    if (today >= Math.Min(input.Policy.MaxDraftsPerDay, latest.MaxDraftsPerDay)
                        || await context.CommentDrafts.AnyAsync(row => row.Author.ToLower() == author && row.CreatedAt >= cooldown && row.Status != CommentStatus.Rejected, ct))
                    {
                        Stop(context, current, "дневной лимит или пауза автора изменились", true);
                        return;
                    }
                }
                var draft = new CommentDraft
                {
                    SourceRequestId = id, PostId = input.PostId, PostUrl = input.PostUrl, Author = input.Preparation.Author,
                    PostText = input.Preparation.PostText, PostPublishedAt = input.PublishedAt, Audience = CommentPublicationRules.Audience(result.Plan.Audience),
                    Variants = [.. result.Plan.Variants], Reason = result.Plan.Reason, CreatedAt = time.GetUtcNow(),
                };
                CommentPublicationRules.Decide(draft, result.Plan.WorthCommenting, input.Replaces is null && input.Policy.AutoPublish && latest.AutoPublish, time.GetUtcNow());
                context.CommentDrafts.Add(draft);
                await notifier.SaveJoinedAsync(context, outbox, ct);
                if (draft.Status == CommentStatus.Pending)
                {
                    var (text, buttons) = Cards.CommentDraft(draft);
                    notifier.Add(context, NotificationKind.CommentDraft, text, buttons, draft.Id);
                }
                else if (draft.Status == CommentStatus.Approved) await deliveries.QueueAsync(context, outbox, draft, ct, automatic: true, expiresAt: input.ExpiresAt);
                current.DraftId = draft.Id;
                current.Stage = CommentSourceStage.Completed;
                current.CompletedAt = time.GetUtcNow();
            }, ct);
        }
        catch (UnsupportedCommentSourceException)
        {
            await StopAsync(request, "unsupported comment source checkpoint", false, ct);
            throw;
        }
        catch (UnsupportedCommentPreparationException)
        {
            await StopAsync(request, "unsupported comment preparation checkpoint", false, ct);
            throw new UnsupportedCommentSourceException();
        }
        catch (JsonException)
        {
            await StopAsync(request, "corrupted comment source checkpoint", false, ct);
            throw new UnsupportedCommentSourceException();
        }
    }

    private async Task<bool> PrerequisitesAsync(JobFinderDbContext context, IDbContextOutbox outbox, CommentSourceRequest current, CommentSourceInput input, CancellationToken ct)
    {
        await context.Database.ExecuteSqlRawAsync("SELECT 1 FROM app_documents WHERE key IN ('candidate.profile', 'policy.automation', 'policy.engagement') FOR SHARE", ct);
        if (await settings.GetBoolAsync(RuntimeSettings.CommentsPaused, false, ct))
        {
            await ContinueAsync(outbox, current, time.GetUtcNow().AddMinutes(1));
            return false;
        }
        var profile = await context.Documents.SingleOrDefaultAsync(row => row.Key == ProfileProvider.Key, ct);
        if (profile is null || profile.Version != input.ProfileVersion
            || CanonicalJson.Hash(ProfileProvider.ToJson(ProfileProvider.FromJson(profile.Content))) != input.ProfileHash)
        {
            Stop(context, current, "профиль изменился во время подготовки комментария", true);
            return false;
        }
        if (input.ExpiresAt <= time.GetUtcNow())
        {
            Stop(context, current, "пост устарел во время подготовки комментария", true);
            return false;
        }
        if (input.Replaces is { } old)
        {
            var previous = await context.CommentDrafts.SingleOrDefaultAsync(row => row.Id == old.Id, ct);
            if (previous is null || previous.Status != CommentStatus.Pending || !CommentSources.Matches(previous, old))
            {
                Stop(context, current, "черновик изменился во время подготовки новых вариантов", true);
                return false;
            }
        }
        return true;
    }

    private async Task ChangeAsync(CommentSourceRequest before, Func<JobFinderDbContext, IDbContextOutbox, CommentSourceRequest, Task> change, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await CommentSources.LockAsync(context, ct);
        ownership.EnsureOwned();
        var current = await context.CommentSourceRequests.SingleAsync(row => row.Id == before.Id, ct);
        if (current.Stage != before.Stage || current.ClaimEnvelopeId != before.ClaimEnvelopeId) return;
        current.ClaimEnvelopeId = null;
        current.RetryAt = null;
        await change(context, outbox, current);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private static Task ContinueAsync(IDbContextOutbox outbox, CommentSourceRequest request, DateTimeOffset at)
    {
        request.RetryAt = at;
        return outbox.PublishAsync(new PrepareCommentSource { RequestId = request.Id }, new DeliveryOptions { ScheduledTime = at }).AsTask();
    }

    private Task StopAsync(CommentSourceRequest before, string reason, bool cancelled, CancellationToken ct) =>
        ChangeAsync(before, (context, _, current) => { Stop(context, current, reason, cancelled); return Task.CompletedTask; }, ct);

    private void Stop(JobFinderDbContext context, CommentSourceRequest request, string reason, bool cancelled)
    {
        request.Stage = cancelled ? CommentSourceStage.Cancelled : CommentSourceStage.Failed;
        request.Error = reason;
        request.CompletedAt = time.GetUtcNow();
        notifier.Add(context, NotificationKind.Alert, "⚠️ Комментарий не обновлён: " + TextUtil.EscapeHtml(reason));
    }
}

[NonTransactional]
public static class CommentSourceHandler
{
    public static Task Handle(PrepareCommentSource command, CommentSourceWorkflow workflow, IMessageContext message, CancellationToken ct) =>
        workflow.RunAsync(command.RequestId, command.Version, message.Envelope?.Id ?? throw new UnsupportedCommentSourceException(), ct);
}
