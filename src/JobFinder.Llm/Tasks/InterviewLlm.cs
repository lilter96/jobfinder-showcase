using System.Globalization;
using JobFinder.Llm.Contracts;
using JobFinder.Llm.Contracts.Wire;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Llm.Client;

namespace JobFinder.Llm.Tasks;

/// <summary>
/// The interview copilot: the plan on the maximum-effort route (<see cref="LlmPurpose.Interview"/>), each answer on the tier its question
/// needs. The versions below are what stored answers are keyed by: a changed prompt, route or schema regenerates them.
/// </summary>
public sealed partial class JobFinderLlm
{
    /// <summary>The prompt template's version (its hash).</summary>
    public string PromptVersion(string name) => Hash(prompts.Template(name));

    /// <summary>The route's version: its models, output mode, token limits and extra body, where the reasoning effort lives.</summary>
    public string RouteVersion(string purpose) => client.RouteVersion(purpose);

    /// <summary>The spoken answer's schema version.</summary>
    public static string AnswerSchemaVersion => LlmTaskCatalog.InterviewAnswer.ContractVersion;

    public static string PlanSchemaVersion => LlmTaskCatalog.InterviewPlan.ContractVersion;

    public static string ApplicationSchemaVersion => Hash(LlmTaskCatalog.RecruiterMessage.ContractVersion + LlmTaskCatalog.CheckRecruiterMessage.ContractVersion);

    public Core.Artifacts.ArtifactRecipe Recipe(IEnumerable<(ILlmTaskDefinition Task, string Purpose)> tasks, IReadOnlyDictionary<string, string?> inputs,
        IReadOnlyDictionary<string, string>? rules = null) => GenerationRecipe.Capture(prompts, client.RouteVersion, tasks, inputs, rules,
            client.PolicyRevision, client.OperationId);

    private static string Hash(string text) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..16];

    /// <summary>Pass 1: priorities, the question shortlist with one real case each, gaps, questions to ask, money, risks.</summary>
    public Task<LlmResult<InterviewPlan>> InterviewPlanAsync(CandidateProfile profile, string kind, Vacancy vacancy, string company, string evidence,
        string facts, string conversation, string market, string notes, DateTimeOffset now, CancellationToken ct)
    {
        return LlmTaskCatalog.InterviewPlan.ExecuteAsync(client, prompts,
            new InterviewPlanInput(profile, kind, vacancy, company, evidence, facts, conversation, market, notes, now), ct).MapAsync(static wire => wire.ToDomain());
    }

    /// <summary>The general answer to one HR question of the bank, for the HR document.</summary>
    /// <param name="funnel">The real state of the search (active processes, interviews, offers): the honest basis for "other offers".</param>
    public Task<LlmResult<SpokenAnswer>> HrAnswerAsync(CandidateProfile profile, HrQuestion question, string evidence, string facts, string funnel,
        string? remarks, DateTimeOffset now, string purpose, CancellationToken ct)
    {
        return LlmTaskCatalog.HrAnswer.ExecuteAsync(client, prompts,
            new HrAnswerInput(profile, question, evidence, facts, funnel, remarks, now, purpose), ct).MapAsync(static wire => wire.ToDomain());
    }

    /// <summary>Pass 2: the speech for one question, with follow-ups and what not to say.</summary>
    /// <param name="evidence">The catalog entries the question rests on, in full, then the rest of the catalog.</param>
    /// <param name="remarks">What the speech check found in the previous variant.</param>
    public Task<LlmResult<SpokenAnswer>> InterviewAnswerAsync(CandidateProfile profile, string kind, Vacancy vacancy, PlannedQuestion question,
        string evidence, string facts, string conversation, string? remarks, DateTimeOffset now, string purpose, CancellationToken ct)
    {
        return LlmTaskCatalog.InterviewAnswer.ExecuteAsync(client, prompts,
            new InterviewAnswerInput(profile, kind, vacancy, question, evidence, facts, conversation, remarks, now, purpose), ct).MapAsync(static wire => wire.ToDomain());
    }
}
