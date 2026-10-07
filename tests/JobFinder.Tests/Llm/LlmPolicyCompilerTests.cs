using System.Text.Json.Nodes;
using JobFinder.Llm;
using JobFinder.Llm.Client;
using JobFinder.Llm.Configuration;
using JobFinder.Llm.Tasks;
using Microsoft.Extensions.Configuration;

namespace JobFinder.Tests.Llm;

public sealed class LlmPolicyCompilerTests
{
    private static LlmOptions Options(string purpose, params LlmModelOptions[] models) => new()
    {
        Routes = new() { [purpose] = [.. models] },
    };

    [Theory]
    [InlineData(LlmPurpose.Application)]
    [InlineData(LlmPurpose.Resume)]
    [InlineData(LlmPurpose.Profile)]
    public void A_missing_known_route_never_uses_the_score_route(string purpose)
    {
        Assert.Empty(LlmPolicyCompiler.Route(Options(LlmPurpose.Score, new LlmModelOptions { Name = "score" }), purpose));
    }

    [Theory]
    [InlineData(LlmPurpose.Application, "high")]
    [InlineData(LlmPurpose.Application, "none")]
    [InlineData(LlmPurpose.Resume, "medium")]
    [InlineData(LlmPurpose.Resume, null)]
    public void Every_maximum_quality_fallback_is_validated_before_a_route_can_be_used(string purpose, string? effort)
    {
        var options = Options(purpose, new() { Name = "primary", Reasoning = new() { Effort = "xhigh" } },
            new() { Name = "fallback", Reasoning = new() { Effort = effort } });
        Assert.Throws<LlmPolicyException>(() => LlmPolicyCompiler.Route(options, purpose));
    }

    [Theory]
    [InlineData(LlmPurpose.Application, "xhigh")]
    [InlineData(LlmPurpose.Application, "max")]
    [InlineData(LlmPurpose.Resume, "xhigh")]
    [InlineData(LlmPurpose.Resume, "max")]
    public void Explicit_maximum_profiles_are_kept_on_every_model(string purpose, string effort)
    {
        var models = LlmPolicyCompiler.Route(Options(purpose, new LlmModelOptions { Name = "model", Reasoning = new() { Effort = effort } }), purpose);
        Assert.Equal(effort, Assert.Single(models).Reasoning!.Effort);
    }

    [Theory]
    [InlineData("{\"reasoning\":{\"effort\":\"xhigh\",\"enabled\":false}}")]
    [InlineData("{\"reasoning\":{\"effort\":\"ultra\"}}")]
    [InlineData("{\"reasoning\":{\"effort\":\"high\",\"max_tokens\":1}}")]
    [InlineData("{\"reasoning\":{\"budget\":\"unlimited\"}}")]
    [InlineData("{\"provider\":{\"mystery\":true}}")]
    [InlineData("{\"provider\":{\"only\":null}}")]
    [InlineData("{\"provider\":{\"require_parameters\":false}}")]
    [InlineData("{\"messages\":[]}")]
    public void Legacy_settings_are_an_import_adapter_not_an_unrestricted_request_override(string legacy)
    {
        Assert.Throws<LlmPolicyException>(() => LlmPolicyCompiler.Model(new() { Name = "model", ExtraBody = legacy }, 90));
    }

    [Fact]
    public void Typed_and_legacy_owners_cannot_compete_for_the_same_setting()
    {
        Assert.Throws<LlmPolicyException>(() => LlmPolicyCompiler.Model(new()
        {
            Name = "model", Reasoning = new() { Effort = "xhigh" }, ExtraBody = "{\"reasoning\":{\"effort\":\"xhigh\"}}",
        }, 90));
    }

    [Fact]
    public void A_compiled_snapshot_cannot_be_changed_by_mutating_its_source()
    {
        var model = new LlmModelOptions { Name = "model", Reasoning = new() { Effort = "xhigh" }, Provider = new() { Order = ["first"] } };
        var snapshot = Assert.Single(LlmPolicyCompiler.Route(Options(LlmPurpose.Application, model), LlmPurpose.Application));
        model.Reasoning.Effort = "none";
        model.Provider.Order[0] = "other";
        Assert.Equal("xhigh", snapshot.Reasoning!.Effort);
        Assert.Equal("first", Assert.Single(snapshot.Provider!.Order));
    }

    [Fact]
    public void Route_identity_uses_effective_values_not_legacy_JSON_formatting_or_property_order()
    {
        var first = Options(LlmPurpose.Resume, new LlmModelOptions { Name = "model", ExtraBody = "{\"reasoning\":{\"effort\":\"xhigh\"},\"provider\":{\"data_collection\":\"deny\"}}" });
        var second = Options(LlmPurpose.Resume, new LlmModelOptions { Name = "model", ExtraBody = " { \"provider\": { \"data_collection\":\"deny\" }, \"reasoning\": {\"effort\":\"xhigh\"} }" });
        Assert.Equal(LlmPolicyCompiler.RouteVersion(first, LlmPurpose.Resume), LlmPolicyCompiler.RouteVersion(second, LlmPurpose.Resume));
        second.Routes[LlmPurpose.Resume][0].TimeoutSec = 600;
        Assert.NotEqual(LlmPolicyCompiler.RouteVersion(first, LlmPurpose.Resume), LlmPolicyCompiler.RouteVersion(second, LlmPurpose.Resume));
    }

    [Fact]
    public void Typed_request_building_preserves_strict_provider_parameters_and_omits_empty_filters()
    {
        var model = new LlmModelOptions { Name = "model", Reasoning = new() { Effort = "xhigh" }, Provider = new() { DataCollection = "deny" } };
        var transport = new OpenRouterTransport(null!, new LlmOptions());
        var request = transport.BuildBody(model, "system", "user", Schemas.PostExtraction);
        Assert.Equal("xhigh", request["reasoning"]!["effort"]!.GetValue<string>());
        var provider = request["provider"]!.AsObject();
        Assert.True(provider["require_parameters"]!.GetValue<bool>());
        Assert.False(provider.ContainsKey("only"));
        Assert.Equal("deny", provider["data_collection"]!.GetValue<string>());
    }

    [Fact]
    public void The_public_bootstrap_routes_compile_without_weak_application_or_resume_fallbacks()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json")).Build();
        var options = new LlmOptions();
        configuration.GetSection(LlmOptions.Section).Bind(options);
        foreach (var purpose in options.Routes.Keys) Assert.NotEmpty(LlmPolicyCompiler.Route(options, purpose));
        Assert.All(LlmPolicyCompiler.Route(options, LlmPurpose.Application), model => Assert.Null(model.ExtraBody));
    }
}
