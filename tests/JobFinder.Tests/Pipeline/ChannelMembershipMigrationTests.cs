using JobFinder.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace JobFinder.Tests.Pipeline;

public sealed partial class ChannelMembershipTests
{
    [Theory]
    [InlineData("root")]
    [InlineData("marker")]
    [InlineData("envelope")]
    public async Task Downgrade_refuses_roots_orphan_markers_and_orphan_native_envelopes(string retained)
    {
        await AdmitAsync();
        await _host!.StopAsync(Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (retained != "root")
        {
            await context.ChannelMembershipAttempts.ExecuteDeleteAsync(Ct);
            await context.ChannelMembershipRequests.ExecuteDeleteAsync(Ct);
        }
        if (retained == "envelope") await context.TelegramChannels.ExecuteUpdateAsync(update => update.SetProperty(row => row.MembershipRequestId, (Guid?)null), Ct);
        else await ClearMembershipEnvelopesAsync(context);
        var error = await Assert.ThrowsAsync<PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261004074148_CapturedTelegramChannelDecisions", Ct));
        Assert.Contains("Cannot downgrade retained Telegram membership", error.MessageText);
        Assert.Contains("20261005103050_DurableChannelMembership", await context.Database.GetAppliedMigrationsAsync(Ct));
    }

    [Fact]
    public async Task Empty_membership_down_and_upgrade_preserve_legacy_channel_identity_and_history()
    {
        await _host!.StopAsync(Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var source = new TelegramChannel { Username = "legacy", Title = "Legacy", Status = ChannelStatus.Joined,
            AddedAt = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero), PeerId = 987 };
        context.TelegramChannels.Add(source);
        await context.SaveChangesAsync(Ct);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20261004074148_CapturedTelegramChannelDecisions", Ct);
        await migrator.MigrateAsync(null, Ct);
        var saved = await context.TelegramChannels.AsNoTracking().SingleAsync(row => row.Id == source.Id, Ct);
        Assert.Equal("Legacy", saved.Title);
        Assert.Equal(987L, saved.PeerId);
        Assert.Equal(ChannelStatus.Joined, saved.Status);
        Assert.Null(saved.MembershipRequestId);
    }
}
