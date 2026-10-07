using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using JobFinder.Core.Artifacts;
using JobFinder.Llm.Contracts;

namespace JobFinder.Llm.Client;

/// <summary>Where every attempt is recorded (the llm_calls table) and where today's spend comes from.</summary>
public interface ILlmUsageSink
{
    Task RecordAsync(LlmAttempt attempt, CancellationToken ct);

    Task<decimal> SpentTodayUsdAsync(CancellationToken ct);
}

public sealed record LlmAttempt(string Purpose, string Model, int PromptTokens, int CompletionTokens, decimal CostUsd, int LatencyMs, bool Ok, string? Error)
{
    public Guid? OperationId { get; set; }
    public Guid? CallId { get; set; }
    public int? AttemptNumber { get; set; }
    public string? RequestedModel { get; set; }
    public ArtifactRecipe? Recipe { get; set; }
}

public sealed record LlmResult<T>(T Value, string Model, decimal CostUsd);

/// <summary>Every model of the route failed or was parked; <see cref="Unauthorized"/> means no route can work until the key is fixed.</summary>
public sealed class LlmUnavailableException(string message, bool unauthorized = false, bool budgetExceeded = false) : Exception(message)
{
    public bool Unauthorized { get; } = unauthorized;

    public bool BudgetExceeded { get; } = budgetExceeded;
}

/// <summary>A typed answer that knows its own invariants (score in range, required text present).</summary>
public interface IValidatable
{
    /// <returns>null when valid, else what is wrong (the attempt counts as a bad response).</returns>
    string? Validate();
}

/// <summary>
/// Routes a call through the purpose's model list: skips parked models, classifies each failure into the model's breaker, and treats
/// an answer that does not parse or validate as that model's failure. The first valid answer wins.
/// </summary>
public sealed class LlmClient(OpenRouterTransport transport, ModelHealth health, LlmOptions options, ILlmUsageSink usage, ILogger<LlmClient> log,
    Configuration.ILlmPolicySource? policies = null)
{
    public long PolicyRevision { get; private set; }
    public Guid? OperationId { get; private set; }
    internal LlmOptions Options => options;
    private Configuration.LlmRuntimePolicy? _capturedPolicy;

    public async Task<LlmClient> ForOperationAsync(CancellationToken ct)
    {
        if (OperationId is not null) return this;
        var snapshot = policies is null ? null : await policies.ReadAsync(ct);
        var policy = snapshot is null ? Configuration.LlmRuntimePolicyCompiler.Import(options) : Configuration.LlmRuntimePolicyCompiler.Clone(snapshot.Value);
        return new LlmClient(transport, health, Configuration.LlmRuntimePolicyCompiler.Compile(policy, options), usage, log)
        {
            PolicyRevision = snapshot?.Version ?? 0, OperationId = Guid.NewGuid(),
            _capturedPolicy = policy,
        };
    }

    public Configuration.LlmOperationSnapshot CaptureOperation()
    {
        if (OperationId is not { } id) throw new InvalidOperationException("capture an LLM operation before checkpointing it");
        var policy = _capturedPolicy ?? Configuration.LlmRuntimePolicyCompiler.Import(options);
        return new Configuration.LlmOperationSnapshot
        {
            OperationId = id, PolicyRevision = PolicyRevision, Policy = Configuration.LlmRuntimePolicyCompiler.Clone(policy),
        };
    }

    public LlmClient RestoreOperation(Configuration.LlmOperationSnapshot snapshot)
    {
        if (snapshot.SchemaVersion != 1 || snapshot.OperationId == Guid.Empty || snapshot.PolicyRevision < 0 || snapshot.Policy is null)
            throw new Configuration.LlmPolicyException("invalid persisted LLM operation snapshot");
        var policy = Configuration.LlmRuntimePolicyCompiler.Clone(snapshot.Policy);
        return new LlmClient(transport, health, Configuration.LlmRuntimePolicyCompiler.Compile(policy, options), usage, log)
        {
            OperationId = snapshot.OperationId, PolicyRevision = snapshot.PolicyRevision, _capturedPolicy = policy,
        };
    }

    public async Task<bool> MayAnswerAsync(string purpose, CancellationToken ct) => (await ForOperationAsync(ct)).MayAnswer(purpose);

    public async Task<LlmResult<T>> CompleteJsonAsync<T>(string purpose, string system, string user, JsonSchemaSpec schema,
        JsonTypeInfo<T> typeInfo, CancellationToken ct, bool essential = false, ArtifactRecipe? recipe = null)
        where T : class
    {
        return await RunAsync(purpose, system, user, schema, essential, raw =>
        {
            var json = ExtractJson(raw);
            Contracts.StructuredOutputValidator.Validate(json, schema);
            T? value;
            try
            {
                value = JsonSerializer.Deserialize(json, typeInfo);
            }
            catch (JsonException e)
            {
                throw new LlmCallException(LlmFailureKind.BadResponse, $"JSON does not match the schema: {e.Message}");
            }

            if (value is null) throw new LlmCallException(LlmFailureKind.BadResponse, "JSON null");
            if (value is IValidatable v && v.Validate() is { } problem) throw new LlmCallException(LlmFailureKind.BadResponse, $"invalid: {problem}");
            return value;
        }, ct, recipe);
    }

    public Task<LlmResult<string>> CompleteTextAsync(string purpose, string system, string user, CancellationToken ct, bool essential = false,
        ArtifactRecipe? recipe = null) =>
        RunAsync(purpose, system, user, null, essential, raw =>
        {
            // Free texts are letters, DMs and notes sent under the user's name: no typography that marks them as generated.
            var text = Core.Text.HumanText.Clean(StripFences(raw).Trim().Trim('"', '«', '»').Trim());
            if (text.Length == 0) throw new LlmCallException(LlmFailureKind.BadResponse, "empty text");
            return text;
        }, ct, recipe);

    internal Task<LlmResult<string>> ProbeAsync(OpenRouterTransport probeTransport, LlmModelOptions model, LlmTaskProbe probe,
        string system, ArtifactRecipe recipe, CancellationToken ct) =>
        AttemptAsync(probeTransport, probe.Payload.Purpose, model, system, probe.Payload.UserMessage, probe.Schema, raw =>
        {
            try
            {
                if (probe.ValidateResponse(raw) is { } problem)
                    throw new LlmCallException(LlmFailureKind.BadResponse, problem);
                return raw;
            }
            catch (JsonException error)
            {
                throw new LlmCallException(LlmFailureKind.BadResponse, "probe response does not match the wire contract", inner: error);
            }
        }, CreateCall(recipe, probe.Payload.Purpose, probe.Schema), 1, ct);

    /// <summary>
    /// Some model of the purpose's route is not parked now, so a call may be answered: a cheap check before expensive preparation (reading
    /// a post's pages) that would be thrown away when no model answers.
    /// </summary>
    public bool MayAnswer(string purpose) => !string.IsNullOrWhiteSpace(options.ApiKey) && Route(purpose).Any(m => health.IsAvailable(m.Name));

    public IReadOnlyList<LlmModelOptions> Route(string purpose)
    {
        if (policies is not null) throw new InvalidOperationException("capture an LLM operation snapshot before reading a route");
        return Configuration.LlmPolicyCompiler.Route(options, purpose);
    }

    public string RouteVersion(string purpose) => policies is null ? Configuration.LlmPolicyCompiler.RouteVersion(options, purpose)
        : throw new InvalidOperationException("capture an LLM operation snapshot before computing generation provenance");

    private async Task<LlmResult<T>> RunAsync<T>(string purpose, string system, string user, JsonSchemaSpec? schema, bool essential,
        Func<string, T> accept, CancellationToken ct, ArtifactRecipe? recipe)
    {
        if (OperationId is null) return await (await ForOperationAsync(ct)).RunAsync(purpose, system, user, schema, essential, accept, ct, recipe);
        if (string.IsNullOrWhiteSpace(options.ApiKey)) throw new LlmUnavailableException("Llm:ApiKey is empty", unauthorized: true);

        // Essential calls (a user-initiated letter regeneration, the resume import) may exceed the budget; background ones may not.
        if (!essential && options.DailyBudgetUsd > 0)
        {
            var spent = await usage.SpentTodayUsdAsync(ct);
            if (spent >= options.DailyBudgetUsd)
                throw new LlmUnavailableException($"daily LLM budget spent: ${spent:0.###} of ${options.DailyBudgetUsd:0.##}", budgetExceeded: true);
        }

        var route = Route(purpose);
        if (route.Count == 0) throw new LlmUnavailableException($"no models configured for {purpose}");

        var errors = new List<string>();
        var attempts = 0;
        var call = CreateCall(recipe, purpose, schema);
        foreach (var model in route)
        {
            if (attempts >= options.MaxAttempts) break;
            if (!health.IsAvailable(model.Name))
            {
                errors.Add($"{model.Name}: parked");
                continue;
            }

            attempts++;
            try
            {
                var result = await AttemptAsync(transport, purpose, model, system, user, schema, accept, call, attempts, ct);
                health.ReportSuccess(model.Name);
                return result;
            }
            catch (LlmCallException e)
            {
                if (e.Kind == LlmFailureKind.Unauthorized)
                    throw new LlmUnavailableException($"OpenRouter rejected the key: {e.Message}", unauthorized: true);

                var cooldown = health.ReportFailure(model.Name, e.Kind, e.RetryAfter, e.Message);
                var parked = cooldown > TimeSpan.Zero ? $", parked {cooldown.TotalSeconds:0}s" : "";
                log.Warn($"[llm] {purpose} {model.Name}: {e.Kind} {e.Message}{parked}");
                errors.Add($"{model.Name}: {e.Kind}");
            }
        }

        throw new LlmUnavailableException($"{purpose}: no model answered ({string.Join("; ", errors)})");
    }

    private sealed record CallContext(Guid Id, string? RecipeJson);

    private CallContext CreateCall(ArtifactRecipe? recipe, string purpose, JsonSchemaSpec? schema)
    {
        if (OperationId is null) throw new InvalidOperationException("capture an LLM operation before recording a call");
        if (recipe is not null && (recipe.OperationId != OperationId || recipe.PolicyRevision != PolicyRevision))
            throw new ArgumentException("call provenance must belong to the captured LLM operation", nameof(recipe));
        if (recipe is not null && (recipe.Tasks.Count != 1 || !recipe.Tasks[0].Purpose.Equals(purpose, StringComparison.OrdinalIgnoreCase)
            || recipe.Tasks[0].RouteVersion != RouteVersion(purpose)
            || recipe.Tasks[0].ContractVersion != Core.Text.CanonicalJson.Hash(schema?.Schema ?? "\"plain-text-v1\"")))
            throw new ArgumentException("call provenance must match the effective task contract and route", nameof(recipe));
        return new CallContext(Guid.NewGuid(), recipe is null ? null : JsonSerializer.Serialize(recipe, RecipeJsonContext.Default.ArtifactRecipe));
    }

    private async Task<LlmResult<T>> AttemptAsync<T>(OpenRouterTransport attemptTransport, string purpose, LlmModelOptions model,
        string system, string user, JsonSchemaSpec? schema, Func<string, T> accept, CallContext call, int number, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        LlmRawResult? raw = null;
        var recipe = call.RecipeJson is null ? null : JsonSerializer.Deserialize(call.RecipeJson, RecipeJsonContext.Default.ArtifactRecipe);
        using var span = Core.Telemetry.Telemetry.Start($"llm {purpose}", ActivityKind.Client);
        span?.SetTag("llm.purpose", purpose);
        span?.SetTag("llm.model", model.Name);
        span?.SetTag("llm.attempt", number);
        span?.SetTag("llm.operation_id", OperationId);
        span?.SetTag("llm.call_id", call.Id);
        span?.SetTag("llm.recipe_hash", recipe?.Hash);
        span?.SetTag("llm.task", recipe?.Tasks.Single().TaskId);
        LlmAttempt Recorded(bool ok, string? error) => new(purpose, raw?.Model ?? model.Name, raw?.PromptTokens ?? 0,
            raw?.CompletionTokens ?? 0, raw?.CostUsd ?? 0, (int)watch.ElapsedMilliseconds, ok, error)
        {
            OperationId = OperationId, CallId = call.Id, AttemptNumber = number, RequestedModel = model.Name, Recipe = recipe,
        };
        try
        {
            raw = await attemptTransport.CompleteAsync(model, system, user, schema, ct);
            var value = accept(raw.Content);
            await RecordAsync(Recorded(true, null));
            span?.SetTag("llm.prompt_tokens", raw.PromptTokens);
            span?.SetTag("llm.completion_tokens", raw.CompletionTokens);
            span?.SetTag("llm.cost_usd", (double)raw.CostUsd);
            return new LlmResult<T>(value, raw.Model, raw.CostUsd);
        }
        catch (LlmCallException error)
        {
            raw ??= error.Raw;
            span?.SetStatus(ActivityStatusCode.Error, $"{error.Kind}: {error.Message}");
            await RecordAsync(Recorded(false, error.Message));
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            const string error = "cancelled; provider usage unavailable unless a response was received";
            span?.SetStatus(ActivityStatusCode.Error, error);
            await RecordAsync(Recorded(false, error));
            throw;
        }
    }

    private async Task RecordAsync(LlmAttempt attempt)
    {
        var tags = new TagList { { "purpose", attempt.Purpose }, { "model", attempt.Model } };
        var outcome = tags;
        outcome.Add("outcome", attempt.Ok ? "ok" : "failed");
        Core.Telemetry.Telemetry.LlmCalls.Add(1, outcome);
        Core.Telemetry.Telemetry.LlmDuration.Record(attempt.LatencyMs / 1000.0, outcome);
        var prompt = tags;
        prompt.Add("kind", "prompt");
        Core.Telemetry.Telemetry.LlmTokens.Add(attempt.PromptTokens, prompt);
        var completion = tags;
        completion.Add("kind", "completion");
        Core.Telemetry.Telemetry.LlmTokens.Add(attempt.CompletionTokens, completion);
        Core.Telemetry.Telemetry.LlmCost.Add((double)attempt.CostUsd, tags);
        try
        {
            // Accounting must not fail the call, and must not be skipped by the caller's cancellation either.
            await usage.RecordAsync(attempt, CancellationToken.None);
        }
        catch (Exception e)
        {
            log.Warn($"[llm] usage not recorded: {e.Message}");
        }
    }

    /// <summary>
    /// The first complete JSON object of the answer: prose before it and anything after it are dropped. Some models append a stray
    /// quote or brace after a valid object; scanning to the matching brace (strings and escapes respected) ignores that tail.
    /// </summary>
    internal static string ExtractJson(string raw)
    {
        var s = StripFences(raw);
        var start = s.IndexOf('{');
        if (start < 0) return s;
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return s[start..(i + 1)];
        }

        var end = s.LastIndexOf('}');
        return end > start ? s[start..(end + 1)] : s;
    }

    private static string StripFences(string raw)
    {
        var s = raw.Trim();
        if (!s.StartsWith("```", StringComparison.Ordinal)) return s;
        var firstBreak = s.IndexOf('\n');
        var lastFence = s.LastIndexOf("```", StringComparison.Ordinal);
        return firstBreak > 0 && lastFence > firstBreak ? s[(firstBreak + 1)..lastFence] : s.Trim('`');
    }
}
