using System.Text.Json.Serialization;
using Wolverine.Attributes;

namespace JobFinder.Pipeline.Modules.Discovery;

public sealed class UnsupportedHhLinkCommandException() : Exception("unsupported hh-link command identity or version");

[MessageIdentity("jobfinder.discovery.read-hh-link.v1")]
public sealed record ReadHhLink
{
    public Guid RequestId { get; set; }
    public int Version { get; set; } = 1;
}

[MessageIdentity("jobfinder.discovery.link-hh-contacts.v1")]
public sealed record LinkHhContacts
{
    public Guid RequestId { get; set; }
    public int Version { get; set; } = 1;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ReadHhLink))]
[JsonSerializable(typeof(LinkHhContacts))]
public sealed partial class DiscoveryMessageJsonContext : JsonSerializerContext;
