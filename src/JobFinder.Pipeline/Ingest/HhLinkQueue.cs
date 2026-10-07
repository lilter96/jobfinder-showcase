using JobFinder.Integrations.HeadHunter;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Discovery;

namespace JobFinder.Pipeline.Ingest;

/// <param name="Ids">The vacancy ids found in the text (queued now or already waiting).</param>
/// <param name="Contacts">Recruiters' Telegram usernames in the same text: linked to these vacancies once they are read.</param>
/// <param name="ViaAccount">They will be read at once under the user's session.</param>
/// <param name="PausedUntil">Not via the account and the site is paused: they wait until then.</param>
public sealed record LinksAdded(IReadOnlyList<long> Ids, IReadOnlyList<string> Contacts, bool ViaAccount, DateTimeOffset? PausedUntil);

/// <summary>A queued vacancy link and the recruiter contacts that came with it.</summary>
public sealed record PendingLink(long Id, IReadOnlyList<string> Contacts);

public sealed class HhLinkQueue(HhLinkRequests requests, IHhLinkGateway gateway)
{
    public const string Key = "hh.links.pending";

    public async Task<LinksAdded> AddAsync(string text, CancellationToken ct)
    {
        var ids = HhClient.ParseVacancyIds(text);
        var contacts = ContactRegistry.TelegramHandles(text);
        if (ids.Count == 0) return new LinksAdded([], contacts, false, null);
        var access = await gateway.AccessAsync(ct);
        await requests.EnqueueAsync(ids.Select(id => new PendingLink(id, contacts)).ToList(), ct, resumePaused: access.ViaAccount);
        return new LinksAdded(ids, contacts, access.ViaAccount, access.PausedUntil);
    }

    public Task<List<PendingLink>> PendingAsync(CancellationToken ct) => requests.PendingAsync(ct);
}
