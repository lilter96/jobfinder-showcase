using JobFinder.Core.Artifacts;
using JobFinder.Llm;
using JobFinder.Llm.Contracts;
using JobFinder.Llm.Prompts;
using JobFinder.Llm.Tasks;

namespace JobFinder.Tests.Llm;

public sealed class GenerationRecipeTests
{
    private static ArtifactRecipe Recipe(PromptCatalog? prompts = null, string route = "route-v1", string? input = "question",
        string rules = "quality-v1") => GenerationRecipe.Capture(prompts ?? new PromptCatalog(Fixture.PromptsDir), _ => route,
        [(LlmTaskCatalog.HrAnswer, LlmPurpose.InterviewMedium)], new Dictionary<string, string?> { ["question"] = input },
        new Dictionary<string, string> { ["quality"] = rules });

    [Fact]
    public void A_recipe_captures_catalog_contract_prompt_route_and_processing_versions()
    {
        var recipe = Recipe();
        var task = Assert.Single(recipe.Tasks);
        Assert.Equal(LlmTaskCatalog.HrAnswer.Id, task.TaskId);
        Assert.Equal(LlmTaskCatalog.HrAnswer.ContractVersion, task.ContractVersion);
        Assert.Equal(LlmTaskCatalog.HrAnswer.RulesVersion, task.RulesVersion);
        Assert.Equal("interviewmedium", task.Purpose);
        Assert.Equal("route-v1", task.RouteVersion);
        Assert.Equal("canonical-v1", task.ProviderProjectionVersion);
        Assert.Equal(64, task.PromptVersion.Length);
        Assert.Equal(64, recipe.Hash.Length);
    }

    [Fact]
    public void Prompt_route_rules_or_static_input_changes_invalidate_but_unrelated_prompts_do_not()
    {
        var original = Recipe().Hash;
        Assert.NotEqual(original, Recipe(route: "route-v2").Hash);
        Assert.NotEqual(original, Recipe(input: "another-question").Hash);
        Assert.NotEqual(original, Recipe(rules: "quality-v2").Hash);
        var catalog = new PromptCatalog(Fixture.PromptsDir);
        var templates = PromptCatalog.Required.ToDictionary(name => name, catalog.Template, StringComparer.OrdinalIgnoreCase);
        templates["score_vacancy"] += "changed";
        Assert.Equal(original, Recipe(new PromptCatalog(templates)).Hash);
        templates["hr_answer"] += "changed";
        Assert.NotEqual(original, Recipe(new PromptCatalog(templates)).Hash);
    }

    [Fact]
    public void Schema_processing_and_projection_changes_have_distinct_identities()
    {
        var recipe = Recipe();
        var original = recipe.Hash;
        var task = recipe.Tasks[0];
        recipe.Tasks[0] = task with { ContractVersion = "new-schema" };
        Assert.NotEqual(original, recipe.Hash);
        recipe.Tasks[0] = task with { RulesVersion = "new-task-rules" };
        Assert.NotEqual(original, recipe.Hash);
        recipe.Tasks[0] = task with { ProviderProjectionVersion = "new-projection" };
        Assert.NotEqual(original, recipe.Hash);
    }

    [Fact]
    public void Property_order_does_not_change_identity_but_task_execution_order_and_nulls_do()
    {
        var first = Recipe();
        first.Inputs = new() { ["a"] = "1", ["b"] = "2" };
        var second = Recipe();
        second.Inputs = new() { ["b"] = "2", ["a"] = "1" };
        Assert.Equal(first.Hash, second.Hash);
        Assert.NotEqual(Recipe(input: null).Hash, Recipe(input: "").Hash);
        first.Tasks.Add(first.Tasks[0] with { TaskId = "review" });
        second.Tasks = first.Tasks.AsEnumerable().Reverse().ToList();
        Assert.NotEqual(first.Hash, second.Hash);
    }

    [Fact]
    public void Captured_static_inputs_are_independent_of_mutable_callers_and_recipe_roundtrips()
    {
        var inputs = new Dictionary<string, string?> { ["profile"] = "original" };
        var rules = new Dictionary<string, string> { ["gate"] = "v1" };
        var recipe = GenerationRecipe.Capture(new PromptCatalog(Fixture.PromptsDir), _ => "route", [(LlmTaskCatalog.Score, LlmPurpose.Score)], inputs, rules);
        var hash = recipe.Hash;
        inputs["profile"] = "changed";
        rules["gate"] = "v2";
        Assert.Equal(hash, recipe.Hash);
        var json = System.Text.Json.JsonSerializer.Serialize(recipe, RecipeJsonContext.Default.ArtifactRecipe);
        Assert.DoesNotContain("original", json);
        Assert.Equal(hash, System.Text.Json.JsonSerializer.Deserialize(json, RecipeJsonContext.Default.ArtifactRecipe)!.Hash);
    }

    [Fact]
    public void Policy_revision_is_audited_without_invalidating_unrelated_effective_routes()
    {
        var recipe = Recipe();
        var updated = recipe with { PolicyRevision = 99 };
        Assert.Equal(recipe.Hash, updated.Hash);
        Assert.Equal(99, System.Text.Json.JsonSerializer.Deserialize(
            System.Text.Json.JsonSerializer.Serialize(updated, RecipeJsonContext.Default.ArtifactRecipe), RecipeJsonContext.Default.ArtifactRecipe)!.PolicyRevision);
    }

    [Fact]
    public void Operation_identity_is_audited_without_changing_historical_cache_keys()
    {
        var legacy = Recipe();
        var json = System.Text.Json.JsonSerializer.Serialize(legacy, RecipeJsonContext.Default.ArtifactRecipe);
        Assert.DoesNotContain("operationId", json);
        var captured = legacy with { OperationId = Guid.NewGuid() };
        Assert.Equal(legacy.Hash, captured.Hash);
        Assert.Equal(captured.Hash, (captured with { OperationId = Guid.NewGuid(), PolicyRevision = 999 }).Hash);
        var restored = System.Text.Json.JsonSerializer.Deserialize(
            System.Text.Json.JsonSerializer.Serialize(captured, RecipeJsonContext.Default.ArtifactRecipe), RecipeJsonContext.Default.ArtifactRecipe)!;
        Assert.Equal(captured.OperationId, restored.OperationId);
        Assert.Equal(legacy.Hash, restored.Hash);
        Assert.Null(System.Text.Json.JsonSerializer.Deserialize(json, RecipeJsonContext.Default.ArtifactRecipe)!.OperationId);
    }

    [Fact]
    public void Unknown_task_purposes_and_empty_recipes_fail_before_generation()
    {
        Assert.Throws<ArgumentException>(() => GenerationRecipe.Capture(new PromptCatalog(Fixture.PromptsDir), _ => "route",
            [(LlmTaskCatalog.HrAnswer, LlmPurpose.Score)], new Dictionary<string, string?>()));
        Assert.Throws<ArgumentException>(() => GenerationRecipe.Capture(new PromptCatalog(Fixture.PromptsDir), _ => "route", [], new Dictionary<string, string?>()));
    }
}
