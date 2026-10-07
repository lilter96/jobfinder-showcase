using System.Collections.Concurrent;
using System.Data.Common;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Candidate;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableLetterPreparationIntegrationTests
{
    private const string ExactText = "Synthetic approved text, with its exact paragraphs.\n\nNo rewrite is authorized.";

    private Task<LetterReviewTicket> ExactAsync(IHost host, Vacancy vacancy, string text = ExactText, LetterBrief? brief = null) =>
        host.Services.GetRequiredService<LetterWriter>().RequestReviewAsync(brief ?? Brief(vacancy), text, Ct);

    [Fact]
    public async Task Exact_review_producer_exit_recovers_one_review_without_any_writer_call()
    {
        var steps = new Steps();
        var producer = await HostAsync(false, steps);
        var vacancy = await SeedAsync();
        var ticket = await ExactAsync(producer, vacancy);
        var id = ticket.RequestId!.Value;
        var input = LetterPreparationRequests.ReadInput(await RowAsync(id));
        Assert.Equal(2, input.Version);
        Assert.Equal(ExactText, input.ExactText);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            var lane = Assert.Single(await context.LetterPreparationLanes.ToListAsync(Ct));
            Assert.Equal(LetterLaneStage.Reviewing, lane.Stage);
            Assert.Equal(ExactText, LetterPreparationRequests.ReadCandidate(lane.DraftJson).Text);
        }
        await StopAsync(producer);
        var consumer = await HostAsync(true, steps);
        var result = await CompletedAsync(id);
        Assert.Equal(ExactText, result.Text);
        Assert.Equal(0, steps.Drafts);
        Assert.Equal(1, steps.Reviews);
        var review = await consumer.Services.GetRequiredService<LetterWriter>().GetReviewAsync(id, Ct);
        Assert.Equal(input.ReviewContextHash, review!.ContextHash);
        Assert.Equal(Fingerprint.Of(ExactText), review.TextHash);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    public async Task Exact_review_returns_the_single_verdict_without_grace_or_rewriting(int score)
    {
        var steps = new Steps { Score = score };
        var host = await HostAsync(true, steps);
        var ticket = await ExactAsync(host, await SeedAsync());
        var result = await CompletedAsync(ticket.RequestId!.Value);
        Assert.Equal(score, result.Score);
        Assert.Equal(ExactText, result.Text);
        Assert.Equal(1, steps.Reviews);
        Assert.Equal(0, steps.Drafts);
        Assert.Equal(0, steps.Revisions);
        Assert.Null((await RowAsync(ticket.RequestId.Value)).GraceUntil);
        Assert.Equal(1, (await RowAsync(ticket.RequestId.Value)).Round);
        Assert.Equal(score >= 9, LetterPreparationWorkflow.Ready(result));
    }

    [Fact]
    public async Task Exact_review_duplicate_requests_and_envelopes_share_one_paid_claim()
    {
        var hold = Barrier();
        var steps = new Steps { ReviewHold = hold };
        var host = await HostAsync(true, steps);
        var vacancy = await SeedAsync();
        var tickets = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => ExactAsync(host, vacancy)));
        var id = tickets[0].RequestId!.Value;
        Assert.All(tickets, ticket => Assert.Equal(id, ticket.RequestId));
        await hold.Entered.Task.WaitAsync(Timeout, Ct);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            var lane = await context.LetterPreparationLanes.SingleAsync(row => row.RequestId == id, Ct);
            await host.Services.GetRequiredService<IMessageBus>().PublishAsync(new ReviewLetterLane { RequestId = id, LaneId = lane.Id });
            await host.Services.GetRequiredService<IMessageBus>().PublishAsync(new ReviewLetterLane { RequestId = id, LaneId = lane.Id });
        }
        hold.Release.TrySetResult();
        await CompletedAsync(id);
        Assert.Equal(1, steps.Reviews);
        Assert.Equal(0, steps.Drafts);
    }

    [Fact]
    public async Task Exact_review_warm_reuse_and_text_or_context_change_have_distinct_recipes()
    {
        var steps = new Steps();
        var host = await HostAsync(true, steps);
        var vacancy = await SeedAsync();
        var original = await ExactAsync(host, vacancy);
        await CompletedAsync(original.RequestId!.Value);
        var warm = await ExactAsync(host, vacancy);
        Assert.Equal(original.RequestId, warm.RequestId);
        Assert.NotNull(warm.Review);
        Assert.Equal(1, steps.Reviews);
        var edited = await ExactAsync(host, vacancy, ExactText + " One precise correction.");
        await CompletedAsync(edited.RequestId!.Value);
        Assert.NotEqual(original.RequestId, edited.RequestId);
        var context = await ExactAsync(host, vacancy, brief: Brief(vacancy) with { Conversation = "An updated synthetic conversation." });
        await CompletedAsync(context.RequestId!.Value);
        Assert.NotEqual(original.RequestId, context.RequestId);
        Assert.Equal(3, steps.Reviews);
        Assert.Equal(0, steps.Drafts);
    }

    [Fact]
    public async Task Exact_review_checkpoint_fault_reuses_the_paid_result_without_a_second_review()
    {
        var steps = new Steps();
        var fault = new FailCommit("review");
        var host = await HostAsync(true, steps, fault);
        var ticket = await ExactAsync(host, await SeedAsync());
        await CompletedAsync(ticket.RequestId!.Value);
        Assert.True(fault.Fired);
        Assert.Equal(1, steps.Reviews);
        Assert.Equal(0, steps.Drafts);
    }

    [Fact]
    public async Task Exact_review_outage_restarts_review_only_and_preserves_exact_text()
    {
        var steps = new Steps { ReviewUnavailable = true };
        var host = await HostAsync(true, steps);
        var ticket = await ExactAsync(host, await SeedAsync());
        var id = ticket.RequestId!.Value;
        await UntilAsync(async () =>
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            return await context.LetterPreparationLanes.AnyAsync(row => row.RequestId == id && row.RetryAt != null, Ct);
        });
        Assert.Null(await host.Services.GetRequiredService<LetterWriter>().GetReviewAsync(id, Ct));
        await StopAsync(host);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            await context.LetterPreparationLanes.Where(row => row.RequestId == id).ExecuteUpdateAsync(update => update.SetProperty(row => row.RetryAt, (DateTimeOffset?)null), Ct);
        steps.ReviewUnavailable = false;
        host = await HostAsync(true, steps);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            var lane = await context.LetterPreparationLanes.SingleAsync(row => row.RequestId == id, Ct);
            await host.Services.GetRequiredService<IMessageBus>().PublishAsync(new ReviewLetterLane { RequestId = id, LaneId = lane.Id });
        }
        Assert.Equal(ExactText, (await CompletedAsync(id)).Text);
        Assert.Equal(0, steps.Drafts);
        Assert.Equal(2, steps.Reviews);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("candidate")]
    public async Task Exact_review_stale_inputs_are_cancelled_before_paid_work(string changed)
    {
        var steps = new Steps();
        var producer = await HostAsync(false, steps);
        var vacancy = await SeedAsync();
        var ticket = await ExactAsync(producer, vacancy);
        if (changed == "source")
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            await context.Vacancies.Where(row => row.Id == vacancy.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.Description, "Different synthetic service."), Ct);
        }
        else
        {
            var profile = (await producer.Services.GetRequiredService<ProfileProvider>().GetSnapshotAsync(Ct))!;
            profile.Value.Summary += " New candidate evidence.";
            await producer.Services.GetRequiredService<ICandidatePublisher>().PublishProfileAsync(profile.Value, profile.Version, "synthetic-test", Ct);
        }
        await StopAsync(producer);
        await HostAsync(true, steps);
        await UntilAsync(async () => (await RowAsync(ticket.RequestId!.Value)).Stage == LetterPreparationStage.Cancelled);
        Assert.Equal(0, steps.Reviews);
        Assert.Equal(0, steps.Drafts);
    }

    [Fact]
    public async Task Exact_review_dependency_cannot_certify_another_text_or_contact_context()
    {
        var host = await HostAsync(true, new Steps());
        var vacancy = await SeedAsync();
        var ticket = await ExactAsync(host, vacancy);
        await CompletedAsync(ticket.RequestId!.Value);
        var writer = host.Services.GetRequiredService<LetterWriter>();
        Assert.NotNull(await writer.GetReviewAsync(ticket.RequestId.Value, Brief(vacancy), ExactText, Ct));
        await Assert.ThrowsAsync<LetterPreparationSupersededException>(() => writer.GetReviewAsync(ticket.RequestId.Value, Brief(vacancy), "A different approved text.", Ct));
        await Assert.ThrowsAsync<LetterPreparationSupersededException>(() => writer.GetReviewAsync(ticket.RequestId.Value,
            Brief(vacancy) with { Contact = "A different recruiter." }, ExactText, Ct));
    }

    [Fact]
    public async Task Exact_review_damaged_proof_is_not_returned_as_a_verified_report()
    {
        var host = await HostAsync(true, new Steps());
        var ticket = await ExactAsync(host, await SeedAsync());
        var candidate = await CompletedAsync(ticket.RequestId!.Value);
        candidate.Problems.Add("synthetic persisted result is not sendable");
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            await context.LetterPreparationRequests.Where(row => row.Id == ticket.RequestId).ExecuteUpdateAsync(update =>
                update.SetProperty(row => row.ResultJson, LetterPreparationRequests.SerializeCandidate(candidate)), Ct);
        await Assert.ThrowsAsync<LetterPreparationFailedException>(() => host.Services.GetRequiredService<LetterWriter>().GetReviewAsync(ticket.RequestId.Value, Ct));
    }

    [Fact]
    public async Task Exact_review_waiter_cancellation_does_not_cancel_the_saved_review_request()
    {
        var producer = await HostAsync(false, new Steps());
        var vacancy = await SeedAsync();
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var waiting = producer.Services.GetRequiredService<LetterWriter>().ReviewMessageAsync(Brief(vacancy), ExactText, cancelled.Token);
        await UntilAsync(async () =>
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            return await context.LetterPreparationRequests.AnyAsync(Ct);
        });
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Guid id;
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            id = (await context.LetterPreparationRequests.SingleAsync(Ct)).Id;
        Assert.Equal(LetterPreparationStage.Active, (await RowAsync(id)).Stage);
        await StopAsync(producer);
        var steps = new Steps();
        await HostAsync(true, steps);
        await CompletedAsync(id);
        Assert.Equal(1, steps.Reviews);
        Assert.Equal(0, steps.Drafts);
    }

    [Fact]
    public async Task Exact_review_guarded_downgrade_keeps_pending_reviews_and_native_envelopes()
    {
        var producer = await HostAsync(false, new Steps());
        var ticket = await ExactAsync(producer, await SeedAsync());
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        try
        {
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20260930193511_AtomicVacancyCapture", Ct));
            Assert.Equal(LetterPreparationStage.Active, (await RowAsync(ticket.RequestId!.Value)).Stage);
            Assert.Contains("20260930203129_EmployerReviewDependency", await context.Database.GetAppliedMigrationsAsync(Ct));
        }
        finally { await context.Database.MigrateAsync(CancellationToken.None); }
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(Ct));
    }

    [Fact]
    public async Task Exact_review_malformed_new_mode_is_quarantined_instead_of_rewritten()
    {
        var steps = new Steps();
        var producer = await HostAsync(false, steps);
        var ticket = await ExactAsync(producer, await SeedAsync());
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            var request = await context.LetterPreparationRequests.SingleAsync(row => row.Id == ticket.RequestId, Ct);
            var input = LetterPreparationRequests.ReadInput(request);
            input.Version = 1;
            request.InputJson = LetterPreparationRequests.SerializeInput(input);
            request.InputHash = CanonicalJson.Hash(request.InputJson);
            await context.SaveChangesAsync(Ct);
        }
        await StopAsync(producer);
        var host = await HostAsync(true, steps);
        await UntilAsync(async () => (await RowAsync(ticket.RequestId!.Value)).Stage == LetterPreparationStage.Failed);
        await UntilAsync(async () =>
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            return await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_messages.wolverine_dead_letters").SingleAsync(Ct) == 1;
        });
        Assert.Equal(0, steps.Drafts);
        Assert.Equal(0, steps.Reviews);
        await Assert.ThrowsAsync<LetterPreparationFailedException>(() => host.Services.GetRequiredService<LetterWriter>().GetReviewAsync(ticket.RequestId!.Value, Ct));
    }

    [Fact]
    public async Task Exact_review_parent_checkpoint_failure_reuses_the_committed_child_review()
    {
        var hold = Barrier();
        var steps = new Steps { ReviewHold = hold };
        var recipient = new ExactRecipient();
        var fault = new ExactParentCommitFault();
        var host = await HostAsync(true, steps, fault, recipient);
        var vacancy = await SeedAsync();
        var contact = new CompanyContact
        {
            CompanyKey = VacancyFingerprint.NormalizeCompany(vacancy.CompanyName!), Kind = ContactKind.Telegram,
            Value = "@synthetic_checkpoint", PersonName = "Synthetic HR", Source = "synthetic-test",
        };
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            context.CompanyContacts.Add(contact);
            await context.SaveChangesAsync(Ct);
        }
        var pending = host.Services.GetRequiredService<EmployerMessages>().RecruiterAsync(vacancy.Id, contact.Id, ExactText, false, true, null, Ct);
        await UntilAsync(() => Task.FromResult(fault.Fired));
        await hold.Entered.Task.WaitAsync(Timeout, Ct);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            Assert.Single(await context.LetterPreparationRequests.ToListAsync(Ct));
            Assert.Null((await context.EmployerMessages.SingleAsync(Ct)).LetterReviewRequestId);
        }
        hold.Release.TrySetResult();
        var result = await pending.WaitAsync(Timeout, Ct);
        Assert.True(result.Ok, result.Message);
        Assert.Single(recipient.Deliveries);
        Assert.Equal(1, steps.Reviews);
        Assert.Equal(0, steps.Drafts);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await read.LetterPreparationRequests.ToListAsync(Ct));
        Assert.NotNull((await read.EmployerMessages.SingleAsync(Ct)).LetterReviewRequestId);
    }

    [Fact]
    public async Task Exact_review_parent_checkpoints_dependency_while_slow_review_does_not_block_delivery()
    {
        var hold = Barrier();
        var steps = new Steps { ReviewHold = hold };
        var recipient = new ExactRecipient();
        var host = await HostAsync(true, steps, recipient: recipient);
        var vacancy = await SeedAsync();
        var contact = new CompanyContact
        {
            CompanyKey = VacancyFingerprint.NormalizeCompany(vacancy.CompanyName!), Kind = ContactKind.Telegram,
            Value = "@synthetic_recruiter", PersonName = "Synthetic HR", Source = "synthetic-test",
        };
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            context.CompanyContacts.Add(contact);
            await context.SaveChangesAsync(Ct);
        }
        var pending = host.Services.GetRequiredService<EmployerMessages>().RecruiterAsync(vacancy.Id, contact.Id, ExactText, false, true, null, Ct);
        await hold.Entered.Task.WaitAsync(Timeout, Ct);
        await UntilAsync(async () =>
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            return await context.EmployerMessages.AnyAsync(row => row.VacancyId == vacancy.Id && row.LetterReviewRequestId != null && row.ClaimEnvelopeId == null, Ct);
        });
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            var message = await context.EmployerMessages.SingleAsync(Ct);
            Assert.Equal(EmployerMessageStatus.Pending, message.Status);
            Assert.Null(message.Review);
            Assert.NotNull(message.RetryAt);
        }
        Assert.Empty(recipient.Deliveries);
        hold.Release.TrySetResult();
        var result = await pending.WaitAsync(Timeout, Ct);
        Assert.True(result.Ok, result.Message);
        var delivered = Assert.Single(recipient.Deliveries);
        Assert.Equal(ExactText, delivered.Text);
        Assert.Equal(1, steps.Reviews);
        Assert.Equal(0, steps.Drafts);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(EmployerMessageStatus.Sent, (await read.EmployerMessages.SingleAsync(Ct)).Status);
        Assert.Equal(DeliveryAttemptStatus.Confirmed, (await read.DeliveryAttempts.SingleAsync(Ct)).Status);
    }

    private sealed class ExactRecipient : IDeliveryTransport
    {
        public ConcurrentQueue<ApprovedDispatch> Deliveries { get; } = new();
        public Task<DeliveryReceipt> SendAsync(ApprovedDispatch dispatch, CancellationToken ct)
        {
            Deliveries.Enqueue(dispatch);
            return Task.FromResult(new DeliveryReceipt(ApplicationStatus.Sent, TargetPeerId: 123));
        }
    }

    private sealed class ExactParentCommitFault : DbTransactionInterceptor
    {
        private int _armed = 1;
        public bool Fired { get; private set; }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken ct = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<EmployerMessage>().Any(entry => entry.Entity.LetterReviewRequestId is not null
                    && entry.Entity.Status == EmployerMessageStatus.Pending) == true && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Fired = true;
                throw new IOException("synthetic parent review dependency commit failure");
            }
            return ValueTask.FromResult(result);
        }
    }
}
