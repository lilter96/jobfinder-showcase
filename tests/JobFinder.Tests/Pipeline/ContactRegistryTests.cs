using JobFinder.Pipeline.Career;

namespace JobFinder.Tests.Pipeline;

public sealed class ContactRegistryTests
{
    [Theory]
    [InlineData("Резюме присылайте в Telegram @HR_Anna, спасибо", new[] { "@hr_anna" })]
    [InlineData("Пишите: t.me/anna_hr или https://telegram.me/ivan_recruit", new[] { "@anna_hr", "@ivan_recruit" })]
    [InlineData("Бот @company_jobs_bot, канал t.me/joinchat/AbCdEf, invite t.me/+xYz", new string[0])]
    [InlineData("почта hr@acme.io, не телеграм", new string[0])]
    [InlineData("@hr_anna и снова @HR_ANNA", new[] { "@hr_anna" })]
    [InlineData("https://hh.ru/vacancy/137846728?from=share_android @recruiter_kate", new[] { "@recruiter_kate" })]
    public void Recruiter_usernames_are_found_in_free_text(string text, string[] expected) =>
        Assert.Equal(expected, ContactRegistry.TelegramHandles(text));
}
