using JobFinder.Core.Domain;
using JobFinder.Integrations.Telegram;
using JobFinder.Pipeline.Career;

namespace JobFinder.Tests.Pipeline;

public sealed class RecruiterChatTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);

    private static Application App(ApplicationStage? stage, ApplicationStatus status = ApplicationStatus.Sent) =>
        new() { Channel = ApplicationChannel.HeadHunter, Mode = ApplicationMode.External, Status = status, Stage = stage, CreatedAt = T0, SentAt = T0 };

    [Theory]
    [InlineData(null, RecruiterSituation.NotApplied)]
    [InlineData(ApplicationStage.Sent, RecruiterSituation.Waiting)]
    [InlineData(ApplicationStage.NoResponse, RecruiterSituation.Silent)]
    [InlineData(ApplicationStage.Rejected, RecruiterSituation.Rejected)]
    [InlineData(ApplicationStage.TechInterview, RecruiterSituation.Interview)]
    public void The_next_message_follows_the_response(ApplicationStage? stage, RecruiterSituation expected) =>
        Assert.Equal(expected, RecruiterChatService.Situation(stage is null ? null : App(stage)));

    [Fact]
    public void Our_own_message_without_an_answer_is_a_nudge_their_answer_is_a_conversation()
    {
        var app = App(ApplicationStage.Sent);
        app.Events.Add(new ApplicationEvent { At = T0, Kind = EventKind.MessageOut, Channel = EventChannel.Telegram, Text = "Юлия, здравствуйте!" });
        Assert.Equal(RecruiterSituation.Nudge, RecruiterChatService.Situation(app));
        app.Events.Add(new ApplicationEvent { At = T0.AddHours(3), Kind = EventKind.MessageIn, Channel = EventChannel.Telegram, Text = "Добрый день!" });
        Assert.Equal(RecruiterSituation.Conversation, RecruiterChatService.Situation(app));
    }

    [Fact]
    public void A_queued_or_failed_response_is_not_a_response_yet() =>
        Assert.Equal(RecruiterSituation.NotApplied, RecruiterChatService.Situation(App(null, ApplicationStatus.Pending)));

    [Theory]
    [InlineData(RecruiterSituation.NotApplied, true)]
    [InlineData(RecruiterSituation.Waiting, true)]
    [InlineData(RecruiterSituation.Rejected, false)]
    [InlineData(RecruiterSituation.Conversation, false)]
    public void The_resume_goes_with_a_first_word_only(RecruiterSituation s, bool attach) => Assert.Equal(attach, RecruiterChatService.AttachByDefault(s));

    [Theory]
    [InlineData(RecruiterSituation.Rejected, true)]
    [InlineData(RecruiterSituation.Waiting, false)]
    [InlineData(RecruiterSituation.Silent, false)]
    [InlineData(RecruiterSituation.Interview, false)]
    public void After_a_rejection_the_message_starts_from_scratch(RecruiterSituation s, bool fresh) =>
        Assert.Equal(fresh, RecruiterChatService.FreshByDefault(s));

    [Theory]
    [InlineData(ApplicationStage.Rejected, true)]
    [InlineData(ApplicationStage.NoResponse, true)]
    [InlineData(ApplicationStage.Withdrawn, true)]
    [InlineData(ApplicationStage.Sent, false)]
    [InlineData(ApplicationStage.HrScreen, false)]
    public void A_message_after_a_closed_response_is_a_new_attempt(ApplicationStage stage, bool closed) =>
        Assert.Equal(closed, RecruiterChatService.IsClosed(App(stage)));

    [Fact]
    public void The_chat_joins_the_history_and_our_own_sent_message_is_recognized_not_copied()
    {
        var sentFromHere = new ApplicationEvent
        {
            At = T0, Kind = EventKind.MessageOut, Channel = EventChannel.Telegram, Author = "я",
            Text = "Юлия, здравствуйте! Откликался на вашу вакансию .NET разработчика на hh.\n📎 Гацуков — Senior .NET.pdf",
        };
        var messages = new[]
        {
            new TgDirectMessage(10, true, "📎 Гацуков — Senior .NET.pdf\nЮлия, здравствуйте! Откликался на вашу вакансию .NET разработчика на hh.", T0.AddSeconds(3)),
            new TgDirectMessage(11, false, "Добрый день! Передам тимлиду, вернусь до пятницы.", T0.AddHours(2)),
            new TgDirectMessage(12, true, "Спасибо, жду.", T0.AddHours(3)),
        };

        var (added, claimed) = RecruiterChatService.Merge([sentFromHere], 555, "Yuliay_Botvinina", messages);

        Assert.Equal((sentFromHere, "555/10"), Assert.Single(claimed));
        Assert.Equal(
            [(EventKind.MessageIn, "@yuliay_botvinina", "555/11"), (EventKind.MessageOut, "я", "555/12")],
            added.Select(e => (e.Kind, e.Author!, e.ExternalId!)));
        Assert.All(added, e => Assert.Equal(EventChannel.Telegram, e.Channel));
    }

    [Fact]
    public void A_different_message_of_ours_is_not_mistaken_for_the_stored_one()
    {
        var stored = new ApplicationEvent { At = T0, Kind = EventKind.MessageOut, Channel = EventChannel.Telegram, Text = "Здравствуйте! Хотел уточнить по вакансии." };
        var (added, claimed) = RecruiterChatService.Merge([stored], 1, "hr", [new TgDirectMessage(1, true, "Совсем другой текст про созвон в четверг.", T0)]);
        Assert.Empty(claimed);
        Assert.Single(added);
    }
}
