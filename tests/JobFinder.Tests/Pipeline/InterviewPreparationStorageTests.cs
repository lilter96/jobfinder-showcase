using JobFinder.Core.Domain;
using JobFinder.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace JobFinder.Tests.Pipeline;

public sealed class InterviewPreparationStorageTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Checkpoints_roundtrip_as_jsonb_and_duplicate_source_generation_is_rejected()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var request = new InterviewPreparationRequest
        {
            Id = Guid.NewGuid(), InterviewId = 10, Revision = 7, Stage = InterviewPreparationStage.Planning, SeedJson = "{\"version\":1}", SeedHash = new string('a', 64),
            InputJson = "{\"version\":1}", InputHash = new string('b', 64), PlanJson = "{\"plan\":{}}", PlanHash = new string('c', 64),
            CopilotJson = "{\"role\":\"Backend\"}", CopilotHash = new string('d', 64), PdfHash = new string('e', 64), CreatedAt = DateTimeOffset.UtcNow,
        };
        context.InterviewPreparationRequests.Add(request);
        await context.SaveChangesAsync(Ct);
        try
        {
            var restored = await context.InterviewPreparationRequests.AsNoTracking().SingleAsync(row => row.Id == request.Id, Ct);
            Assert.Equal(InterviewPreparationStage.Planning, restored.Stage);
            Assert.Equal(request.PlanHash, restored.PlanHash);
            var types = await context.Database.SqlQuery<string>($"SELECT data_type AS \"Value\" FROM information_schema.columns WHERE table_name = 'interview_preparation_requests' AND column_name IN ('seed_json', 'input_json', 'plan_json', 'copilot_json')").ToListAsync(Ct);
            Assert.Equal(4, types.Count);
            Assert.All(types, type => Assert.Equal("jsonb", type));
            context.InterviewPreparationRequests.Add(new() { Id = Guid.NewGuid(), InterviewId = request.InterviewId, Revision = request.Revision, CreatedAt = DateTimeOffset.UtcNow });
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }
        finally { await context.InterviewPreparationRequests.Where(row => row.Id == request.Id).ExecuteDeleteAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData("root")]
    [InlineData("marker")]
    [InlineData("revision")]
    [InlineData("envelope-alias")]
    [InlineData("envelope-type")]
    public async Task Guarded_rollback_refuses_saved_generations_source_fences_and_orphaned_envelopes(string evidence)
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var envelopeId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var vacancy = new Vacancy { Title = "Synthetic", Url = "https://vacancy.invalid/1", Fingerprint = Guid.NewGuid().ToString("N"), FirstSeenAt = DateTimeOffset.UtcNow };
        var application = new Application { Vacancy = vacancy, CreatedAt = DateTimeOffset.UtcNow };
        var interview = new Interview { Application = application, CreatedAt = DateTimeOffset.UtcNow };
        context.Interviews.Add(interview);
        await context.SaveChangesAsync(Ct);
        try
        {
            if (evidence == "root") context.InterviewPreparationRequests.Add(new() { Id = requestId, InterviewId = interview.Id, CreatedAt = DateTimeOffset.UtcNow });
            if (evidence == "marker") interview.PreparationRequestId = requestId;
            if (evidence == "revision") interview.PreparationRevision = 1;
            await context.SaveChangesAsync(Ct);
            if (evidence.StartsWith("envelope", StringComparison.Ordinal))
            {
                var type = evidence == "envelope-alias" ? "jobfinder.preparation.interview.v1" : "JobFinder.Pipeline.Modules.Preparation.PrepareInterview";
                await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO jobfinder_queues.wolverine_queue_preparation_scheduled (id, body, message_type, execution_time) VALUES ({envelopeId}, {Array.Empty<byte>()}, {type}, {DateTimeOffset.UtcNow.AddDays(1)})", Ct);
            }
            var error = await Assert.ThrowsAsync<PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261001120111_DurablePostProcessing", Ct));
            Assert.Contains("Cannot roll back interview preparation", error.MessageText);
        }
        finally
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM jobfinder_queues.wolverine_queue_preparation_scheduled WHERE id = {envelopeId}", CancellationToken.None);
            await context.InterviewPreparationRequests.Where(row => row.Id == requestId).ExecuteDeleteAsync(CancellationToken.None);
            await context.Interviews.Where(row => row.Id == interview.Id).ExecuteDeleteAsync(CancellationToken.None);
            await context.Applications.Where(row => row.Id == application.Id).ExecuteDeleteAsync(CancellationToken.None);
            await context.Vacancies.Where(row => row.Id == vacancy.Id).ExecuteDeleteAsync(CancellationToken.None);
            await context.Database.MigrateAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task An_unused_schema_can_be_downgraded_and_reapplied_without_pending_model_changes()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        try { await context.GetService<IMigrator>().MigrateAsync("20261001120111_DurablePostProcessing", Ct); }
        finally { await context.Database.MigrateAsync(CancellationToken.None); }
        Assert.False(context.Database.HasPendingModelChanges());
    }
}
