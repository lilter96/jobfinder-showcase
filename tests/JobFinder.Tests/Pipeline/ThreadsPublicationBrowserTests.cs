using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Integrations.Threads;
using Microsoft.Playwright;

namespace JobFinder.Tests.Pipeline;

public sealed class ThreadsPublicationBrowserTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly ThreadsPost Target = new("original", "author", "Original post", null, "https://www.threads.com/@author/post/original");
    private const string Comment = "Я бы договаривался о сроке обратной связи до следующего этапа.";
    private static readonly ThreadsPreparedPublication Ready = new() { State = ThreadsPreparationState.Ready, Username = "candidate", AccountId = "candidate" };
    private static readonly ThreadsSelectors Selectors = new() { Composer = "[contenteditable]", PostButton = "#send" };
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;
    private IPage _page = null!;

    public async ValueTask InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
        _page = await _browser.NewPageAsync();
        _page.SetDefaultTimeout(3000);
    }

    public async ValueTask DisposeAsync()
    {
        await _browser.DisposeAsync();
        _playwright.Dispose();
    }

    private async Task PageAsync(string replies = "", string own = "candidate", string controls = "", string effect = "candidate", bool nested = false)
    {
        var reply = "<article><a href='/@" + effect + "/post/reply'>Published reply</a><p>" + Comment + "</p></article>";
        var html = $$"""
            <!doctype html><html><head><meta charset='utf-8'></head><body><nav><a href='/@{{own}}' aria-label='Profile'>Profile</a></nav>
            <main><section id='thread'>{{(nested ? "<article>" : "")}}<article id='original'><a href='/@author/post/original'>Original</a><p>Original post</p></article>{{(nested ? "</article>" : "")}}
            {{replies}}<div contenteditable='true' role='textbox'></div>
            <button id='send' onclick='document.getElementById("thread").insertAdjacentHTML("beforeend", {{JsonSerializer.Serialize(reply)}}); window.sent = (window.sent || 0) + 1'>Post</button>
            </section>{{controls}}</main></body></html>
            """;
        await _page.RouteAsync("**/*", route => route.Request.Url == Target.Url
            ? route.FulfillAsync(new() { Status = 200, ContentType = "text/html", Body = html }) : route.AbortAsync());
    }

    [Fact]
    public async Task A_new_own_full_text_reply_in_the_exact_thread_is_a_confirmed_receipt()
    {
        await PageAsync();
        var receipt = await ThreadsBrowserClient.PublishOnPageAsync(_page, Target, Comment, Ready, Selectors, _ => Task.FromResult(true), Ct);
        Assert.Equal(DeliveryAttemptStatus.Confirmed, receipt.Status);
        Assert.Equal("https://www.threads.com/@candidate/post/reply", receipt.ExternalId);
        Assert.Equal(1, await _page.EvaluateAsync<int>("window.sent"));
    }

    [Fact]
    public async Task Existing_own_full_text_is_unknown_and_is_never_resubmitted()
    {
        await PageAsync("<article><a href='/@candidate/post/existing'>Earlier</a><p>" + Comment + "</p></article>");
        var prepared = await ThreadsBrowserClient.PrepareOnPageAsync(_page, Target, Comment, null, Ct);
        Assert.Equal(ThreadsPreparationState.Unknown, prepared.State);
        var receipt = await ThreadsBrowserClient.PublishOnPageAsync(_page, Target, Comment, Ready, Selectors, _ => Task.FromResult(true), Ct);
        Assert.Equal(DeliveryAttemptStatus.OutcomeUnknown, receipt.Status);
        Assert.Equal(0, await _page.EvaluateAsync<int>("window.sent || 0"));
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("snippet")]
    [InlineData("quoted")]
    [InlineData("related")]
    [InlineData("external-link")]
    public async Task A_snippet_other_author_quote_or_related_section_is_not_a_publication_proof(string kind)
    {
        var author = kind == "foreign" ? "other" : "candidate";
        var text = kind == "snippet" ? Comment[..40] : Comment;
        var link = kind == "external-link" ? "https://external.invalid/@candidate/post/earlier" : "/@" + author + "/post/earlier";
        var reply = "<article><a href='" + link + "'>Earlier</a><p>" + text + "</p></article>";
        if (kind is "quoted" or "related") reply = "<aside>" + reply + "</aside>";
        await PageAsync(reply);
        Assert.Equal(ThreadsPreparationState.Ready, (await ThreadsBrowserClient.PrepareOnPageAsync(_page, Target, Comment, null, Ct)).State);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("target-author")]
    [InlineData("nested-target")]
    [InlineData("composer")]
    [InlineData("submit")]
    public async Task Changed_account_target_or_ambiguous_controls_refuse_the_click(string problem)
    {
        var controls = problem switch { "composer" => "<div contenteditable='true'></div>", "submit" => "<button id='send'>Other</button>", _ => "" };
        await PageAsync(own: problem == "account" ? "other" : "candidate", controls: controls, nested: problem == "nested-target");
        var target = problem == "target-author" ? Target with { Username = "different" } : Target;
        var receipt = await ThreadsBrowserClient.PublishOnPageAsync(_page, target, Comment, Ready, Selectors, _ => Task.FromResult(true), Ct);
        Assert.Equal(DeliveryAttemptStatus.KnownNotSent, receipt.Status);
        Assert.Equal(0, await _page.EvaluateAsync<int>("window.sent || 0"));
    }

    [Fact]
    public async Task Permission_revocation_immediately_before_click_is_known_not_sent()
    {
        await PageAsync();
        var receipt = await ThreadsBrowserClient.PublishOnPageAsync(_page, Target, Comment, Ready, Selectors, _ => Task.FromResult(false), Ct);
        Assert.Equal(DeliveryAttemptStatus.KnownNotSent, receipt.Status);
        Assert.True(receipt.Retryable);
        Assert.Equal(0, await _page.EvaluateAsync<int>("window.sent || 0"));
    }

    [Fact]
    public async Task A_click_followed_by_an_unproved_reply_is_unknown_not_a_retry()
    {
        await PageAsync(effect: "other");
        var receipt = await ThreadsBrowserClient.PublishOnPageAsync(_page, Target, Comment, Ready, Selectors, _ => Task.FromResult(true), Ct);
        Assert.Equal(DeliveryAttemptStatus.OutcomeUnknown, receipt.Status);
        Assert.False(receipt.Retryable);
    }

    [Theory]
    [InlineData("https://threads.com.evil.invalid/@author/post/id")]
    [InlineData("https://user:password@www.threads.com/@author/post/id")]
    [InlineData("http://www.threads.com/@author/post/id")]
    [InlineData("https://www.threads.com:8081/@author/post/id")]
    public void Browser_target_must_be_an_official_HTTPS_post(string url) => Assert.False(ThreadsBrowserClient.PostUrl(url));
}
