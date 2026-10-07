namespace JobFinder.Llm;

/// <summary>What a call is for: each purpose has its own ordered model list (<see cref="LlmOptions.Routes"/>).</summary>
public static class LlmPurpose
{
    public const string Extract = "Extract";
    public const string Score = "Score";
    public const string Letter = "Letter";
    public const string Comment = "Comment";
    public const string Profile = "Profile";

    /// <summary>Tailored resumes, interview packs, offer analysis: long structured outputs where quality matters.</summary>
    public const string Resume = "Resume";

    /// <summary>Reading replies, drafting answers and follow-ups, outreach notes.</summary>
    public const string Reply = "Reply";

    /// <summary>
    /// The second pass for a reply that needs thought (a technical or open question, a questionnaire, a conflict of conditions) or whose
    /// first draft failed the style check: high reasoning effort, rare calls.
    /// </summary>
    public const string ReplyDeep = "ReplyDeep";

    /// <summary>
    /// The first word to an employer about a vacancy — the response letter, the message to a recruiter or to the author of a post — and
    /// the tailored resume that goes with it: what decides whether there is a conversation at all. Maximum reasoning effort.
    /// </summary>
    public const string Application = "Application";

    /// <summary>Mock interviews and the story-bank interview: short turns, conversational.</summary>
    public const string Coach = "Coach";

    /// <summary>
    /// Interview preparation at maximum reasoning effort: the plan and questions about depth (internals, "how deep did you go"). The
    /// other answers take the tier their question needs: a notice period does not need 8k tokens of thought.
    /// </summary>
    public const string Interview = "Interview";

    /// <summary>Technical, system design and coding answers: high effort.</summary>
    public const string InterviewHigh = "InterviewHigh";

    /// <summary>Behavioral, motivation, the pitch, negotiation: medium effort.</summary>
    public const string InterviewMedium = "InterviewMedium";

    /// <summary>Simple HR (notice, trips, hobby, formalities): low effort.</summary>
    public const string InterviewLow = "InterviewLow";

    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly<string>(
        [Extract, Score, Letter, Comment, Profile, Resume, Reply, ReplyDeep, Application, Coach, Interview, InterviewHigh, InterviewMedium, InterviewLow]);
}

public sealed class LlmOptions
{
    public const string Section = "Llm";

    public string Endpoint { get; set; } = "https://openrouter.ai/api/v1/chat/completions";

    public string ApiKey { get; set; } = "";

    /// <summary>HTTP proxy for models with <see cref="LlmModelOptions.UseProxy"/> (e.g. a vendor that geo-blocks the direct route).</summary>
    public string? Proxy { get; set; }

    /// <summary>Whole-request budget of one attempt (headers + body).</summary>
    public int TimeoutSec { get; set; } = 90;

    /// <summary>Spend ceiling per local day. Background calls stop above it; the bot sends an alert.</summary>
    public decimal DailyBudgetUsd { get; set; } = 2m;

    /// <summary>Attempts per call across the route (each attempt is a different healthy model, then the list wraps).</summary>
    public int MaxAttempts { get; set; } = 4;

    public string AppTitle { get; set; } = "JobFinder";

    public string Referer { get; set; } = "https://github.com/jobfinder";

    /// <summary>Purpose → models in priority order. Missing routes never fall back to another purpose.</summary>
    public Dictionary<string, List<LlmModelOptions>> Routes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class LlmModelOptions
{
    /// <summary>OpenRouter model id, e.g. <c>openai/gpt-6-luna</c>.</summary>
    public string Name { get; set; } = "";

    /// <summary>Legacy import only: reasoning/provider fields are compiled into typed settings, never merged into the request body.</summary>
    public string? ExtraBody { get; set; }

    public Configuration.ReasoningSettings? Reasoning { get; set; }

    public Configuration.ProviderSettings? Provider { get; set; }

    public bool UseProxy { get; set; }

    /// <summary>
    /// <c>json_schema</c> — strict structured output (the provider must support it; <c>provider.require_parameters</c> is set);
    /// <c>json_object</c> — JSON mode + schema in the prompt; <c>prompt</c> — schema in the prompt only.
    /// </summary>
    public string Structured { get; set; } = "json_schema";

    public int MaxTokens { get; set; } = 2000;

    /// <summary>
    /// Omitted when null. Reasoning models (OpenAI GPT-6) reject it, and with <c>require_parameters</c> OpenRouter then finds no endpoint.
    /// </summary>
    public double? Temperature { get; set; }

    /// <summary>This model's own time limit; null = <see cref="LlmOptions.TimeoutSec"/>. High reasoning effort thinks for minutes.</summary>
    public int? TimeoutSec { get; set; }
}
