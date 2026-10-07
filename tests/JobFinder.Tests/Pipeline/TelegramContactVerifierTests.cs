using JobFinder.Core.Domain;
using JobFinder.Integrations.Telegram;
using JobFinder.Pipeline.Ingest;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace JobFinder.Tests.Pipeline;

/// <summary>
/// Only people stay among a vacancy's Telegram contacts. The channel names are the footer seen live on every post of a Proglib
/// channel («База знаний», «Задачи», «Собеседования»), taken for recruiters before this check existed.
/// </summary>
public sealed class TelegramContactVerifierTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ScriptedTelegram : ITelegramUsernames
    {
        public Dictionary<string, (TelegramPeerKind, string?)> Known { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Asked { get; } = [];

        public bool IsReady { get; set; } = true;

        public TimeSpan? Flood { get; set; }

        public Task<(TelegramPeerKind Kind, string? Name)> ResolveKindAsync(string username)
        {
            Asked.Add(username);
            if (Flood is { } wait) throw new TelegramFloodException(wait);
            return Task.FromResult(Known.GetValueOrDefault(username, (TelegramPeerKind.Missing, null)));
        }
    }

    private static string Unique(string handle) => $"{handle}_{Guid.NewGuid():N}"[..Math.Min(32, handle.Length + 9)];

    private TelegramContactVerifier Verifier(ScriptedTelegram telegram, TimeProvider? time = null, int perHour = 30)
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        return new TelegramContactVerifier(pg.Db, telegram, time ?? TimeProvider.System, NullLogger<TelegramContactVerifier>.Instance, perHour);
    }

    private static VacancyContact Tg(string handle, string? label = null) => new() { Kind = ContactKind.Telegram, Value = "@" + handle, Label = label };

    [Fact]
    public async Task Channels_groups_and_missing_handles_go_a_person_stays_with_their_name()
    {
        var (lib, tasks, recruiter, chat, typo) = (Unique("csharpproglib"), Unique("csharp_problems"), Unique("hr_anna"), Unique("dotnet_chat"), Unique("nobody"));
        var telegram = new ScriptedTelegram
        {
            Known =
            {
                [lib] = (TelegramPeerKind.Channel, "Библиотека шарписта"),
                [tasks] = (TelegramPeerKind.Channel, "Задачи по C#"),
                [recruiter] = (TelegramPeerKind.Person, "Анна Смирнова"),
                [chat] = (TelegramPeerKind.Group, ".NET чат"),
            },
        };

        var kept = await Verifier(telegram).VerifyAsync(
        [
            Tg(lib, "База знаний"), Tg(tasks, "Задачи"), Tg(recruiter), Tg(chat), Tg(typo),
            new VacancyContact { Kind = ContactKind.Email, Value = "hr@acme.io" },
            new VacancyContact { Kind = ContactKind.Telegram, Value = "tg://user?id=42", Label = "Ивану" }, // no username: cannot be asked, kept
        ], Ct);

        Assert.Equal([("@" + recruiter, "Анна Смирнова"), ("hr@acme.io", null), ("tg://user?id=42", "Ивану")], kept.Select(c => (c.Value, c.Label)));
    }

    [Fact]
    public async Task Each_handle_is_asked_once_and_a_restart_reads_the_answer_from_the_table()
    {
        var channel = Unique("csharp_interview");
        var telegram = new ScriptedTelegram { Known = { [channel] = (TelegramPeerKind.Channel, "Собеседования") } };

        await Verifier(telegram).VerifyAsync([Tg(channel)], Ct);
        await Verifier(telegram).VerifyAsync([Tg(channel), Tg(channel.ToUpperInvariant())], Ct); // a new process, another case

        Assert.Single(telegram.Asked);
    }

    [Fact]
    public async Task Past_the_hourly_budget_a_handle_is_kept_unverified_not_dropped()
    {
        var (first, second) = (Unique("channel_a"), Unique("maybe_hr"));
        var telegram = new ScriptedTelegram { Known = { [first] = (TelegramPeerKind.Channel, "A"), [second] = (TelegramPeerKind.Channel, "B") } };

        var kept = await Verifier(telegram, perHour: 1).VerifyAsync([Tg(first), Tg(second)], Ct);

        Assert.Equal(["@" + second], kept.Select(c => c.Value));
        Assert.Equal([first], telegram.Asked);
    }

    [Fact]
    public async Task A_flood_wait_stops_resolving_for_as_long_as_telegram_asks()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (a, b, c) = (Unique("x_first"), Unique("x_second"), Unique("x_third"));
        var telegram = new ScriptedTelegram { Flood = TimeSpan.FromMinutes(10), Known = { [c] = (TelegramPeerKind.Channel, "C") } };
        var verifier = Verifier(telegram, time);

        Assert.Equal(2, (await verifier.VerifyAsync([Tg(a), Tg(b)], Ct)).Count);
        Assert.Single(telegram.Asked); // after the flood answer nobody is asked

        telegram.Flood = null;
        time.Advance(TimeSpan.FromMinutes(11));
        Assert.Empty(await verifier.VerifyAsync([Tg(c)], Ct));
        Assert.Equal(2, telegram.Asked.Count);
    }

    [Fact]
    public async Task Without_telegram_handles_stay_and_nothing_is_asked()
    {
        var telegram = new ScriptedTelegram { IsReady = false };
        var handle = Unique("hr_kate");
        Assert.Equal(["@" + handle], (await Verifier(telegram).VerifyAsync([Tg(handle)], Ct)).Select(c => c.Value));
        Assert.Empty(telegram.Asked);
    }

    [Fact]
    public async Task A_free_handle_is_asked_again_after_a_few_days_a_known_one_is_not()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (free, channel) = (Unique("free_now"), Unique("known_channel"));
        var telegram = new ScriptedTelegram { Known = { [channel] = (TelegramPeerKind.Channel, "K") } };
        var verifier = Verifier(telegram, time);
        await verifier.VerifyAsync([Tg(free), Tg(channel)], Ct);

        time.Advance(TimeSpan.FromDays(4));
        telegram.Known[free] = (TelegramPeerKind.Person, "Taken since"); // somebody took the handle
        var kept = await Verifier(telegram, time).VerifyAsync([Tg(free), Tg(channel)], Ct);

        Assert.Equal([("@" + free, "Taken since")], kept.Select(c => (c.Value, c.Label)));
        Assert.Equal(3, telegram.Asked.Count); // the channel was not asked again
    }
}
