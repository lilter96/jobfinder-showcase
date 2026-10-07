using JobFinder.Core.Domain;
using JobFinder.Pipeline.Modules.Discovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurablePostProcessingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hh_posts_atomically_link_the_structured_vacancy_without_paid_extraction(bool existing)
    {
        var steps = new Steps { HeadHunter = true };
        var host = await HostAsync(true, steps);
        var post = await PostAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.RawPosts.Where(row => row.Id == post.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.Text, row => row.Text + " https://hh.ru/vacancy/77777"), Ct);
        if (existing)
        {
            var vacancy = new Vacancy
            {
                Title = "Existing hh", Description = "Structured", Url = "https://hh.ru/vacancy/77777", Fingerprint = "hh-77777", Status = VacancyStatus.Scored,
                FirstSeenAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                Sources = [new() { Source = SourceKind.HeadHunter, ExternalId = "77777", Url = "https://hh.ru/vacancy/77777", FetchedAt = DateTimeOffset.UtcNow }],
            };
            context.Vacancies.Add(vacancy);
            await context.SaveChangesAsync(Ct);
            steps.ExistingHhId = vacancy.Id;
        }
        var id = await RequestAsync(host, post);
        await UntilAsync(async () => (await RootAsync(id)).Stage == PostProcessingStage.Completed);
        Assert.Single(await context.Vacancies.ToListAsync(Ct));
        Assert.Equal(2, await context.VacancySources.CountAsync(Ct));
        Assert.Equal(0, steps.Extractions);
        Assert.Equal(0, steps.Verifications);
        Assert.Equal(existing ? steps.ExistingHhId : (await context.Vacancies.SingleAsync(Ct)).Id, (await context.RawPosts.SingleAsync(Ct)).VacancyId);
    }

    [Fact]
    public async Task Contacts_changed_before_refresh_commit_reverify_without_recrawling_or_repeating_paid_work()
    {
        var steps = new Steps { Partial = true, HoldVerification = true, Database = pg.Db };
        var host = await HostAsync(true, steps);
        var post = await PostAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var vacancy = new Vacancy
        {
            Title = "Senior .NET Developer", Description = post.Text, CompanyName = "Acme", Fingerprint = "synthetic-refresh", Status = VacancyStatus.Scored,
            Url = post.Url!, Contacts = [new() { Kind = ContactKind.Email, Value = "old@acme.io" }],
            FirstSeenAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            Sources = [new() { Source = SourceKind.Telegram, ExternalId = post.ChatRef + "/" + post.ExternalId, Url = post.Url!, FetchedAt = DateTimeOffset.UtcNow }],
        };
        context.Vacancies.Add(vacancy);
        await context.SaveChangesAsync(Ct);
        await context.RawPosts.Where(row => row.Id == post.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, RawPostStatus.Vacancy).SetProperty(row => row.VacancyId, vacancy.Id), Ct);
        var id = (await host.Services.GetRequiredService<PostProcessingRequests>().RequestAsync(post.Id, Ct, refresh: true))!.Value;
        await steps.Entered.Task.WaitAsync(Ct);
        vacancy.Contacts.Add(new() { Kind = ContactKind.Email, Value = "new@acme.io" });
        await context.SaveChangesAsync(Ct);
        steps.Release.TrySetResult();
        await UntilAsync(async () => (await RootAsync(id)).Stage == PostProcessingStage.Completed);
        var current = await context.Vacancies.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(new[] { "old@acme.io", "new@acme.io", "jobs@acme.io" }, current.Contacts.Select(contact => contact.Value));
        Assert.Equal(1, steps.Reads);
        Assert.Equal(1, steps.Extractions);
        Assert.Equal(2, steps.Verifications);
    }
}
