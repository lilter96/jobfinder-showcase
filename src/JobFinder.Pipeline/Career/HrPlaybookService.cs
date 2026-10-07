using System.Text.Json;
using JobFinder.Core.Options;
using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Modules.Preparation;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Career;

public sealed class HrPlaybookService(HrPlaybookSources sources, HrPlaybookRequests requests, FactsProvider facts, PathsOptions paths)
{
    public const string LatestKey = "hr.playbook";
    public const string RequestedKey = "hr.playbook.requested";
    public const string BankFile = "hr_bank.json";


    private readonly Lock _lock = new();
    private (DateTime Mtime, HrBank Bank)? _bank;

    public HrBank Bank()
    {
        var path = Path.GetFullPath(Path.Combine(paths.Prompts, BankFile));
        lock (_lock)
        {
            var mtime = File.GetLastWriteTimeUtc(path);
            if (_bank is { } cached && cached.Mtime == mtime) return cached.Bank;
            var bank = JsonSerializer.Deserialize(File.ReadAllText(path), InterviewJsonContext.Default.HrBank) ?? new HrBank();
            _bank = (mtime, bank);
            return bank;
        }
    }


    public Task<HrPlaybook?> LatestAsync(CancellationToken ct) => sources.LatestAsync(ct);
    public async Task RequestAsync(CancellationToken ct)
    {
        if (await sources.RequestAsync(Bank(), ct, force: true) is null) throw new InvalidOperationException("нет профиля: сначала импортируйте или сохраните профиль кандидата");
    }
    public Task<Guid?> EnsureAsync(CancellationToken ct) => sources.RequestAsync(Bank(), ct, force: false);
    public Task<bool> RequestedAsync(CancellationToken ct) => sources.RequestedAsync(ct);

    public async Task<List<string>> MissingFactsAsync(CancellationToken ct)
    {
        var current = (await facts.GetSnapshotAsync(ct)).Value;
        return Bank().Questions.SelectMany(question => question.Facts).Distinct(StringComparer.Ordinal)
            .Where(key => !current.IsConfirmed(key)).Select(key => current.Get(key)?.Question ?? key).ToList();
    }

    /// <summary>The bank question as a planned one: the frequency sets priority and likelihood, "why they ask" is what it tests.</summary>
    public static PlannedQuestion ToPlanned(HrQuestion q) => new()
    {
        Id = q.Id,
        Question = q.Question,
        Category = q.Category switch
        {
            "Мотивация" or "Сочетание ожиданий" => "motivation",
            "Планирование" or "Работа с людьми" or "Работа над собой" or "Вовлечённость" => "behavioral",
            _ => "hr",
        },
        Priority = q.Frequency switch { "всегда" => "critical", "часто" => "high", _ => "medium" },
        Likelihood = q.Frequency switch { "всегда" or "часто" => "high", _ => "low" },
        Basis = "likely",
        Source = q.Category,
        Tests = q.Why,
        Risk = q.Category is "Вопрос с подвохом" or "Личные" ? "high" : "medium",
        CandidateLevel = "adequate",
    };


    public async Task<HrPlaybook> BuildAsync(CancellationToken ct)
    {
        var id = await sources.RequestAsync(Bank(), ct, force: true) ?? throw new InvalidOperationException("нет профиля");
        return await requests.WaitAsync(id, ct);
    }

    public static string PositionFactKey(string questionId) => "hr_" + questionId;
    public Task<IReadOnlyDictionary<string, HrPositionEditSnapshot>> CapturePositionsAsync(IReadOnlyList<HrQuestion> questions, CancellationToken ct) =>
        sources.CapturePositionsAsync(questions, Bank(), ct);
    public Task<Actions.ActionResult> SavePositionAsync(HrPositionEditSnapshot snapshot, string text, CancellationToken ct) =>
        sources.SavePositionAsync(snapshot, text, Bank(), ct);
}

public sealed class HrPlaybookJob(HrPlaybookService hr, JobTriggers triggers, IDbContextFactory<JobFinderDbContext> db, Notifier notifier,
    TimeProvider time, ILogger<HrPlaybookJob> log) : ScheduledJob(db, triggers, notifier, time, log)
{
    public override string Name => JobNames.HrPlaybook;
    protected override bool Enabled => true;
    protected override TimeSpan FirstDelay => TimeSpan.FromMinutes(2);
    protected override DateTimeOffset NextRunAfter(DateTimeOffset finishedAt) => finishedAt + TimeSpan.FromMinutes(30);

    protected override async Task<JobOutcome> RunOnceAsync(CancellationToken ct)
    {
        var id = await hr.EnsureAsync(ct);
        return new JobOutcome(id is null ? 0 : 1, id is null ? "up to date or no profile" : $"native preparation {id}");
    }
}
