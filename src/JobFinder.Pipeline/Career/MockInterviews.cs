using JobFinder.Core.Domain;
using JobFinder.Pipeline.Modules.Preparation;

namespace JobFinder.Pipeline.Career;

public sealed class MockInterviews(MockSources sources, ConversationStore conversations)
{
    public const string Mode = "mock";
    public const int RehearsalSize = 8;
    public static string KindArgument(InterviewKind kind) => kind switch
    {
        InterviewKind.HrScreen => "hr", InterviewKind.Technical => "technical", InterviewKind.SystemDesign => "system_design",
        InterviewKind.Coding => "coding", _ => "behavioral",
    };
    public async Task<BotReply> StartAsync(InterviewKind kind, long? vacancyId, CancellationToken ct) =>
        Reply(await sources.BeginAsync(await sources.CaptureStartAsync(kind, vacancyId, ct), ct));
    public async Task<BotReply> RehearseAsync(long interviewId, CancellationToken ct) =>
        Reply(await sources.BeginAsync(await sources.CaptureStartAsync(InterviewKind.HrScreen, null, ct, interviewId), ct));
    public async Task<BotReply> RehearseHrAsync(CancellationToken ct) =>
        Reply(await sources.BeginAsync(await sources.CaptureStartAsync(InterviewKind.HrScreen, null, ct, hr: true), ct));
    public async Task<BotReply> AnswerAsync(ConversationState state, string answer, CancellationToken ct) => Reply(await sources.AnswerAsync(state, answer, ct));
    public async Task<BotReply> FinishAsync(ConversationState state, CancellationToken ct) => Reply(await sources.FinishAsync(state, ct));
    public async Task<BotReply> StopAsync(long sessionId, int revision, CancellationToken ct)
    {
        var state = await conversations.GetAsync(ct);
        if (state is null) return new("Эта кнопка устарела; текущая тренировка не изменена.");
        return Reply(await sources.FinishAsync(state, ct, sessionId, revision));
    }
    private static BotReply Reply(MockPublication publication) => new(publication.Result.Message);
}
