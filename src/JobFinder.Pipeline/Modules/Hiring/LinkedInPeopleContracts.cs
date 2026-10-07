using System.Text.Json.Serialization;
using JobFinder.Core.Artifacts;
using JobFinder.Integrations.LinkedIn;
using JobFinder.Llm.Configuration;
using JobFinder.Llm.Tasks;
using Wolverine.Attributes;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed record LinkedInPeopleCard
{
    public string ProfileUrl { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed record LinkedInPeopleParsed
{
    public string Name { get; set; } = "";
    public string Headline { get; set; } = "";
    public string ProfileUrl { get; set; } = "";
    public string Details { get; set; } = "";
}

public sealed record LinkedInPeopleInput
{
    public int Version { get; set; } = 1;
    public string Company { get; set; } = "";
    public List<LinkedInPeopleCard> Cards { get; set; } = [];
    public List<LinkedInPeopleParsed> Parsed { get; set; } = [];
    public LlmOperationSnapshot Operation { get; set; } = new();
    public ArtifactRecipe Recipe { get; set; } = new();

    public LinkedInSearchPage Page() => new(Cards.Select(card => new LinkedInCard(card.ProfileUrl, card.Text)).ToList(),
        Parsed.Select(person => new LinkedInPerson(person.Name, person.Headline, person.ProfileUrl, person.Details)).ToList());
}

public sealed record LinkedInPeopleResult
{
    public int Version { get; set; } = 1;
    public LinkedInPeople? Reading { get; set; }
}

[MessageIdentity("jobfinder.hiring.read-linkedin-people.v1")]
public sealed record ReadLinkedInPeople
{
    public int Version { get; set; } = 1;
    public Guid RequestId { get; set; }
}

public sealed class UnsupportedLinkedInPeopleException() : Exception("unsupported or damaged LinkedIn people preparation");

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LinkedInPeopleInput))]
[JsonSerializable(typeof(LinkedInPeopleResult))]
[JsonSerializable(typeof(ReadLinkedInPeople))]
public sealed partial class LinkedInPeopleJsonContext : JsonSerializerContext;
