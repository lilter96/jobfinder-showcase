using JobFinder.Core.Text;
using JobFinder.Pipeline.Modules.Hiring;

namespace JobFinder.Pipeline.Career;

public sealed class OfferAdvisor(OfferPreparationRequests requests)
{
    public const string Mode = "offer";

    public async Task<BotReply> AnalyzeAsync(string text, CancellationToken ct, ConversationState? conversation = null)
    {
        var id = await requests.RequestAsync(text, ct, conversation);
        return id is null ? new BotReply("Исходный диалог изменился или нужен проверенный профиль; данные сохранены.") : new BotReply("");
    }

    public async Task<BotReply> StopAsync(ConversationState expected, CancellationToken ct)
    {
        var result = await requests.StopAsync(expected, ct);
        return new BotReply(TextUtil.EscapeHtml(result.Message));
    }
}
