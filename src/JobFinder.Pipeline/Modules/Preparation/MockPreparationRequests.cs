using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Pipeline.Career;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Preparation;

public sealed class MockPreparationRequests(IDbContextFactory<JobFinderDbContext> db, TimeProvider time)
{
    public async Task<(MockPreparationInput Input, MockPreparationResult Result)> WaitAsync(Guid id, CancellationToken ct)
    {
        while (true)
        {
            await using var context = await db.CreateDbContextAsync(ct);
            var root = await context.MockPreparationRequests.AsNoTracking().SingleAsync(row => row.Id == id, ct);
            var input = ReadInput(root);
            if (root.Stage == MockPreparationStage.Completed) return (input, ReadResult(root));
            if (root.Stage is MockPreparationStage.Cancelled or MockPreparationStage.Failed) throw new InvalidOperationException(root.Error ?? "mock preparation stopped");
            await Task.Delay(TimeSpan.FromMilliseconds(250), time, ct);
        }
    }

    internal static MockPreparationRequest Root(MockPreparationInput input, string key)
    {
        var json = JsonSerializer.Serialize(input, MockPreparationJsonContext.Default.MockPreparationInput);
        var root = new MockPreparationRequest { Id = input.Operation.OperationId, SessionId = input.Source.Id, BusinessKey = key,
            InputJson = json, InputHash = CanonicalJson.Hash(json), Stage = MockPreparationStage.Preparing, CreatedAt = input.CapturedAt };
        ReadInput(root);
        return root;
    }

    internal static MockPreparationInput ReadInput(MockPreparationRequest root)
    {
        if (CanonicalJson.Hash(root.InputJson) != root.InputHash) throw new UnsupportedMockPreparationException();
        var input = JsonSerializer.Deserialize(root.InputJson, MockPreparationJsonContext.Default.MockPreparationInput) ?? throw new UnsupportedMockPreparationException();
        if (root.Id == Guid.Empty || root.SessionId <= 0 || string.IsNullOrWhiteSpace(root.BusinessKey) || !Enum.IsDefined(root.Stage) || root.Failures < 0
            || input.Version != 1 || !Enum.IsDefined(input.Kind) || input.CapturedAt == default || input.Source is null
            || input.Source.Id != root.SessionId || input.Source.Revision <= 0 || input.Source.PreparationRequestId != root.Id || input.Source.Status != MockStatus.Active
            || input.Source.CreatedAt == default || input.Source.FinishedAt is not null || input.Source.TurnsJson is null || !Enum.IsDefined(input.Source.Kind)
            || input.Waiting is null || !MockSources.ValidDialogue(input.Waiting) || input.Waiting.EntityId != root.SessionId || input.Waiting.Step != input.Source.Current
            || input.Waiting.MockPending != true || input.Waiting.MockRequestId != root.Id
            || input.Operation is null || input.Operation.SchemaVersion != 1 || input.Operation.OperationId != root.Id || input.Operation.Policy is null
            || input.Recipe is null || input.Recipe.Version != 1 || input.Recipe.OperationId != root.Id || input.Recipe.Tasks is not { Count: 1 }
            || input.Recipe.Tasks.Any(task => task is null) || input.Recipe.Inputs is null || input.Recipe.Rules is null || input.Prefix is null)
            throw new UnsupportedMockPreparationException();
        var source = new MockSession { Status = input.Source.Status, Kind = input.Source.Kind, TurnsJson = input.Source.TurnsJson,
            Current = input.Source.Current, Revision = input.Source.Revision };
        var turns = MockSources.ValidTurns(source);
        if (input.Kind == MockPreparationKind.Questions)
        {
            if (input.Start is not { IntentId: var intent, Profile: not null, Turns.Count: 0, ProfileVersion: > 0, FactsVersion: >= 0, Stories: not null, Header: not null }
                || intent == Guid.Empty || input.Start.Hr || input.Start.InterviewId is not null || input.Start.Kind != input.Source.Kind
                || input.Start.Vacancy?.Id != input.Source.VacancyId || turns.Count != 0 || source.Current != 0 || input.Answer is not null || input.Learning is not null)
                throw new UnsupportedMockPreparationException();
        }
        else if (input.Kind == MockPreparationKind.Review)
        {
            if (input.Start is not null || string.IsNullOrWhiteSpace(input.Answer) || input.Answer != input.Answer.Trim() || source.Current >= turns.Count
                || turns[source.Current].Answer != input.Answer || turns[source.Current].Score is not null || turns[source.Current].Feedback is not null
                || input.Waiting.Subject != turns[source.Current].Question) throw new UnsupportedMockPreparationException();
            if (input.Learning is { } item && (!InterviewProgress.TryParse(turns[source.Current].ItemKey, out var scope, out var key)
                || item.Scope != scope || item.Key != key || item.Revision < 0 || item.State is not (null or InterviewProgress.Known or InterviewProgress.Again)))
                throw new UnsupportedMockPreparationException();
            if (turns[source.Current].ItemKey is not null && input.Learning is null) throw new UnsupportedMockPreparationException();
        }
        else if (input.Start is not null || input.Answer is not null || input.Learning is not null || !turns.Any(turn => turn.Score is not null) || input.Waiting.Subject is not null)
            throw new UnsupportedMockPreparationException();
        return input;
    }

    internal static MockPreparationResult ReadResult(MockPreparationRequest root)
    {
        var input = ReadInput(root);
        if (root.ResultJson is null || CanonicalJson.Hash(root.ResultJson) != root.ResultHash) throw new UnsupportedMockPreparationException();
        var result = JsonSerializer.Deserialize(root.ResultJson, MockPreparationJsonContext.Default.MockPreparationResult) ?? throw new UnsupportedMockPreparationException();
        if (result.Version != 1) throw new UnsupportedMockPreparationException();
        if (input.Kind == MockPreparationKind.Questions)
        {
            if (result.Questions is not { Count: >= 2 and <= 8 } || result.Review is not null || result.Summary is not null
                || result.Questions.Any(turn => turn is null || string.IsNullOrWhiteSpace(turn.Question) || string.IsNullOrWhiteSpace(turn.Ideal)
                    || turn.Focus is null || turn.Answer is not null || turn.Score is not null || turn.Feedback is not null || turn.ItemKey is not null))
                throw new UnsupportedMockPreparationException();
        }
        else if (input.Kind == MockPreparationKind.Review)
        {
            if (result.Questions is not null || result.Summary is not null || result.Review is not { Good: not null, Missing: not null, Tip: not null } review
                || review.Validate() is not null || review.Good.Any(text => text is null) || review.Missing.Any(text => text is null)) throw new UnsupportedMockPreparationException();
        }
        else if (result.Questions is not null || result.Review is not null || result.Summary is not { Strengths: not null, Focus: not null, Verdict: not null } summary
            || summary.Validate() is not null || summary.Strengths.Any(text => text is null) || summary.Focus.Any(text => text is null)) throw new UnsupportedMockPreparationException();
        return result;
    }

    internal static Task LockAsync(JobFinderDbContext context, string key, CancellationToken ct) => MockSources.LockAsync(context, ct);
    internal static ValueTask PublishAsync(IDbContextOutbox outbox, MockPreparationRequest root, DateTimeOffset? at = null) => root.Stage switch
    {
        MockPreparationStage.Preparing => outbox.PublishAsync(new PrepareMock { RequestId = root.Id }, new DeliveryOptions { ScheduledTime = at }),
        MockPreparationStage.Publishing => outbox.PublishAsync(new PublishMock { RequestId = root.Id }, new DeliveryOptions { ScheduledTime = at }),
        _ => throw new UnsupportedMockPreparationException(),
    };
}
