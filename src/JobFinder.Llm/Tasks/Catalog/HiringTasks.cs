using System.Globalization;
using System.Text;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Llm.Contracts;
using JobFinder.Llm.Contracts.Wire;
using static JobFinder.Llm.Tasks.JobFinderLlm;

namespace JobFinder.Llm.Tasks;

public sealed record AnalyzeReplyInput(CandidateProfile Profile, CandidateFacts Facts, Vacancy? Vacancy, string Conversation, string Message, string Stories, DateTimeOffset Now, string TimeZone, string? Remarks, bool Deep);

public sealed record OutreachInput(CandidateProfile Profile, Vacancy Vacancy, string Contact, string Role, string Channel, bool Essential);

public sealed record RecruiterMessageInput(CandidateProfile Profile, Vacancy Vacancy, string Facts, string Contact, string Situation, string Channel, bool AttachesResume, string Conversation, DateTimeOffset Now, bool First, string? Remarks);

public sealed record CheckRecruiterMessageInput(CandidateProfile Profile, string Facts, Vacancy Vacancy, string Message, bool Deep, bool First);

internal static class HiringTaskBuilders
{
    public static LlmTaskPayload AnalyzeReply(AnalyzeReplyInput input)
    {
        var profile = input.Profile;
        var facts = input.Facts;
        var vacancy = input.Vacancy;
        var conversation = input.Conversation;
        var message = input.Message;
        var stories = input.Stories;
        var now = input.Now;
        var timeZone = input.TimeZone;
        var remarks = input.Remarks;
        var deep = input.Deep;
        var bindings = new Dictionary<string, string>
        {
            ["profile"] = ProfileText(profile),
            ["facts"] = facts.ForPrompt(),
            ["today"] = now.ToString("yyyy-MM-dd dddd HH:mm", CultureInfo.GetCultureInfo("ru-RU")),
            ["timezone"] = timeZone,
        };
        var user = (vacancy is null ? "<vacancy>(не определена)</vacancy>" : VacancyText(vacancy)) +
                   $"\n\n<stories>\n{(string.IsNullOrWhiteSpace(stories) ? "(пока нет)" : TextUtil.Truncate(stories, 6000))}\n</stories>" +
                   $"\n\n<conversation>\n{TextUtil.Truncate(conversation, 6000)}\n</conversation>\n\n<message>\n{TextUtil.Truncate(message, 4000)}\n</message>";
        if (remarks is not null)
            user += $"\n\nПрошлый draft не прошёл проверку и не может уйти как есть:\n{remarks}\nНапиши draft заново по правилам: готовый ответ от первого лица, " +
                    "без скобок и пометок; чего нет в профиле, выведи из опыта. Остальные поля заполни заново.";
        return new LlmTaskPayload(deep ? LlmPurpose.ReplyDeep : LlmPurpose.Reply, bindings, user);
    }

    public static LlmTaskPayload Outreach(OutreachInput input)
    {
        var profile = input.Profile;
        var vacancy = input.Vacancy;
        var contact = input.Contact;
        var role = input.Role;
        var channel = input.Channel;
        var essential = input.Essential;
        var bindings = new Dictionary<string, string>
        {
            ["profile"] = ProfileText(profile),
            ["contact"] = contact,
            ["role"] = role,
            ["channel"] = channel,
        };
        return new LlmTaskPayload(LlmPurpose.Letter, bindings, VacancyText(vacancy, withEvaluation: true), essential);
    }

    public static LlmTaskPayload RecruiterMessage(RecruiterMessageInput input)
    {
        var profile = input.Profile;
        var vacancy = input.Vacancy;
        var facts = input.Facts;
        var contact = input.Contact;
        var situation = input.Situation;
        var channel = input.Channel;
        var attachesResume = input.AttachesResume;
        var conversation = input.Conversation;
        var now = input.Now;
        var first = input.First;
        var remarks = input.Remarks;
        var bindings = new Dictionary<string, string>
        {
            ["profile"] = ProfileText(profile),
            ["name"] = profile.Name,
            ["facts"] = facts,
            ["contact"] = contact,
            ["situation"] = situation,
            ["channel"] = channel,
            ["attachment"] = attachesResume
                ? "К сообщению будет приложен PDF с резюме под эту вакансию: файл виден и так, не упоминай его."
                : "Резюме к сообщению не прикладывается: не упоминай вложений.",
            ["today"] = now.ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("ru-RU")),
        };
        var user = VacancyText(vacancy, withEvaluation: true) + $"\n\n<conversation>\n{TextUtil.Truncate(conversation, 6000)}\n</conversation>" +
                   (remarks is null ? "" : $"\n\nПрошлый вариант не прошёл проверку. Улучши его: исправь названное, сильное оставь, факты — только из профиля и фактов.\n{remarks}");
        // The response decides whether there is a conversation at all: the maximum-effort route; later turns the high-effort one.
        return new LlmTaskPayload(first ? LlmPurpose.Application : LlmPurpose.ReplyDeep, bindings, user);
    }

    public static LlmTaskPayload CheckRecruiterMessage(CheckRecruiterMessageInput input)
    {
        var profile = input.Profile;
        var facts = input.Facts;
        var vacancy = input.Vacancy;
        var message = input.Message;
        var deep = input.Deep;
        var first = input.First;
        var bindings = new Dictionary<string, string>();
        // The whole vacancy, not only its title: the verdict weighs the message against what the vacancy needs most.
        var user = $"<profile>\n{ProfileText(profile)}\n</profile>\n\n<facts>\n{facts}\n</facts>\n\n{VacancyText(vacancy)}\n\n" +
                   $"<message>\n{message}\n</message>";
        return new LlmTaskPayload(first ? LlmPurpose.Application : deep ? LlmPurpose.ReplyDeep : LlmPurpose.Reply, bindings, user);
    }
}
