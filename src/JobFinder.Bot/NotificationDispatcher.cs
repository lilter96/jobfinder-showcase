using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Notifications;
using Telegram.Bot;
using Telegram.Bot.Types.ReplyMarkups;

namespace JobFinder.Bot;

public sealed class TelegramBotClientHolder(BotOptions options, IHttpClientFactory http)
{
    public const string HttpClientName = "telegram-bot";

    public ITelegramBotClient? Client { get; } = string.IsNullOrWhiteSpace(options.Token)
        ? null
        : new TelegramBotClient(options.Token, http.CreateClient(HttpClientName));

    internal ITelegramBotClient? DeliveryClient { get; } = string.IsNullOrWhiteSpace(options.Token)
        ? null
        : new TelegramBotClient(new TelegramBotClientOptions(options.Token) { RetryCount = 1, RetryThreshold = 0 }, http.CreateClient(HttpClientName));

    internal ITelegramBotClient? ControlClient { get; } = string.IsNullOrWhiteSpace(options.Token)
        ? null
        : new TelegramBotClient(new TelegramBotClientOptions(options.Token) { RetryCount = 1, RetryThreshold = 0 }, http.CreateClient(HttpClientName));
}

public sealed class NotificationDispatcher(LegacyNotificationImports imports, JobTriggers triggers, TimeProvider time,
    ILogger<NotificationDispatcher> log) : QueueWorker(triggers, time, log)
{
    public override string Name => JobNames.Notifications;
    protected override TimeSpan IdleDelay => TimeSpan.FromSeconds(3);
    protected override bool Enabled => true;

    protected override Task<int> ProcessBatchAsync(CancellationToken ct) => imports.ImportBatchAsync(10, ct);

    public static InlineKeyboardMarkup Keyboard(List<List<string>> rows) =>
        new(rows.Where(row => row.Count > 0).Select(row => row.Select(button =>
        {
            var split = button.IndexOf('|');
            return split > 0 ? InlineKeyboardButton.WithCallbackData(button[..split], button[(split + 1)..]) : InlineKeyboardButton.WithCallbackData(button, "noop:0:0");
        })));
}
