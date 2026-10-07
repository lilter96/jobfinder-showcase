using System.Globalization;
using JobFinder.Core.Domain;

namespace JobFinder.App;

/// <summary>Display helpers shared by the dashboard pages.</summary>
public static class Ui
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Minsk");

    public static string Local(DateTimeOffset? at, string format = "dd.MM HH:mm") =>
        at is null ? "—" : TimeZoneInfo.ConvertTime(at.Value, Zone).ToString(format, CultureInfo.InvariantCulture);

    public static string StatusCss(VacancyStatus s) => s switch
    {
        VacancyStatus.Applied => "good",
        VacancyStatus.Scored => "warn",
        VacancyStatus.Rejected or VacancyStatus.Failed => "bad",
        _ => "",
    };

    public static string StatusText(VacancyStatus s) => s switch
    {
        VacancyStatus.New => "новая",
        VacancyStatus.Rejected => "отсеяна",
        VacancyStatus.Scored => "оценена",
        VacancyStatus.Applied => "отклик",
        VacancyStatus.Dismissed => "пропущена",
        VacancyStatus.Failed => "ошибка",
        _ => s.ToString(),
    };

    public static string DecisionText(Decision d) => d switch
    {
        Decision.AutoApply => "авто",
        Decision.Approval => "на одобрение",
        Decision.Ignore => "ниже порога",
        _ => "",
    };

    /// <summary>hh's experience ids as people read them; anything else is shown as the source wrote it.</summary>
    public static string ExperienceText(string? experience) => experience switch
    {
        null or "" => "—",
        "noExperience" => "без опыта",
        "between1And3" => "1–3 года",
        "between3And6" => "3–6 лет",
        "moreThan6" => "больше 6 лет",
        _ => experience,
    };

    public static string EnglishText(EnglishLevel level) => level switch
    {
        EnglishLevel.Unknown => "не указан",
        EnglishLevel.NotRequired => "не нужен",
        EnglishLevel.Native => "родной",
        _ => level.ToString(),
    };

    public static string ReplyText(ReplyClass? c) => c switch
    {
        ReplyClass.Invitation => "приглашение",
        ReplyClass.Question => "вопрос",
        ReplyClass.Rejection => "отказ",
        ReplyClass.Offer => "оффер",
        ReplyClass.Viewed => "просмотр",
        _ => "",
    };

    public static string ChannelText(EventChannel c) => c switch
    {
        EventChannel.HeadHunter => "hh",
        EventChannel.Email => "почта",
        EventChannel.Manual => "вручную",
        _ => c.ToString(),
    };

    public static string ContactIcon(ContactKind kind) => kind switch
    {
        ContactKind.Telegram => "✈️",
        ContactKind.Email => "📧",
        ContactKind.Phone => "📞",
        ContactKind.WhatsApp => "💬",
        ContactKind.LinkedIn => "in",
        _ => "🔗",
    };

    /// <summary>
    /// What a contact reads as: a form or a bot by its caption ("Откликнуться", "Посмотреть контакт") rather than a long URL; a person or an
    /// address by its value.
    /// </summary>
    public static string ContactText(VacancyContact c) =>
        c.Kind == ContactKind.Link && !string.IsNullOrWhiteSpace(c.Label) ? c.Label.Trim() : c.Value;

    /// <summary>
    /// The note after a contact: a link's site ("job.2gis.ru"), or the label when it says more than the value (a name, not
    /// "AKHROMOV@ALFABANK.RU" next to akhromov@alfabank.ru).
    /// </summary>
    public static string? ContactNote(VacancyContact c)
    {
        if (c.Kind == ContactKind.Link)
            return !string.IsNullOrWhiteSpace(c.Label) && Uri.TryCreate(c.Value, UriKind.Absolute, out var url) ? url.Host.Replace("www.", "", StringComparison.Ordinal) : null;
        var label = c.Label?.Trim();
        return string.IsNullOrEmpty(label) || string.Equals(label.TrimStart('@'), c.Value.TrimStart('@'), StringComparison.OrdinalIgnoreCase) ? null : label;
    }

    /// <summary>Where a click on a contact goes: a chat, a mail, a call, the form. Null for a Telegram user known only by id.</summary>
    public static string? ContactHref(VacancyContact c) => c.Kind switch
    {
        ContactKind.Telegram when c.Value.StartsWith('@') => "https://t.me/" + c.Value[1..],
        ContactKind.Email => "mailto:" + c.Value,
        ContactKind.Phone => "tel:" + c.Value,
        ContactKind.WhatsApp => "https://wa.me/" + c.Value.TrimStart('+'),
        ContactKind.LinkedIn or ContactKind.Link when Uri.TryCreate(c.Value, UriKind.Absolute, out _) => c.Value,
        _ => null,
    };

    public static string AppText(ApplicationStatus s) => s switch
    {
        ApplicationStatus.Pending => "в очереди",
        ApplicationStatus.Sending => "отправляется",
        ApplicationStatus.Sent => "отправлен",
        ApplicationStatus.Failed => "ошибка",
        ApplicationStatus.NeedsManual => "нужно вручную",
        ApplicationStatus.Cancelled => "отменён",
        ApplicationStatus.DryRun => "dry-run: ждёт",
        ApplicationStatus.OutcomeUnknown => "доставка не подтверждена",
        _ => s.ToString(),
    };

    public static string ModeText(ApplicationMode m) => m switch
    {
        ApplicationMode.Auto => "авто",
        ApplicationMode.Approved => "одобрен",
        ApplicationMode.External => "вне системы",
        _ => m.ToString(),
    };

    public static string AppCss(ApplicationStatus s) => s switch
    {
        ApplicationStatus.Sent => "good",
        ApplicationStatus.Pending or ApplicationStatus.Sending or ApplicationStatus.NeedsManual or ApplicationStatus.DryRun or ApplicationStatus.OutcomeUnknown => "warn",
        ApplicationStatus.Failed => "bad",
        _ => "",
    };
}

/// <summary>A table's sort: the column key and the direction; <c>"score.desc"</c> in a query string.</summary>
public readonly record struct SortState(string Key, bool Desc)
{
    public static SortState Parse(string? value, SortState fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var dot = value.LastIndexOf('.');
        return dot <= 0 ? new(value, false) : new(value[..dot], value[(dot + 1)..] == "desc");
    }

    public override string ToString() => $"{Key}.{(Desc ? "desc" : "asc")}";
}
