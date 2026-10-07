using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Llm;
using JobFinder.Llm.Client;
using JobFinder.Llm.Contracts;
using JobFinder.Llm.Contracts.Wire;
using JobFinder.Llm.Prompts;
using JobFinder.Llm.Tasks;
using JobFinder.Llm.Tasks.Discovery;

namespace JobFinder.Tests.Llm;

public sealed class LlmTaskCatalogTests
{
    private const string NonVacancy = """
        {"isVacancy":false,"isDotnet":false,"title":null,"company":null,"city":null,"country":null,"workFormats":[],
         "salaryFrom":null,"salaryTo":null,"currency":null,"salaryGross":null,"englishLevel":"unknown","seniority":"unknown",
         "skills":[],"contacts":[],"applyUrl":null}
        """;

    public static TheoryData<string, string> Probes => new(LlmTaskCatalog.All.SelectMany(task => task.Purposes.Select(purpose => (task.Id, purpose))));

    [Fact]
    public void The_catalog_owns_the_complete_unique_prompt_and_purpose_roster()
    {
        Assert.Equal(22, LlmTaskCatalog.All.Count);
        Assert.Equal(LlmTaskCatalog.All.Count, LlmTaskCatalog.All.Select(task => task.Id).Distinct().Count());
        Assert.Equal(LlmTaskCatalog.All.Select(task => task.Prompt).Distinct().Order(), PromptCatalog.Required.Order());
        Assert.Equal(LlmPurpose.All.Order(), LlmTaskCatalog.All.SelectMany(task => task.Purposes).Distinct().Order());
        Assert.All(LlmTaskCatalog.All, task => Assert.False(string.IsNullOrWhiteSpace(task.RulesVersion)));
    }

    [Fact]
    public void Public_rosters_cannot_be_mutated_by_an_adapter_or_other_caller()
    {
        Assert.Throws<NotSupportedException>(() => ((IList<string>)LlmPurpose.All)[0] = "changed");
        Assert.Throws<NotSupportedException>(() => ((IList<string>)PromptCatalog.Required)[0] = "changed");
        Assert.Throws<NotSupportedException>(() => ((IList<string>)LlmTaskCatalog.HrAnswer.Purposes)[0] = "changed");
    }

    [Theory]
    [MemberData(nameof(Probes))]
    public void Every_supported_task_route_has_a_typed_probe_that_renders_the_real_prompt(string taskId, string purpose)
    {
        var task = LlmTaskCatalog.All.Single(task => task.Id == taskId);
        var probe = task.Probe(purpose);
        var system = new PromptCatalog(Fixture.PromptsDir).Render(task.Prompt, probe.Payload.Bindings);
        Assert.Equal(task.Id, probe.TaskId);
        Assert.Equal(purpose, probe.Payload.Purpose);
        Assert.Same(task.Schema, probe.Schema);
        Assert.NotEmpty(system);
        Assert.NotEmpty(probe.Payload.UserMessage);
        Assert.DoesNotContain("{{", system);
        Assert.Equal(64, task.ContractVersion.Length);
        Assert.ThrowsAny<ArgumentException>(() => task.Probe("unregistered-purpose"));
    }

    [Fact]
    public void Probes_use_their_task_schema_and_the_same_semantic_validator_as_execution()
    {
        var extraction = ExtractPostTask.Definition.Probe(LlmPurpose.Extract);
        Assert.Null(extraction.ValidateResponse(NonVacancy));
        Assert.NotNull(extraction.ValidateResponse(NonVacancy.Replace("\"isVacancy\":false", "\"isVacancy\":true")));
        Assert.Throws<LlmCallException>(() => LlmTaskCatalog.Score.Probe(LlmPurpose.Score).ValidateResponse(NonVacancy));
        Assert.Throws<LlmCallException>(() => extraction.ValidateResponse("{}"));
        var outreach = LlmTaskCatalog.Outreach.Probe(LlmPurpose.Letter);
        Assert.NotNull(outreach.ValidateResponse(" \n"));
        Assert.Null(outreach.ValidateResponse("Здравствуйте!"));
    }

    [Fact]
    public void Runtime_purpose_parameters_cannot_silently_choose_an_unrelated_route()
    {
        Assert.Throws<ArgumentException>(() => LlmTaskCatalog.HrAnswer.Build(TaskProbeInputs.HrAnswer(LlmPurpose.Score)));
        Assert.Throws<ArgumentException>(() => LlmTaskCatalog.InterviewAnswer.Build(TaskProbeInputs.InterviewAnswer(LlmPurpose.Application)));
        Assert.Throws<ArgumentException>(() => LlmTaskCatalog.Score.Probe(LlmPurpose.Extract));
        Assert.Throws<ArgumentException>(() => new LlmTextTaskDefinition<string>("text", "outreach", "v1", [],
            text => new LlmTaskPayload(LlmPurpose.Letter, new Dictionary<string, string>(), text)));
    }

    [Fact]
    public void Candidate_facing_tasks_remain_essential_and_first_messages_keep_the_maximum_route()
    {
        Assert.True(LlmTaskCatalog.TailorResume.Essential);
        Assert.True(LlmTaskCatalog.ReviewResume.Essential);
        Assert.True(LlmTaskCatalog.RecruiterMessage.Essential);
        Assert.True(LlmTaskCatalog.CheckRecruiterMessage.Essential);
        Assert.Equal(LlmPurpose.Application, LlmTaskCatalog.RecruiterMessage.Build(TaskProbeInputs.RecruiterMessage(LlmPurpose.Application)).Purpose);
        Assert.Equal(LlmPurpose.Application, LlmTaskCatalog.CheckRecruiterMessage.Build(TaskProbeInputs.CheckRecruiterMessage(LlmPurpose.Application)).Purpose);
        Assert.True(LlmTaskCatalog.Outreach.Build(TaskProbeInputs.Outreach(LlmPurpose.Letter) with { Essential = true }).Essential);
    }

    [Fact]
    public void Typed_bindings_preserve_untrusted_data_wrappers_revisions_and_attachment_semantics()
    {
        var input = TaskProbeInputs.RecruiterMessage(LlmPurpose.Application) with
        {
            Profile = new CandidateProfile { Name = "{{untrusted}}" },
            Vacancy = new Vacancy { Title = "Senior .NET", Description = "{{do_not_execute}}", Url = "https://example.com/job", Fingerprint = "f" },
            Conversation = "test-conversation", AttachesResume = true, Remarks = "revision-reason",
        };
        var request = LlmTaskCatalog.RecruiterMessage.Build(input);
        var system = new PromptCatalog(Fixture.PromptsDir).Render(LlmTaskCatalog.RecruiterMessage.Prompt, request.Bindings);
        Assert.Contains("{{untrusted}}", system);
        Assert.Contains("К сообщению будет приложен PDF", request.Bindings["attachment"]);
        Assert.Contains("<vacancy>", request.UserMessage);
        Assert.Contains("{{do_not_execute}}", request.UserMessage);
        Assert.Contains("<conversation>\ntest-conversation\n</conversation>", request.UserMessage);
        Assert.Contains("revision-reason", request.UserMessage);
        var resume = LlmTaskCatalog.TailorResume.Build(TaskProbeInputs.TailorResume(LlmPurpose.Resume) with
        {
            Facts = new string('ф', 31000), Current = new JobFinder.Core.Resume.ResumeDocument { Headline = "keep-best-version" }, Feedback = "fix-one-issue",
        });
        Assert.Contains(new string('ф', 31000), resume.UserMessage);
        Assert.Contains("<current>", resume.UserMessage);
        Assert.Contains("keep-best-version", resume.UserMessage);
        Assert.Contains("fix-one-issue", resume.UserMessage);
    }
}
