using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JobFinder.App.Cli;
using JobFinder.Core.Domain;
using JobFinder.Core.Options;
using JobFinder.Core.Profile;
using JobFinder.Core.Resume;
using JobFinder.Llm.Contracts.Wire;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Documents;
using JobFinder.Pipeline.Modules.Preparation;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableResumeIntegrationTests
{
    private static ResumeDraftWire SyntheticDraft() => new()
    {
        Requirements = [new() { Requirement = ".NET services", MustHave = true, Evidence = "Prevented duplicate charges under 5000 RPS.", Placement = "Synthetic Systems, first bullet" }],
        Document = new()
        {
            Language = "en", Headline = "Senior .NET Developer", Location = "Minsk",
            Summary = "Prevented duplicate charges under 5000 RPS. Reduced batch processing from 60 to 10 minutes.",
            Skills = [new() { Group = "Backend", Items = [".NET", "C#", "PostgreSQL", "xUnit"] }],
            Experience = [new() { Company = "Synthetic Systems", Role = "Senior .NET Developer", Period = "Jan 2021 – Jan 2025",
                Bullets = ["Prevented duplicate charges under 5000 RPS.", "Reduced batch processing from 60 to 10 minutes."], Stack = [".NET", "PostgreSQL"] }],
            Languages = ["English B1"],
        },
    };

    private async Task SeedCandidateAsync(IHost host)
    {
        var document = SyntheticDraft().Document;
        var profile = new CandidateProfile
        {
            Name = "Synthetic Candidate", NameLatin = "Synthetic Candidate", Title = document.Headline, Location = "Minsk",
            Summary = document.Summary, EnglishLevel = "B1", Contacts = new() { Email = "candidate@example.invalid" },
            Skills = document.Skills[0].Items.Select(name => new ProfileSkill { Name = name }).ToList(),
            Experience = document.Experience.Select(job => new ProfileJob
            {
                Company = job.Company, Role = job.Role, Period = job.Period, Highlights = string.Join(" ", job.Bullets),
            }).ToList(),
        };
        var profiles = host.Services.GetRequiredService<ProfileProvider>();
        await profiles.SaveAsync(profile, (await profiles.GetSnapshotAsync(Ct))?.Version ?? 0, "test", Ct);
        var directory = host.Services.GetRequiredService<PathsOptions>().Profile;
        Directory.CreateDirectory(directory);
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(595, 842);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        string[] lines = [profile.Name, profile.Title, profile.Location, document.Summary, "Synthetic Systems", "Senior .NET Developer",
            "Jan 2021 - Jan 2025", .. document.Experience[0].Bullets, ".NET C# PostgreSQL xUnit", "English B1"];
        for (var index = 0; index < lines.Length; index++) page.AddText(lines[index], 10, new PdfPoint(40, 790 - index * 25), font);
        await File.WriteAllBytesAsync(Path.Combine(directory, "resume.pdf"), builder.Build(), Ct);
    }

    [Fact]
    public async Task Real_adapters_print_read_back_review_and_attach_a_cached_ATS_PDF_through_the_shared_facade()
    {
        using var http = new ResumeHttp();
        using var consumer = await HostAsync(true, null, http: http);
        await consumer.StartAsync(Ct);
        try
        {
            var seed = await InputAsync(consumer);
            await SeedCandidateAsync(consumer);
            var tailor = consumer.Services.GetRequiredService<ResumeTailor>();
            var ticket = await tailor.RequestAsync(seed.Vacancy.Id, Ct);
            Assert.Null(ticket.Cached);
            await UntilAsync(async () => (await RequestAsync(ticket.RequestId!.Value)).Stage == ResumePreparationStage.Completed);
            var result = await tailor.GetOrCreateAsync(seed.Vacancy.Id, Ct);
            Assert.True(ResumeTailor.Current(result));
            var report = JsonSerializer.Deserialize(result.ReportJson!, ResumeJsonContext.Default.ResumeReport)!;
            Assert.Equal(10, report.Review!.Scored().Score);
            Assert.Equal(1, report.Pages);
            Assert.Empty(report.Layout);
            Assert.Empty(report.Violations);
            Assert.Empty(report.Unshown);
            var document = JsonSerializer.Deserialize(result.ContentJson, ResumeJsonContext.Default.ResumeDocument)!;
            Assert.Equal("Synthetic Systems", Assert.Single(document.Experience).Company);
            var attachment = await tailor.AttachmentAsync(seed.Vacancy.Id, Ct);
            Assert.NotNull(attachment);
            Assert.Equal(result.Id, attachment.TailoredResumeId);
            Assert.Equal(report.PdfHash, attachment.Sha256);
            Assert.Equal(result.Id, (await tailor.GetOrCreateAsync(seed.Vacancy.Id, Ct)).Id);
            Assert.Equal(2, http.Requests.Count);
            Assert.Equal(0.0002m, result.CostUsd);
            Assert.All(http.Requests, body => Assert.Equal("xhigh", body["reasoning"]!["effort"]!.ToString()));
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            var calls = await context.LlmCalls.ToListAsync(Ct);
            Assert.Equal(2, calls.Count);
            Assert.All(calls, call => Assert.Equal(ticket.RequestId, call.OperationId));
            Assert.Single(await context.ResumePreparationRequests.ToListAsync(Ct));
            if (Environment.GetEnvironmentVariable("JOBFINDER_TEST_RESUME_ARTIFACTS") is { Length: > 0 } destination)
            {
                Directory.CreateDirectory(destination);
                File.Copy(StoredPath.Resolve(result.PdfPath), Path.Combine(destination, "Synthetic_Candidate_CV.pdf"), true);
                await File.WriteAllTextAsync(Path.Combine(destination, "Synthetic_Candidate_CV.html"),
                    ResumeHtml.Render(document, (await consumer.Services.GetRequiredService<ProfileProvider>().GetSnapshotAsync(Ct))!.Value,
                        consumer.Services.GetRequiredService<DocumentAssets>().FontCss), Ct);
            }
        }
        finally { await consumer.StopAsync(Ct); }
    }

    [Fact]
    public async Task Replacing_a_completed_PDF_reprints_and_reviews_the_checkpoint_without_a_paid_writer_rewrite()
    {
        using var http = new ResumeHttp();
        using var consumer = await HostAsync(true, null, http: http);
        await consumer.StartAsync(Ct);
        try
        {
            var seed = await InputAsync(consumer);
            await SeedCandidateAsync(consumer);
            var tailor = consumer.Services.GetRequiredService<ResumeTailor>();
            var original = await tailor.GetOrCreateAsync(seed.Vacancy.Id, Ct);
            Assert.True(ResumeTailor.Current(original));
            await File.WriteAllTextAsync(StoredPath.Resolve(original.PdfPath), "replaced synthetic PDF", Ct);
            Assert.False(ResumeTailor.Current(original));
            var repaired = await tailor.GetOrCreateAsync(seed.Vacancy.Id, Ct);
            Assert.NotEqual(original.Id, repaired.Id);
            Assert.True(ResumeTailor.Current(repaired));
            Assert.Equal(original.ContentJson, repaired.ContentJson);
            Assert.Equal(original.Model, repaired.Model);
            Assert.Equal(3, http.Requests.Count);
            Assert.Single(http.Requests, body => body["response_format"]!["json_schema"]!["name"]!.ToString() == "tailored_resume");
            Assert.Equal(0.0003m, repaired.CostUsd);
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Single(await context.ResumePreparationRequests.ToListAsync(Ct));
            Assert.Equal(2, await context.TailoredResumes.CountAsync(Ct));
        }
        finally { await consumer.StopAsync(Ct); }
    }

    [Fact]
    public async Task Real_tailor_CLI_publishes_to_the_live_isolated_consumer_without_claiming_ownership_or_opening_Telegram()
    {
        using var http = new ResumeHttp();
        using var consumer = await HostAsync(true, null, http: http);
        await consumer.StartAsync(Ct);
        try
        {
            var seed = await InputAsync(consumer);
            await SeedCandidateAsync(consumer);
            var result = await TailorCliAsync(consumer, seed.Vacancy.Id);
            Assert.True(result.Code == 0, result.Output);
            Assert.Contains("Synthetic_Candidate", result.Output);
            Assert.Contains("10/10", result.Output);
            Assert.True(consumer.Services.GetRequiredService<ServerOwnership>().IsOwned);
            Assert.Equal(2, http.Requests.Count);
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Empty(await context.TelegramSessions.ToListAsync(Ct));
            Assert.Single(await context.ResumePreparationRequests.ToListAsync(Ct));
            Assert.Single(await context.TailoredResumes.ToListAsync(Ct));
            var cached = await TailorCliAsync(consumer, seed.Vacancy.Id);
            Assert.True(cached.Code == 0, cached.Output);
            Assert.Equal(2, http.Requests.Count);
        }
        finally { await consumer.StopAsync(Ct); }
    }

    [Fact]
    public async Task Cancellation_while_drafting_prevents_printing_and_preserves_terminal_history()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var steps = new Steps { OnStage = async (stage, round, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        } };
        using var consumer = await HostAsync(true, steps);
        await consumer.StartAsync(Ct);
        try
        {
            var requests = consumer.Services.GetRequiredService<ResumePreparationRequests>();
            var id = await requests.EnqueueAsync(await InputAsync(consumer), Ct);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
            Assert.True(await requests.CancelAsync(id, Ct));
            Assert.False(await requests.CancelAsync(id, Ct));
            release.SetResult();
            await Task.Delay(1500, Ct);
            Assert.Equal(ResumePreparationStage.Cancelled, (await RequestAsync(id)).Stage);
            Assert.Single(steps.Calls);
            Assert.Equal(0, await NoticesAsync());
        }
        finally { release.TrySetResult(); await consumer.StopAsync(Ct); }
    }

    [Fact]
    public async Task Ownership_loss_after_a_costly_step_prevents_checkpoint_and_continuation()
    {
        using var producer = await HostAsync(false, new Steps());
        await producer.StartAsync(Ct);
        try
        {
            var input = await InputAsync(producer);
            var id = await producer.Services.GetRequiredService<ResumePreparationRequests>().EnqueueAsync(input, Ct);
            var ownership = producer.Services.GetRequiredService<ServerOwnership>();
            var workflow = producer.Services.GetRequiredService<ResumePreparationWorkflow>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RunAsync(id, 1, 1, ResumePreparationStage.Drafting, Guid.NewGuid(), Ct));
            Assert.Null((await RequestAsync(id)).ClaimEnvelopeId);
            ownership.Acquired();
            var request = await RequestAsync(id);
            var steps = producer.Services.GetRequiredService<IResumePreparationSteps>();
            ((Steps)steps).OnStage = (stage, round, token) => { ownership.Lost(); return Task.CompletedTask; };
            await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RunAsync(id, 1, 1, ResumePreparationStage.Drafting, Guid.NewGuid(), Ct));
            var saved = await RequestAsync(id);
            Assert.Equal(ResumePreparationStage.Drafting, saved.Stage);
            Assert.Null(saved.CurrentJson);
            Assert.NotNull(saved.ClaimEnvelopeId);
            Assert.Equal(request.Failures, saved.Failures);
            Assert.Equal(0, await NoticesAsync());
        }
        finally { await producer.StopAsync(Ct); }
    }

    private async Task<(int Code, string Output)> TailorCliAsync(IHost consumer, long id)
    {
        var home = Path.Combine(_artifacts, "cli");
        Directory.CreateDirectory(Path.Combine(home, "config"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json"), Path.Combine(home, "config", "appsettings.json"), true);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = home, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("JOBFINDER_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["JOBFINDER_HOME"] = home;
        start.Environment["JOBFINDER_Database__ConnectionString"] = context.Database.GetConnectionString();
        var paths = consumer.Services.GetRequiredService<PathsOptions>();
        start.Environment["JOBFINDER_Paths__Profile"] = paths.Profile;
        start.Environment["JOBFINDER_Paths__Data"] = paths.Data;
        start.Environment["JOBFINDER_Paths__Assets"] = paths.Assets;
        start.Environment["JOBFINDER_Paths__Prompts"] = Fixture.PromptsDir;
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "JobFinder.Tests.runtimeconfig.json"));
        start.ArgumentList.Add("--depsfile");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "JobFinder.Tests.deps.json"));
        start.ArgumentList.Add(typeof(CliHost).Assembly.Location);
        start.ArgumentList.Add("--tailor");
        start.ArgumentList.Add(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start isolated resume CLI");
        var stdout = process.StandardOutput.ReadToEndAsync(Ct);
        var stderr = process.StandardError.ReadToEndAsync(Ct);
        try
        {
            await process.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(45), Ct);
            return (process.ExitCode, await stdout + await stderr);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }

    private sealed class ResumeHttp : HttpMessageHandler, IHttpClientFactory
    {
        public ConcurrentQueue<JsonObject> Requests { get; } = new();
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            Requests.Enqueue(body);
            var schema = body["response_format"]!["json_schema"]!["name"]!.ToString();
            var content = schema switch
            {
                "tailored_resume" => JsonSerializer.Serialize(SyntheticDraft(), GeneratedWireJsonContext.Default.ResumeDraftWire),
                "resume_review" => JsonSerializer.Serialize(new ResumeReviewWire { Score = 10, Verdict = "Synthetic grounded review",
                    Checks = ResumeReview.Criteria.Keys.Select(id => new ResumeReviewWireChecksItem { Id = id, Passed = true }).ToList() },
                    GeneratedWireJsonContext.Default.ResumeReviewWire),
                _ => throw new InvalidOperationException("unexpected synthetic LLM task: " + schema),
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(new JsonObject
            {
                ["model"] = "synthetic-" + schema, ["choices"] = new JsonArray(new JsonObject
                { ["message"] = new JsonObject { ["content"] = content }, ["finish_reason"] = "stop" }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 100, ["completion_tokens"] = 100, ["cost"] = 0.0001m },
            }.ToJsonString()) };
        }
    }
}
