using System.Globalization;
using System.Text.Json.Nodes;
using JobFinder.Integrations.Browser;
using Microsoft.Playwright;

namespace JobFinder.Integrations.HeadHunter;

/// <summary>One of the user's hh responses as the "Отклики" page shows it.</summary>
/// <param name="State">The response's lastState, lower-cased: response, invitation, interview, discard, offer, hired…</param>
/// <param name="ViewedByEmployer">The employer opened the response (hh: viewedByOpponent).</param>
public sealed record HhNegotiation(long VacancyId, string State, bool HasUnread, string? TopicId, string? VacancyName, string? CompanyName,
    DateTimeOffset? CreatedAt = null, DateTimeOffset? UpdatedAt = null, bool ViewedByEmployer = false, long? ChatId = null);

public sealed record HhContact(string? Name, string? Email, string? Phone);

/// <summary>
/// The applicant's negotiations under the saved session. The page state (checked on the live page, 2026-09-27) holds
/// <c>applicantNegotiations.topicList</c> — 20 responses per page with vacancyId, lastState, viewedByOpponent, hasNewMessages, chatId and
/// dates — and <c>vacanciesShort.vacanciesList</c> with the vacancy names and companies. Every page is read, with a pause between pages;
/// the first page's state is written to <c>data/debug</c> for checks.
/// </summary>
public sealed class HhNegotiations(BrowserSessions browser, HhOptions options, ILogger<HhNegotiations> log)
{
    private static readonly string[] StateKeys = ["state", "@state", "status", "topicState", "negotiationState", "employerState", "applicantState"];

    public Task<List<HhNegotiation>> ReadAsync(string debugDir, CancellationToken ct, int maxPages = 20) =>
        browser.RunAsync(HhApplier.Site, async page =>
        {
            var byVacancy = new Dictionary<long, HhNegotiation>();
            var pages = 1;
            for (var p = 0; p < pages && p < maxPages; p++)
            {
                var url = $"https://{options.ApplyHost}/applicant/negotiations" + (p == 0 ? "" : $"?page={p}");
                await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                await page.WaitForTimeoutAsync(Random.Shared.Next(1500, 3000));
                var json = await page.EvaluateAsync<string?>(
                    "() => { const t = document.getElementById('HH-Lux-InitialState'); return t ? (t.content ? t.content.textContent : t.textContent) : null; }");
                if (string.IsNullOrEmpty(json)) throw new InvalidOperationException($"no page state on {url}");
                var state = JsonNode.Parse(json)!;
                if (state["userType"]?.ToString() != "applicant") throw new InvalidOperationException("hh session expired (--login hh)");
                if (p == 0)
                {
                    Directory.CreateDirectory(debugDir);
                    await File.WriteAllTextAsync(Path.Combine(debugDir, "hh-negotiations-state.json"), json, ct);
                    pages = int.TryParse(state["applicantNegotiations"]?["pageCount"]?.ToString(), out var n) ? Math.Max(1, n) : 1;
                }

                foreach (var item in Parse(state))
                {
                    if (!byVacancy.TryGetValue(item.VacancyId, out var known) || (item.UpdatedAt ?? DateTimeOffset.MinValue) > (known.UpdatedAt ?? DateTimeOffset.MinValue))
                        byVacancy[item.VacancyId] = item;
                }
            }

            log.Info($"[hh-negotiations] {byVacancy.Count} responses read from {Math.Min(pages, maxPages)} page(s)");
            return byVacancy.Values.ToList();
        }, ct);

    internal static List<HhNegotiation> Parse(JsonNode state)
    {
        if (state["applicantNegotiations"]?["topicList"] is JsonArray topics) return ParseTopics(state, topics);
        return ParseAnywhere(state);
    }

    private static List<HhNegotiation> ParseTopics(JsonNode state, JsonArray topics)
    {
        var names = new Dictionary<long, (string? Name, string? Company)>();
        if (state["vacanciesShort"]?["vacanciesList"] is JsonArray list)
        {
            foreach (var v in list)
            {
                if (HhStateParser.Long(v?["vacancyId"]) is { } id)
                    names[id] = (HhStateParser.Str(v?["name"]), HhStateParser.Str(v?["company"]?["visibleName"]) ?? HhStateParser.Str(v?["company"]?["name"]));
            }
        }

        var result = new List<HhNegotiation>();
        foreach (var t in topics)
        {
            if (HhStateParser.Long(t?["vacancyId"]) is not { } vacancyId) continue;
            var lastState = HhStateParser.Str(t?["lastState"]) ?? HhStateParser.Str(t?["initialState"]);
            if (lastState is null) continue;
            names.TryGetValue(vacancyId, out var name);
            result.Add(new HhNegotiation(
                vacancyId,
                lastState.ToLower(CultureInfo.InvariantCulture),
                HhStateParser.Bool(t?["hasNewMessages"]),
                HhStateParser.Str(t?["id"]),
                name.Name,
                name.Company,
                Date(t?["creationTime"]),
                Date(t?["lastModified"]) ?? Date(t?["creationTime"]),
                HhStateParser.Bool(t?["viewedByOpponent"]),
                HhStateParser.Long(t?["chatId"])));
        }

        return result;
    }

    /// <summary>Any object that carries a vacancy id with a state-like field: the fallback when the page's shape changes.</summary>
    private static List<HhNegotiation> ParseAnywhere(JsonNode state)
    {
        var byVacancy = new Dictionary<long, HhNegotiation>();
        Walk(state);
        return byVacancy.Values.ToList();

        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonArray array:
                    foreach (var item in array) Walk(item);
                    break;
                case JsonObject obj:
                    TryRead(obj);
                    foreach (var (_, child) in obj) Walk(child);
                    break;
            }
        }

        void TryRead(JsonObject obj)
        {
            var vacancyId = HhStateParser.Long(obj["vacancyId"]) ?? HhStateParser.Long(obj["vacancy"]?["id"]) ?? HhStateParser.Long(obj["vacancy"]?["vacancyId"]);
            if (vacancyId is null) return;
            var stateValue = StateKeys.Select(k => HhStateParser.Str(obj[k]) ?? HhStateParser.Str(obj[k]?["id"])).FirstOrDefault(s => s is not null)
                             ?? HhStateParser.Str(obj["lastState"]);
            if (stateValue is null) return;
            var unread = HhStateParser.Bool(obj["hasNewMessages"]) || HhStateParser.Bool(obj["hasUpdates"])
                         || (HhStateParser.Long(obj["unreadCount"]) ?? HhStateParser.Long(obj["newMessagesCount"]) ?? 0) > 0;
            var topic = HhStateParser.Str(obj["topicId"]) ?? HhStateParser.Str(obj["id"]);
            var name = HhStateParser.Str(obj["vacancyName"]) ?? HhStateParser.Str(obj["vacancy"]?["name"]);
            var company = HhStateParser.Str(obj["employerName"]) ?? HhStateParser.Str(obj["employer"]?["name"]) ?? HhStateParser.Str(obj["vacancy"]?["company"]?["name"]);
            var item = new HhNegotiation(vacancyId.Value, stateValue.ToLower(CultureInfo.InvariantCulture), unread, topic, name, company);
            // The richest sighting wins (the same vacancy appears in several blocks of the page state).
            if (!byVacancy.TryGetValue(vacancyId.Value, out var known) || Rank(item) > Rank(known)) byVacancy[vacancyId.Value] = item;
        }

        static int Rank(HhNegotiation n) => (n.TopicId is null ? 0 : 1) + (n.VacancyName is null ? 0 : 1) + (n.HasUnread ? 1 : 0);
    }

    private static DateTimeOffset? Date(JsonNode? node) =>
        HhStateParser.Str(node) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>
    /// hh state names → a funnel direction. Unknown names return null (logged by the caller) rather than a guess.
    /// </summary>
    public static string? Direction(string state) => state switch
    {
        "response" or "responses" or "sent" or "new" => "sent",
        "viewed" or "seen" or "read" or "response_viewed" => "viewed",
        "invitation" or "interview" or "invited" or "assessment" or "phone_interview" => "invitation",
        "offer" or "job_offer" => "offer",
        "discard" or "discarded" or "rejected" or "reject" or "declined" or "discard_by_employer" or "refusal" => "rejected",
        "hired" => "hired",
        _ => null,
    };
}
