using System.Globalization;
using System.Text.Json.Serialization;

namespace JobFinder.Core.Profile;

/// <summary>
/// One answer the candidate gives to a question employers ask in chats and questionnaires. Only a confirmed fact lets the system answer
/// on its own; a drafted or missing one turns the reply into a draft for the user.
/// </summary>
public sealed record CandidateFact
{
    public string Key { get; set; } = "";

    /// <summary>The question as employers ask it (shown to the user and to the model).</summary>
    public string Question { get; set; } = "";

    /// <summary>How the candidate answers, in their words; null = unknown.</summary>
    public string? Answer { get; set; }

    public bool Confirmed { get; set; }

    /// <summary>Where the draft came from: "профиль", "резюме", "вы".</summary>
    public string? Source { get; set; }
}

/// <summary>The versioned PostgreSQL document <c>candidate.facts</c>: the answers to screening questions.</summary>
public sealed record CandidateFacts
{
    public List<CandidateFact> Facts { get; set; } = [];

    public CandidateFact? Get(string key) => Facts.FirstOrDefault(f => f.Key == key);

    public bool IsConfirmed(string key) => Get(key) is { Confirmed: true, Answer: { Length: > 0 } };

    public IEnumerable<CandidateFact> Pending => Facts.Where(f => !f.Confirmed || string.IsNullOrWhiteSpace(f.Answer));

    /// <summary>The facts as the reply prompt reads them: key, question, answer or "неизвестно", confirmed or not.</summary>
    public string ForPrompt() => string.Join("\n", Facts.Select(f =>
        $"- {f.Key} | {f.Question} | {(string.IsNullOrWhiteSpace(f.Answer) ? "НЕИЗВЕСТНО" : f.Answer)} | {(f.Confirmed ? "подтверждено" : "не подтверждено")}"));

    /// <summary>Facts only the candidate knows, asked by HR interviews (the HR document needs them); never drafted from the profile.</summary>
    public static readonly (string Key, string Question)[] Personal =
    [
        ("hobby", "Чем увлекаетесь вне работы"),
        ("learning", "Что изучаете сейчас (курсы, книги, темы)"),
        ("overtime", "Как относитесь к переработкам"),
        ("trips", "Готовы ли к командировкам"),
    ];

    /// <summary>A facts file written before a question existed gets it as an empty fact: the page shows it to fill, answers keep theirs.</summary>
    public CandidateFacts WithStandardQuestions()
    {
        foreach (var (key, question) in Personal.Where(p => Get(p.Key) is null))
            Facts.Add(new CandidateFact { Key = key, Question = question });
        return this;
    }

    /// <summary>The questions employers ask, and what the profile already tells about them (drafts, never confirmed).</summary>
    public static CandidateFacts Draft(CandidateProfile p)
    {
        string? salary = p.DesiredSalary is { } s
            ? $"от {s.Amount.ToString("0", CultureInfo.InvariantCulture)} {s.Currency} в месяц {(s.Net ? "на руки" : "до налогов")}, обсуждаемо"
            : null;
        var formats = p.WorkFormats.Select(f => f switch { "remote" => "удалённо", "hybrid" => "гибрид", "office" => "офис", _ => f }).ToList();
        string? years = p.YearsOfExperience > 0 ? $"{Math.Floor(p.YearsOfExperience).ToString(CultureInfo.InvariantCulture)}+ лет коммерческой разработки на C#/.NET" : null;
        var contacts = string.Join(", ", new[]
        {
            p.Contacts.Telegram is { Length: > 0 } tg ? "Telegram " + tg : null,
            p.Contacts.Email is { Length: > 0 } mail ? mail : null,
            p.Contacts.Phone is { Length: > 0 } phone ? phone : null,
        }.OfType<string>());
        return new CandidateFacts
        {
            Facts =
            [
                Fact("salary", "Зарплатные ожидания", salary, "профиль"),
                Fact("location", "Где вы находитесь", string.IsNullOrWhiteSpace(p.Location) ? null : p.Location, "профиль"),
                Fact("work_format", "Какой формат работы подходит", formats.Count == 0 ? null : string.Join(", ", formats), "профиль"),
                Fact("remote_from", "Можете ли работать удалённо из вашей страны на компанию из РФ", null, null),
                Fact("relocation", "Рассматриваете ли релокацию", "не рассматриваю", "профиль"),
                Fact("english", "Уровень английского", $"{p.EnglishLevel}: читаю документацию и переписываюсь, собеседование на английском не прохожу", "профиль"),
                Fact("experience", "Опыт работы", years, "резюме"),
                Fact("notice", "Когда готовы выйти на работу", null, null),
                Fact("employment", "Как можете оформиться (ТД, ИП, договор)", null, null),
                Fact("citizenship", "Гражданство", null, null),
                Fact("call_slots", "Когда удобно созвониться", null, null),
                Fact("test_task", "Готовы ли выполнить тестовое задание", null, null),
                Fact("status", "Сейчас работаете? Почему ищете", null, null),
                Fact("contacts", "Как с вами связаться", string.IsNullOrWhiteSpace(contacts) ? null : contacts, "профиль"),
                .. Personal.Select(p => Fact(p.Key, p.Question, null, null)),
            ],
        };

        static CandidateFact Fact(string key, string question, string? answer, string? source) =>
            new() { Key = key, Question = question, Answer = answer, Source = source, Confirmed = false };
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(CandidateFacts))]
public sealed partial class FactsJsonContext : JsonSerializerContext;
