using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Pipeline.Modules.Discovery;

namespace JobFinder.Tests.Pipeline;

public sealed class ChannelMembershipProofTests
{
    internal static ChannelMembershipRequest Root()
    {
        var input = new ChannelMembershipInput { Source = new ChannelDecisionSnapshot { Id = 17, Revision = 1,
            Status = ChannelStatus.Approved, Username = "synthetic", Kind = ChannelKind.Channel,
            AddedAt = DateTimeOffset.UtcNow.AddDays(-1) } };
        var chat = new ChannelMembershipChat { Id = 123, AccessHash = 456, Username = "synthetic", Title = "Synthetic" };
        var root = new ChannelMembershipRequest { Id = Guid.NewGuid(), ChannelId = 17, DecisionRevision = 1,
            Stage = ChannelMembershipStage.Publishing, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10), AccountId = 42,
            ClaimEnvelopeId = Guid.NewGuid(), InputJson = JsonSerializer.Serialize(input, ChannelMembershipJsonContext.Default.ChannelMembershipInput), InputHash = "" };
        root.InputHash = CanonicalJson.Hash(root.InputJson);
        root.ChatJson = JsonSerializer.Serialize(chat, ChannelMembershipJsonContext.Default.ChannelMembershipChat);
        root.ChatHash = CanonicalJson.Hash(root.ChatJson);
        root.HistoryJson = JsonSerializer.Serialize(new ChannelMembershipHistory(), ChannelMembershipJsonContext.Default.ChannelMembershipHistory);
        root.HistoryHash = CanonicalJson.Hash(root.HistoryJson);
        return root;
    }

    internal static List<ChannelMembershipAttempt> Attempts(ChannelMembershipRequest root) =>
        new[] { ChannelMembershipStage.Joining, ChannelMembershipStage.Muting, ChannelMembershipStage.Archiving, ChannelMembershipStage.Filing }
        .Select((stage, index) => new ChannelMembershipAttempt { Id = Guid.NewGuid(), RequestId = root.Id, EnvelopeId = Guid.NewGuid(),
            AccountId = 42, PeerId = 123, InputHash = root.InputHash, ChatHash = root.ChatHash!, Stage = stage,
            Outcome = ChannelMembershipOutcome.Confirmed, StartedAt = root.CreatedAt.AddSeconds(index * 2),
            CompletedAt = root.CreatedAt.AddSeconds(index * 2 + 1), Evidence = "synthetic RPC receipt" }).ToList();

    [Fact]
    public void Complete_bound_ordered_receipts_allow_publication() => ChannelMembershipInputs.Publication(RootWithAttempts(out var attempts), attempts);

    private static ChannelMembershipRequest RootWithAttempts(out List<ChannelMembershipAttempt> attempts)
    {
        var root = Root();
        attempts = Attempts(root);
        return root;
    }

    [Theory]
    [InlineData("account")]
    [InlineData("peer")]
    [InlineData("input")]
    [InlineData("chat")]
    [InlineData("envelope")]
    [InlineData("request")]
    [InlineData("timestamp")]
    [InlineData("evidence")]
    [InlineData("invite")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("checked")]
    [InlineData("stage")]
    [InlineData("pointer")]
    public void Damaged_or_incomplete_receipts_cannot_certify_membership(string damage)
    {
        var root = RootWithAttempts(out var attempts);
        var first = attempts[0];
        switch (damage)
        {
            case "account": first.AccountId++; break;
            case "peer": first.PeerId++; break;
            case "input": first.InputHash = "wrong"; break;
            case "chat": first.ChatHash = "wrong"; break;
            case "envelope": first.EnvelopeId = Guid.Empty; break;
            case "request": first.RequestId = Guid.NewGuid(); break;
            case "timestamp": first.CompletedAt = root.CreatedAt.AddSeconds(-1); break;
            case "evidence": first.Evidence = " "; break;
            case "invite": first.Outcome = ChannelMembershipOutcome.InviteRequested; break;
            case "unknown": first.Outcome = ChannelMembershipOutcome.OutcomeUnknown; break;
            case "duplicate": attempts.Add(first); break;
            case "missing": attempts.RemoveAt(0); break;
            case "checked": first.CheckedApplied = true; break;
            case "stage": first.Stage = (ChannelMembershipStage)999; break;
            case "pointer": root.AttemptId = first.Id; break;
        }
        Assert.Throws<UnsupportedChannelMembershipException>(() => ChannelMembershipInputs.Publication(root, attempts));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void History_rejects_duplicate_identity_and_owned_source_payload(bool duplicate)
    {
        var root = Root();
        var post = new RawPost { Source = SourceKind.Telegram, ChatRef = "123", ExternalId = "1", Text = "Synthetic",
            Status = RawPostStatus.New, FetchedAt = DateTimeOffset.UtcNow, PublishedAt = DateTimeOffset.UtcNow };
        if (!duplicate) post.ProcessingRequestId = Guid.NewGuid();
        var history = new ChannelMembershipHistory { Posts = duplicate ? [post, post] : [post] };
        root.HistoryJson = JsonSerializer.Serialize(history, ChannelMembershipJsonContext.Default.ChannelMembershipHistory);
        root.HistoryHash = CanonicalJson.Hash(root.HistoryJson);
        Assert.Throws<UnsupportedChannelMembershipException>(() => ChannelMembershipInputs.History(root));
    }
}
