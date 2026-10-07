using System.Text.Json.Nodes;
using JobFinder.Integrations.HeadHunter;

namespace JobFinder.Tests.Sources;

public sealed class HhNegotiationsTests
{
    [Fact]
    public void Finds_negotiations_anywhere_when_the_page_shape_changes()
    {
        var state = JsonNode.Parse("""
            {
              "userType": "applicant",
              "someOtherBlock": {
                "items": [
                  { "id": 111, "state": "INVITATION", "hasNewMessages": true, "vacancy": { "id": 123456, "name": "Senior .NET", "company": { "name": "Acme" } } },
                  { "id": 112, "state": "DISCARD", "vacancyId": 654321 }
                ]
              },
              "other": { "vacancyId": 123456, "state": "RESPONSE" }
            }
            """)!;
        var list = HhNegotiations.Parse(state).OrderBy(n => n.VacancyId).ToList();
        Assert.Equal(2, list.Count);
        var acme = list.Single(n => n.VacancyId == 123456);
        // The richer sighting (topic, name, unread) wins over the bare one.
        Assert.Equal("invitation", acme.State);
        Assert.True(acme.HasUnread);
        Assert.Equal("Senior .NET", acme.VacancyName);
        Assert.Equal("discard", list.Single(n => n.VacancyId == 654321).State);
    }

    [Theory]
    [InlineData("response", "sent")]
    [InlineData("invitation", "invitation")]
    [InlineData("discard", "rejected")]
    [InlineData("offer", "offer")]
    [InlineData("hired", "hired")]
    [InlineData("something_new", null)]
    public void States_map_to_funnel_directions(string state, string? direction) => Assert.Equal(direction, HhNegotiations.Direction(state));

    [Fact]
    public void Contact_person_of_a_vacancy()
    {
        var c = HhApplier.ReadContact(JsonNode.Parse("""
            { "fio": "Анна Петрова", "email": "anna@acme.by", "phones": { "phones": [ { "country": "375", "city": "29", "number": "1234567" } ] } }
            """));
        Assert.NotNull(c);
        Assert.Equal("Анна Петрова", c.Name);
        Assert.Equal("anna@acme.by", c.Email);
        Assert.Equal("+375 29 1234567", c.Phone);
        Assert.Null(HhApplier.ReadContact(JsonNode.Parse("{}")));
    }
}

public sealed class HhNegotiationsPageTests
{
    // The shape of the live "Отклики" page (2026-09-27), trimmed to the fields the reader uses.
    private const string Page = """
        {
          "userType": "applicant",
          "applicantNegotiations": {
            "pageCount": "11", "total": 204,
            "topicList": [
              { "id": 1, "vacancyId": 137689007, "lastState": "RESPONSE", "viewedByOpponent": false, "hasNewMessages": false, "chatId": 55,
                "creationTime": "2026-09-24T12:00:20.553+03:00", "lastModified": "2026-09-27T15:43:47.496+03:00" },
              { "id": 2, "vacancyId": 135602805, "lastState": "DISCARD", "viewedByOpponent": true, "hasNewMessages": true,
                "creationTime": "2026-08-17T20:23:36.187+03:00", "lastModified": "2026-08-20T10:00:00+03:00" },
              { "id": 3, "vacancyId": 134743431, "lastState": "INTERVIEW", "viewedByOpponent": true, "hasNewMessages": false,
                "creationTime": "2026-09-01T10:00:00+03:00" },
              { "id": 4, "vacancyId": 100, "lastState": "RESPONSE", "viewedByOpponent": true }
            ]
          },
          "vacanciesShort": { "vacanciesList": [
            { "vacancyId": 137689007, "name": "Senior .NET", "company": { "name": "ООО Акме", "visibleName": "Acme" } },
            { "vacancyId": 135602805, "name": "Ведущий разработчик C#", "company": { "name": "Bip.ru" } }
          ] }
        }
        """;

    [Fact]
    public void Reads_the_topic_list_with_names_dates_and_flags()
    {
        var list = HhNegotiations.Parse(JsonNode.Parse(Page)!);
        Assert.Equal(4, list.Count);
        var acme = list.Single(n => n.VacancyId == 137689007);
        Assert.Equal("response", acme.State);
        Assert.Equal("Senior .NET", acme.VacancyName);
        Assert.Equal("Acme", acme.CompanyName);
        Assert.Equal(55, acme.ChatId);
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 12, 0, 20, 553, TimeSpan.FromHours(3)), acme.CreatedAt);
        var bip = list.Single(n => n.VacancyId == 135602805);
        Assert.True(bip.HasUnread);
        Assert.True(bip.ViewedByEmployer);
        var interview = list.Single(n => n.VacancyId == 134743431);
        Assert.Equal(interview.CreatedAt, interview.UpdatedAt); // no lastModified: the response date
    }

    [Theory]
    [InlineData("response", false, JobFinder.Core.Domain.ApplicationStage.Sent)]
    [InlineData("response", true, JobFinder.Core.Domain.ApplicationStage.Viewed)]
    [InlineData("interview", true, JobFinder.Core.Domain.ApplicationStage.HrScreen)]
    [InlineData("discard", true, JobFinder.Core.Domain.ApplicationStage.Rejected)]
    public void Funnel_stage_of_a_response(string state, bool viewed, JobFinder.Core.Domain.ApplicationStage expected) =>
        Assert.Equal(expected, JobFinder.Pipeline.Jobs.HhNegotiationsJob.StageOf(new HhNegotiation(1, state, false, null, null, null, ViewedByEmployer: viewed)));
}
