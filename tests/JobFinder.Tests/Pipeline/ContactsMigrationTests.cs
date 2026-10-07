using JobFinder.Core.Domain;
using JobFinder.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;

namespace JobFinder.Tests.Pipeline;

/// <summary>
/// The migration from one text line per contact to typed jsonb, on rows written the old way: a fresh database is taken to the
/// migration before, filled, and moved forward (and back).
/// </summary>
public sealed class ContactsMigrationTests : IAsyncLifetime
{
    private const string Before = "20260928062410_GeneratedAnswers";
    private PostgreSqlContainer? _container;
    private string? _skip;

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await _container.StartAsync();
        }
        catch (Exception e)
        {
            _skip = $"Docker is not available: {e.Message}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }

    private JobFinderDbContext Context()
    {
        var options = new DbContextOptionsBuilder<JobFinderDbContext>();
        JobFinderDbContext.Configure(options, _container!.GetConnectionString());
        return new JobFinderDbContext(options.Options);
    }

    [Fact]
    public async Task Legacy_contact_lines_become_typed_entries_in_their_order_and_come_back_on_down()
    {
        Assert.SkipWhen(_skip is not null, _skip ?? "");
        var ct = TestContext.Current.CancellationToken;
        await using (var ctx = Context())
        {
            await ctx.GetService<IMigrator>().MigrateAsync(Before, ct);
            await ctx.Database.ExecuteSqlRawAsync("""
                INSERT INTO vacancies (title, company_name, description, url, fingerprint, status, decision, work_formats, english_required, skills,
                    first_seen_at, updated_at, notified, salary_adjustment, attempts, contacts)
                VALUES ('A', 'X', '', 'u1', 'f1', 'New', 'None', 0, 'Unknown', '{{}}', now(), now(), false, 0, 0,
                        E'@HR_Anna\n+7 (963) 9187609\nDmitrii.Nikitin.777@gmail.com\n\nhttps://t.me/Recruiter_Bob/\nпишите в личку\nhttps://talanto.work/jobs/1'),
                       ('B', 'Y', '', 'u2', 'f2', 'New', 'None', 0, 'Unknown', '{{}}', now(), now(), false, 0, 0, NULL);
                """, ct);
            await ctx.GetService<IMigrator>().MigrateAsync(null, ct);
        }

        await using (var ctx = Context())
        {
            var rows = await ctx.Vacancies.AsNoTracking().OrderBy(v => v.Title).ToListAsync(ct);
            Assert.Equal(
            [
                (ContactKind.Telegram, "@hr_anna"),
                (ContactKind.Phone, "+79639187609"),
                (ContactKind.Email, "dmitrii.nikitin.777@gmail.com"),
                (ContactKind.Telegram, "@recruiter_bob"),
                (ContactKind.Link, "https://talanto.work/jobs/1"),
            ], rows[0].Contacts.Select(c => (c.Kind, c.Value))); // "пишите в личку" is not a contact: dropped
            Assert.Empty(rows[1].Contacts);

            // A row written by today's code (labels and sources) reads back as written.
            rows[1].Contacts = [new VacancyContact { Kind = ContactKind.Link, Value = "https://t.me/bot?start=1", Label = "Посмотреть контакт", Source = "https://page" }];
            ctx.Vacancies.Update(rows[1]);
            await ctx.SaveChangesAsync(ct);
        }

        await using (var ctx = Context())
        {
            var b = await ctx.Vacancies.AsNoTracking().SingleAsync(v => v.Title == "B", ct);
            Assert.Equal(("Посмотреть контакт", "https://page"), (b.Contacts[0].Label, b.Contacts[0].Source));

            await ctx.GetService<IMigrator>().MigrateAsync(Before, ct);
            var text = await ctx.Database.SqlQuery<string>($"SELECT contacts AS \"Value\" FROM vacancies WHERE title = 'A'").SingleAsync(ct);
            Assert.Equal("@hr_anna\n+79639187609\ndmitrii.nikitin.777@gmail.com\n@recruiter_bob\nhttps://talanto.work/jobs/1", text);
        }
    }
}
