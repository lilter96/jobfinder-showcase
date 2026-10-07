# Architecture dependencies

## JSON Schema evaluation

`JsonSchema.Net` is pinned to **8.0.5**, whose published NuGet metadata declares the MIT license. This release provides a standards-based local evaluator for the existing canonical structured-output schemas. Package 9.4.0 has different binary license terms and is not treated as an interchangeable MIT upgrade.

Sources: [8.0.5 package metadata](https://api.nuget.org/v3-flatcontainer/jsonschema.net/8.0.5/jsonschema.net.nuspec), [package](https://www.nuget.org/packages/JsonSchema.Net/8.0.5), [MIT license](https://licenses.nuget.org/MIT).

Native .NET JsonSchemaExporter generates contracts as their wire DTOs move into the task catalog. Persisted JSON readers retain their existing compatibility options. A package upgrade must pass schema conformance and provider-projection tests and have its exact distributed license reviewed.
