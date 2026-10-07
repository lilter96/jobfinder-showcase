using JobFinder.Core.Domain;
using JobFinder.Integrations.Threads;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Tests.Pipeline;

public sealed class ThreadsCommentTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 18, 0, 0, TimeSpan.Zero);

    private static CommentDraft Draft(params string[] variants) => new()
    {
        PostId = "p", PostUrl = "https://www.threads.com/@a/post/p", Author = "a", PostText = "пост", Variants = [.. variants], CreatedAt = Now,
    };

    [Fact]
    public void Auto_publish_approves_the_first_clean_variant_at_once()
    {
        var draft = Draft("  Первый вариант  ", "второй");
        ThreadsJob.Decide(draft, worthCommenting: true, autoPublish: true, Now);

        Assert.Equal(CommentStatus.Approved, draft.Status);
        Assert.Equal("Первый вариант", draft.ChosenText);
        Assert.Equal(Now, draft.DecidedAt);
    }

    [Fact]
    public void A_variant_over_the_threads_limit_is_passed_over()
    {
        var draft = Draft(new string('а', 501), "короткий");
        ThreadsJob.Decide(draft, true, true, Now);
        Assert.Equal("короткий", draft.ChosenText);
    }

    [Fact]
    public void Nothing_usable_or_not_worth_it_is_rejected_in_both_modes()
    {
        foreach (var auto in new[] { true, false })
        {
            var notWorth = Draft("вариант");
            ThreadsJob.Decide(notWorth, false, auto, Now);
            Assert.Equal(CommentStatus.Rejected, notWorth.Status);

            var empty = Draft(" ", new string('а', 600));
            ThreadsJob.Decide(empty, true, auto, Now);
            Assert.Equal(CommentStatus.Rejected, empty.Status);
            Assert.Null(empty.ChosenText);
        }
    }

    [Fact]
    public void Without_auto_publish_the_draft_waits_for_the_user()
    {
        var draft = Draft("вариант");
        ThreadsJob.Decide(draft, true, false, Now);
        Assert.Equal(CommentStatus.Pending, draft.Status);
        Assert.Null(draft.ChosenText);
    }

    [Fact]
    public void Auto_publish_is_the_default()
    {
        Assert.True(new ThreadsOptions().AutoPublish);
    }

    [Theory]
    [InlineData("Короткий ответ", "Короткий ответ")]
    [InlineData("А что  именно\nбыло в том посте, из-за чего люди так взорвались?", "А что именно было в том посте, из-за")]
    public void The_reply_is_looked_up_by_its_start_on_a_word_boundary(string text, string snippet) =>
        Assert.Equal(snippet, ThreadsBrowserClient.Snippet(text));

    [Fact]
    public void The_published_report_shows_the_comment_and_the_post()
    {
        var draft = Draft("вариант");
        draft.Id = 5;
        draft.ChosenText = "Мой <ответ>";
        var text = Cards.CommentPosted(draft);

        Assert.StartsWith("✅ <b>Комментарий опубликован</b>", text);
        Assert.Contains("💬 Мой &lt;ответ&gt;", text);
        Assert.Contains("#c5", text);
    }
}

public sealed class ThreadsDailyLimitTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Pending_cards_left_from_approval_mode_do_not_use_up_an_auto_publish_day()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        var since = DateTimeOffset.UtcNow.AddHours(-1);
        var author = Guid.NewGuid().ToString("N");
        await using (var ctx = await pg.Db.CreateDbContextAsync(Ct))
        {
            foreach (var status in new[] { CommentStatus.Pending, CommentStatus.Pending, CommentStatus.Approved, CommentStatus.Posted, CommentStatus.Rejected, CommentStatus.Failed })
                ctx.CommentDrafts.Add(new CommentDraft
                {
                    PostId = Guid.NewGuid().ToString("N"), PostUrl = "u", Author = author, PostText = "t", Status = status, CreatedAt = DateTimeOffset.UtcNow,
                });
            ctx.CommentDrafts.Add(new CommentDraft
            {
                PostId = Guid.NewGuid().ToString("N"), PostUrl = "u", Author = author, PostText = "t", Status = CommentStatus.Posted,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            });
            await ctx.SaveChangesAsync(Ct);
        }

        await using var check = await pg.Db.CreateDbContextAsync(Ct);
        var mine = check.CommentDrafts.Where(d => d.Author == author);
        Assert.Equal(2, await mine.CountAsync(ThreadsJob.CountsTowardDailyLimit(since, autoPublish: true), Ct));
        Assert.Equal(5, await mine.CountAsync(ThreadsJob.CountsTowardDailyLimit(since, autoPublish: false), Ct));
    }
}
