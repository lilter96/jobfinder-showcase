using System.Text.Json;
using JobFinder.Llm;
using JobFinder.Llm.Configuration;
using Microsoft.Extensions.Configuration;

namespace JobFinder.Tests.Llm;

public sealed class LlmRuntimePolicyTests
{
    internal static LlmOptions Bootstrap() => new()
    {
        ApiKey = "synthetic-secret-not-for-policy", Endpoint = "https://example.com/completions", Proxy = "http://localhost:8081",
        TimeoutSec = 120, DailyBudgetUsd = 3.5m, MaxAttempts = 3,
        Routes = new()
        {
            [LlmPurpose.Score] = [new() { Name = "shared-model", MaxTokens = 1000, ExtraBody = """{"reasoning":{"effort":"none"}}""" }],
            [LlmPurpose.Application] = [new() { Name = "shared-model", MaxTokens = 24000, TimeoutSec = 600, Reasoning = new() { Effort = "xhigh" } }],
            [LlmPurpose.Resume] = [new() { Name = "shared-model", MaxTokens = 32000, TimeoutSec = 600, Reasoning = new() { Effort = "xhigh" } }],
        },
    };

    [Fact]
    public void Import_preserves_effective_routes_with_one_shared_model_registry_and_no_deployment_secrets()
    {
        var bootstrap = Bootstrap();
        var policy = LlmRuntimePolicyCompiler.Import(bootstrap);
        var compiled = LlmRuntimePolicyCompiler.Compile(policy, bootstrap);
        Assert.Single(policy.Models);
        Assert.Equal(bootstrap.DailyBudgetUsd, compiled.DailyBudgetUsd);
        Assert.Equal(bootstrap.MaxAttempts, compiled.MaxAttempts);
        Assert.Equal(bootstrap.ApiKey, compiled.ApiKey);
        Assert.Equal(bootstrap.Endpoint, compiled.Endpoint);
        Assert.Equal(bootstrap.Proxy, compiled.Proxy);
        foreach (var purpose in bootstrap.Routes.Keys)
            Assert.Equal(LlmPolicyCompiler.RouteVersion(bootstrap, purpose), LlmPolicyCompiler.RouteVersion(compiled, purpose));
        var json = JsonSerializer.Serialize(policy, RuntimePolicyJsonContext.Default.LlmRuntimePolicy);
        Assert.DoesNotContain(bootstrap.ApiKey, json);
        Assert.DoesNotContain(bootstrap.Endpoint, json);
        Assert.DoesNotContain(bootstrap.Proxy!, json);
        Assert.DoesNotContain("extraBody", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("maximum", policy.Routes[LlmPurpose.Application].QualityProfile);
    }

    [Fact]
    public void Runtime_policy_is_the_only_owner_after_import_not_changed_bootstrap_preferences()
    {
        var bootstrap = Bootstrap();
        var policy = LlmRuntimePolicyCompiler.Import(bootstrap);
        bootstrap.DailyBudgetUsd = 99;
        bootstrap.MaxAttempts = 99;
        bootstrap.TimeoutSec = 1;
        bootstrap.Routes.Clear();
        var compiled = LlmRuntimePolicyCompiler.Compile(policy, bootstrap);
        Assert.Equal(3.5m, compiled.DailyBudgetUsd);
        Assert.Equal(3, compiled.MaxAttempts);
        Assert.Equal(120, compiled.Routes[LlmPurpose.Score][0].TimeoutSec);
        Assert.Equal(3, compiled.Routes.Count);
    }

    [Theory]
    [InlineData("purpose")]
    [InlineData("model")]
    [InlineData("profile")]
    [InlineData("effort")]
    [InlineData("capability")]
    [InlineData("weak-profile")]
    [InlineData("weak-fallback")]
    [InlineData("tokens")]
    [InlineData("disabled")]
    [InlineData("schema-version")]
    public void Invalid_registry_routes_capabilities_and_quality_floors_are_rejected(string invalid)
    {
        var bootstrap = Bootstrap();
        var policy = LlmRuntimePolicyCompiler.Import(bootstrap);
        var route = policy.Routes[LlmPurpose.Application];
        var step = route.Steps[0];
        switch (invalid)
        {
            case "purpose": policy.Routes["Typo"] = new(); break;
            case "model": step.ModelId = "missing"; break;
            case "profile": route.QualityProfile = "missing"; break;
            case "effort": step.Reasoning!.Effort = "ultra"; break;
            case "capability": policy.Models[step.ModelId].ReasoningEfforts = ["none"]; break;
            case "weak-profile": route.QualityProfile = "standard"; break;
            case "weak-fallback": route.Steps.Add(new() { ModelId = step.ModelId, Reasoning = new() { Effort = "none" } }); break;
            case "tokens": step.MaxTokens = 0; break;
            case "disabled": step.Reasoning!.Enabled = false; break;
            case "schema-version": policy.SchemaVersion = 2; break;
        }
        Assert.Throws<LlmPolicyException>(() => LlmRuntimePolicyCompiler.Compile(policy, bootstrap));
    }

    [Fact]
    public void Compiled_operation_objects_do_not_share_mutable_registry_or_route_settings()
    {
        var policy = LlmRuntimePolicyCompiler.Import(Bootstrap());
        var compiled = LlmRuntimePolicyCompiler.Compile(policy, Bootstrap());
        var version = LlmPolicyCompiler.RouteVersion(compiled, LlmPurpose.Application);
        policy.Models.Values.Single().Name = "changed";
        policy.Routes[LlmPurpose.Application].Steps[0].Reasoning!.Effort = "none";
        Assert.Equal(version, LlmPolicyCompiler.RouteVersion(compiled, LlmPurpose.Application));
    }

    [Fact]
    public void Changing_an_unrelated_policy_route_keeps_the_effective_application_identity()
    {
        var policy = LlmRuntimePolicyCompiler.Import(Bootstrap());
        var original = LlmPolicyCompiler.RouteVersion(LlmRuntimePolicyCompiler.Compile(policy, Bootstrap()), LlmPurpose.Application);
        policy.Routes[LlmPurpose.Score].Steps[0].MaxTokens++;
        Assert.Equal(original, LlmPolicyCompiler.RouteVersion(LlmRuntimePolicyCompiler.Compile(policy, Bootstrap()), LlmPurpose.Application));
        policy.Routes[LlmPurpose.Application].Steps[0].MaxTokens++;
        Assert.NotEqual(original, LlmPolicyCompiler.RouteVersion(LlmRuntimePolicyCompiler.Compile(policy, Bootstrap()), LlmPurpose.Application));
    }

    [Fact]
    public void Missing_existing_policy_fields_fail_but_new_unknown_fields_remain_tolerant()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{}", RuntimePolicyJsonContext.Default.LlmRuntimePolicy));
        var json = JsonSerializer.Serialize(LlmRuntimePolicyCompiler.Import(Bootstrap()), RuntimePolicyJsonContext.Default.LlmRuntimePolicy);
        var withNewField = json[..^1] + ",\"futureField\":true}";
        Assert.NotNull(JsonSerializer.Deserialize(withNewField, RuntimePolicyJsonContext.Default.LlmRuntimePolicy));
    }

    [Fact]
    public void Every_public_bootstrap_route_imports_without_silently_changing_its_effective_requests()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json")).Build();
        var bootstrap = configuration.GetSection(LlmOptions.Section).Get<LlmOptions>()!;
        var policy = LlmRuntimePolicyCompiler.Import(bootstrap);
        var compiled = LlmRuntimePolicyCompiler.Compile(policy, bootstrap);
        Assert.Equal(LlmPurpose.All.Order(), compiled.Routes.Keys.Order());
        foreach (var purpose in compiled.Routes.Keys)
            Assert.Equal(LlmPolicyCompiler.RouteVersion(bootstrap, purpose), LlmPolicyCompiler.RouteVersion(compiled, purpose));
    }
}
