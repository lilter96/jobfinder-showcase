using System.Globalization;
using System.Text;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Notifications;

namespace JobFinder.Pipeline.Modules.Preparation;

internal static class MockPresentation
{
    internal const string Version = "native-v1";
    internal static List<List<string>> Buttons(MockSession source) => source.Revision is > 0 and <= int.MaxValue
        ? [[Callback.Button("⏹ Закончить", CareerCallback.MockStop, source.Id, (int)source.Revision)]] : [];
    internal static string Question(List<MockTurn> turns, int current) =>
        $"<b>Вопрос {current + 1}/{turns.Count}.</b> {TextUtil.EscapeHtml(turns[current].Question)}";
    internal static string Stars(int score) => new string('★', score) + new string('☆', 5 - score) + $" {score}/5";
    internal static string Feedback(MockReview review, bool conflict)
    {
        var text = new StringBuilder();
        if (review.Good.Count > 0) text.Append("✅ ").Append(string.Join("; ", review.Good)).Append('\n');
        if (review.Missing.Count > 0) text.Append("➕ ").Append(string.Join("; ", review.Missing)).Append('\n');
        text.Append("💡 ").Append(review.Tip);
        if (conflict) text.Append("\nОтметка обучения изменилась во время разбора; новая отметка не перезаписана.");
        return text.ToString();
    }
    internal static string Reviewed(MockTurn turn) => $"<b>Оценка: {Stars(turn.Score!.Value)}</b>\n{TextUtil.EscapeHtml(turn.Feedback!)}\n"
        + (turn.ItemKey is null ? "<blockquote expandable><b>Сильный ответ:</b>\n" : "<blockquote expandable><b>Подготовленный ответ:</b>\n")
        + TextUtil.EscapeHtml(turn.Ideal) + "</blockquote>\n\n";
    internal static string Summary(MockPreparationInput input, MockSummary summary)
    {
        var answered = ConversationStore.Turns(input.Source.TurnsJson).Where(turn => turn.Score is not null).ToList();
        var text = new StringBuilder(input.Prefix).Append("🏁 <b>Итог: ").Append(Stars(summary.Score)).Append("</b> (среднее по ответам ")
            .Append(answered.Average(turn => turn.Score!.Value).ToString("0.0", CultureInfo.InvariantCulture)).Append(")\n")
            .Append(TextUtil.EscapeHtml(summary.Verdict)).Append('\n');
        if (summary.Strengths.Count > 0) text.Append("\n<b>Сильное:</b> ").Append(TextUtil.EscapeHtml(string.Join("; ", summary.Strengths))).Append('\n');
        if (summary.Focus.Count > 0)
        {
            text.Append("\n<b>Подтянуть:</b>\n");
            foreach (var focus in summary.Focus) text.Append("• ").Append(TextUtil.EscapeHtml(focus)).Append('\n');
        }
        return CareerCards.Truncate(text.ToString());
    }
}
