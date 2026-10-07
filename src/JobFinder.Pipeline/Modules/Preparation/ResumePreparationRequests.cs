using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Preparation;

public sealed class ResumePreparationRequests(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes, TimeProvider time)
{
    internal static Task LockAsync(JobFinderDbContext context, long vacancyId, CancellationToken ct) =>
        context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"jobfinder.preparation.resume." + vacancyId.ToString(System.Globalization.CultureInfo.InvariantCulture)}, 0))", ct);

    public async Task<Guid> EnqueueAsync(ResumePreparationInput input, CancellationToken ct, bool force = false)
    {
        if (input.SchemaVersion != 1 || input.Vacancy.Id <= 0 || input.Operation.OperationId == Guid.Empty
            || input.Recipe.OperationId != input.Operation.OperationId || string.IsNullOrWhiteSpace(input.ResumeText))
            throw new ArgumentException("invalid resume request inputs");
        var json = JsonSerializer.Serialize(input, ResumePreparationJsonContext.Default.ResumePreparationInput);
        var hash = input.Recipe.Hash;
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await LockAsync(context, input.Vacancy.Id, ct);
        var sameOperation = await context.ResumePreparationRequests.SingleOrDefaultAsync(row => row.Id == input.Operation.OperationId, ct);
        if (sameOperation is not null)
        {
            if (sameOperation.InputHash != hash || sameOperation.VacancyId != input.Vacancy.Id)
                throw new ArgumentException("resume operation identity was reused with different inputs");
            return sameOperation.Id;
        }
        var active = await context.ResumePreparationRequests.SingleOrDefaultAsync(row => row.VacancyId == input.Vacancy.Id &&
            (row.Stage == ResumePreparationStage.Drafting || row.Stage == ResumePreparationStage.Printing
                || row.Stage == ResumePreparationStage.Reviewing || row.Stage == ResumePreparationStage.Finalizing), ct);
        if (active?.InputHash == hash) return active.Id;
        if (active is not null)
        {
            active.Stage = ResumePreparationStage.Cancelled;
            active.CompletedAt = time.GetUtcNow();
            await context.SaveChangesAsync(ct);
        }
        if (!force)
        {
            var completed = await context.ResumePreparationRequests.Where(row => row.VacancyId == input.Vacancy.Id && row.InputHash == hash
                && row.Stage == ResumePreparationStage.Completed).OrderByDescending(row => row.CompletedAt).FirstOrDefaultAsync(ct);
            if (completed?.ResultId is { } resultId && await context.TailoredResumes.SingleOrDefaultAsync(row => row.Id == resultId, ct) is { } result)
            {
                var report = result.ReportJson is null ? null : JsonSerializer.Deserialize(result.ReportJson, Core.Resume.ResumeJsonContext.Default.ResumeReport);
                if (Documents.ResumeTailor.Current(result) || result.CreatedAt > time.GetUtcNow().AddHours(-1) && report is { Review: not null, Ready: false })
                {
                    await outbox.SaveChangesAndFlushMessagesAsync(ct);
                    return completed.Id;
                }
                if (report is not null && Documents.ResumeTailor.Ready(report) && completed.BestJson is { } bestJson)
                {
                    var best = JsonSerializer.Deserialize(bestJson, ResumePreparationJsonContext.Default.ResumeRound)
                        ?? throw new JsonException("missing reviewed resume checkpoint");
                    best.Number = completed.Round;
                    best.PdfPath = null;
                    best.PdfHash = null;
                    best.Review = null;
                    completed.CurrentJson = JsonSerializer.Serialize(best, ResumePreparationJsonContext.Default.ResumeRound);
                    completed.BestJson = null;
                    completed.ResultId = null;
                    completed.Stage = ResumePreparationStage.Printing;
                    completed.ClaimEnvelopeId = null;
                    completed.RetryAt = null;
                    completed.CompletedAt = null;
                    completed.Failures = 0;
                    completed.Error = null;
                    await outbox.PublishAsync(new PrintResume { RequestId = completed.Id, Round = completed.Round });
                    await outbox.SaveChangesAndFlushMessagesAsync(ct);
                    return completed.Id;
                }
            }
        }
        var request = new ResumePreparationRequest
        {
            Id = input.Operation.OperationId, VacancyId = input.Vacancy.Id, InputHash = hash, InputJson = json,
            Stage = ResumePreparationStage.Drafting, CreatedAt = time.GetUtcNow(),
        };
        context.ResumePreparationRequests.Add(request);
        await outbox.PublishAsync(new DraftResume { RequestId = request.Id });
        await outbox.SaveChangesAndFlushMessagesAsync(ct);
        return request.Id;
    }

    public async Task<TailoredResume> WaitAsync(Guid id, CancellationToken ct)
    {
        while (true)
        {
            await using var context = await db.CreateDbContextAsync(ct);
            var request = await context.ResumePreparationRequests.AsNoTracking().SingleAsync(row => row.Id == id, ct);
            if (request.ResultId is { } result) return await context.TailoredResumes.AsNoTracking().SingleAsync(row => row.Id == result, ct);
            if (request.Stage == ResumePreparationStage.Cancelled) throw new ResumePreparationSupersededException();
            if (request.Stage == ResumePreparationStage.Failed) throw new InvalidOperationException(request.Error ?? "resume preparation failed");
            await Task.Delay(TimeSpan.FromMilliseconds(500), time, ct);
        }
    }

    public async Task<bool> CancelAsync(Guid id, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var known = await context.ResumePreparationRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct);
        if (known is null) return false;
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockAsync(context, known.VacancyId, ct);
        var request = await context.ResumePreparationRequests.SingleAsync(row => row.Id == id, ct);
        if (request.Stage is ResumePreparationStage.Completed or ResumePreparationStage.Cancelled or ResumePreparationStage.Failed) return false;
        request.Stage = ResumePreparationStage.Cancelled;
        request.CompletedAt = time.GetUtcNow();
        request.RetryAt = null;
        request.ClaimEnvelopeId = null;
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}
