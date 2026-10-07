using System.Text.Json.Nodes;
using JobFinder.Core.Domain;
using Microsoft.Playwright;

namespace JobFinder.Integrations.Threads;

public sealed partial class ThreadsBrowserClient
{
    public async Task<ThreadsPreparedPublication> PreparePublicationAsync(ThreadsPost target, string text, ThreadsPreparedPublication? saved, CancellationToken ct)
    {
        if (!await HasSessionAsync(ct)) return (saved ?? new()) with { State = ThreadsPreparationState.Waiting, Error = "Threads browser login is missing", RetrySeconds = 300 };
        return await browser.RunAsync(Site, page => PrepareOnPageAsync(page, target, text, saved, ct), ct);
    }

    internal static async Task<ThreadsPreparedPublication> PrepareOnPageAsync(IPage page, ThreadsPost target, string text,
        ThreadsPreparedPublication? saved, CancellationToken ct)
    {
        if (!PostUrl(target.Url) || text.Length is 0 or > 500) return new() { State = ThreadsPreparationState.NotSent, Error = "invalid Threads target or comment" };
        ct.ThrowIfCancellationRequested();
        await page.GotoAsync(target.Url, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        var own = await AccountOnPageAsync(page);
        if (own.Length == 0) return new() { State = ThreadsPreparationState.Waiting, Error = "browser account could not be identified", RetrySeconds = 300 };
        if (saved is { Username.Length: > 0 } && saved.Username != own) return new() { State = ThreadsPreparationState.NotSent, Error = "browser account changed" };
        var proof = await ThreadOnPageAsync(page, target, own, text);
        return new()
        {
            State = !proof.Focused ? ThreadsPreparationState.NotSent : proof.Replies.Count > 0 ? ThreadsPreparationState.Unknown : ThreadsPreparationState.Ready,
            Username = own, AccountId = own,
            Error = !proof.Focused ? "exact target thread could not be identified" : proof.Replies.Count > 0 ? "an existing own reply needs reconciliation" : null,
        };
    }

    public Task<ThreadsPublicationReceipt> PublishPreparedAsync(ThreadsPost target, string text, ThreadsPreparedPublication prepared,
        Func<CancellationToken, Task<bool>> mayPublish, CancellationToken ct) =>
        browser.RunAsync(Site, page => PublishOnPageAsync(page, target, text, prepared, options.Selectors, mayPublish, ct), ct);

    internal static async Task<ThreadsPublicationReceipt> PublishOnPageAsync(IPage page, ThreadsPost target, string text,
        ThreadsPreparedPublication prepared, ThreadsSelectors selectors, Func<CancellationToken, Task<bool>> mayPublish, CancellationToken ct)
    {
        var effect = false;
        try
        {
            var current = await PrepareOnPageAsync(page, target, text, prepared, ct);
            if (prepared.State != ThreadsPreparationState.Ready || current.State != ThreadsPreparationState.Ready)
                return new() { Status = current.State == ThreadsPreparationState.Unknown ? DeliveryAttemptStatus.OutcomeUnknown : DeliveryAttemptStatus.KnownNotSent, Error = current.Error };
            if (await page.Locator(selectors.Composer).CountAsync() == 0) return NotSubmitted("no unambiguous inline reply composer");
            var composer = page.Locator(selectors.Composer);
            if (await page.Locator(selectors.Composer).CountAsync() != 1) return NotSubmitted("ambiguous reply composer");
            await composer.FillAsync(text);
            var send = page.Locator(selectors.PostButton);
            if (await send.CountAsync() != 1 || !await send.IsVisibleAsync()) return NotSubmitted("ambiguous reply submit control");
            if (!await ControlBelongsToTargetAsync(composer, target) || !await ControlBelongsToTargetAsync(send, target))
                return NotSubmitted("reply controls do not belong to the exact target");
            ct.ThrowIfCancellationRequested();
            if (!await mayPublish(ct)) return NotSubmitted("publication permission was revoked", retryable: true);
            ct.ThrowIfCancellationRequested();
            effect = true;
            await send.ClickAsync();
            for (var pass = 0; pass < 5; pass++)
            {
                var proof = await ThreadOnPageAsync(page, target, prepared.Username, text);
                if (proof.Focused && proof.Replies.Count == 1)
                    return new() { Status = DeliveryAttemptStatus.Confirmed, ExternalId = proof.Replies[0] };
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            }
            return new() { Status = DeliveryAttemptStatus.OutcomeUnknown, Error = "own full-text reply under the exact target was not confirmed" };
        }
        catch (Exception)
        {
            return effect ? new() { Status = DeliveryAttemptStatus.OutcomeUnknown, Error = "browser publication outcome is unknown; do not retry" }
                : NotSubmitted("browser pre-publication check unavailable", retryable: true);
        }
    }

    public static bool PostUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo) && uri.IsDefaultPort && uri.Host is "threads.com" or "www.threads.com" or "threads.net" or "www.threads.net"
        && uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) is [var author, "post", var code]
        && author.StartsWith('@') && author.Length > 1 && code.Length > 0;

    private static async Task<string> AccountOnPageAsync(IPage page) => await page.EvaluateAsync<string>("""
        () => {
          const accounts = new Set();
          for (const link of document.querySelectorAll('a[href^="/@"]')) {
            if (!link.matches('[aria-label="Profile"], [aria-label="Профиль"]') && !link.querySelector('svg[aria-label="Profile"], svg[aria-label="Профиль"]')) continue;
            const url = new URL(link.href);
            if (url.origin !== location.origin) continue;
            const match = url.pathname.match(/^\/@([a-zA-Z0-9_.]+)\/?$/);
            if (match) accounts.add(match[1].toLowerCase());
          }
          return accounts.size === 1 ? [...accounts][0] : '';
        }
        """);

    private static Task<bool> ControlBelongsToTargetAsync(ILocator control, ThreadsPost target) => control.EvaluateAsync<bool>("""
        (control, path) => {
          const targets = new Set([...document.querySelectorAll('a[href*="/post/"]')]
            .filter(link => new URL(link.href).origin === location.origin && new URL(link.href).pathname.replace(/\/$/, '') === path)
            .map(link => link.closest('[data-pressable-container], article')).filter(Boolean));
          if (targets.size !== 1) return false;
          const original = [...targets][0]; const scope = original.parentElement;
          if (scope?.closest('[data-pressable-container], article')) return false;
          if (!scope || !scope.contains(control)) return false;
          const card = control.closest('[data-pressable-container], article');
          return card === original || card === null;
        }
        """, new Uri(target.Url).AbsolutePath.TrimEnd('/'));

    private static async Task<(bool Focused, List<string> Replies)> ThreadOnPageAsync(IPage page, ThreadsPost target, string own, string text)
    {
        if (!PostUrl(page.Url) || new Uri(page.Url).AbsolutePath != new Uri(target.Url).AbsolutePath) return (false, []);
        var path = new Uri(target.Url).AbsolutePath.TrimEnd('/');
        var author = new Uri(target.Url).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[0][1..];
        if (!author.Equals(target.Username.TrimStart('@'), StringComparison.OrdinalIgnoreCase)) return (false, []);
        var json = await page.EvaluateAsync<string>("""
            ({path, own, text}) => {
              const norm = value => (value || '').replace(/\s+/g, ' ').trim();
              const targets = new Set([...document.querySelectorAll('a[href*="/post/"]')]
                .filter(link => new URL(link.href).origin === location.origin && new URL(link.href).pathname.replace(/\/$/, '') === path)
                .map(link => link.closest('[data-pressable-container], article')).filter(Boolean));
              if (targets.size !== 1) return JSON.stringify({focused:false, replies:[]});
              const original = [...targets][0]; const scope = original.parentElement;
              if (scope?.closest('[data-pressable-container], article')) return JSON.stringify({focused:false, replies:[]});
              if (!scope || scope === document.body || scope === document.documentElement) return JSON.stringify({focused:false, replies:[]});
              const replies = new Set();
              for (const card of scope.querySelectorAll('[data-pressable-container], article')) {
                if (card === original || card.parentElement !== scope) continue;
                const exact = [...card.querySelectorAll('div, span, p')].some(block =>
                  block.closest('[data-pressable-container], article') === card && norm(block.innerText) === norm(text));
                if (!exact) continue;
                for (const link of card.querySelectorAll('a[href*="/post/"]')) {
                  if (link.closest('[data-pressable-container], article') !== card) continue;
                  if (new URL(link.href).origin !== location.origin) continue;
                  const match = new URL(link.href).pathname.match(/^\/@([^/]+)\/post\/([^/?#]+)/);
                  if (match && match[1].toLowerCase() === own && new URL(link.href).pathname.replace(/\/$/, '') !== path)
                    replies.add(new URL(link.href).origin + new URL(link.href).pathname);
                }
              }
              return JSON.stringify({focused:true, replies:[...replies]});
            }
            """, new { path, own, text });
        var node = JsonNode.Parse(json)!;
        return (node["focused"]!.GetValue<bool>(), node["replies"]!.AsArray().Select(value => value!.GetValue<string>()).ToList());
    }

    private static ThreadsPublicationReceipt NotSubmitted(string error, bool retryable = false) =>
        new() { Status = DeliveryAttemptStatus.KnownNotSent, Error = error, Retryable = retryable };
}
