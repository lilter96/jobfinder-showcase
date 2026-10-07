namespace JobFinder.Core.Domain;

public sealed record AssessmentVacancy
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string? CompanyName { get; set; }
    public long? CompanyId { get; set; }
    public bool Blacklisted { get; set; }
    public List<string> Industries { get; set; } = [];
    public string Description { get; set; } = "";
    public string Url { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public decimal? SalaryFrom { get; set; }
    public decimal? SalaryTo { get; set; }
    public string? Currency { get; set; }
    public bool? SalaryGross { get; set; }
    public string? City { get; set; }
    public string? CountryCode { get; set; }
    public WorkFormat WorkFormats { get; set; }
    public EnglishLevel EnglishRequired { get; set; }
    public string? Experience { get; set; }
    public List<string> Skills { get; set; } = [];
    public List<VacancyContact> Contacts { get; set; } = [];
    public List<AssessmentSource> Sources { get; set; } = [];
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset? OriginalPostedAt { get; set; }
    public int? ResponsesCount { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public VacancyStatus Status { get; set; }
    public long Revision { get; set; }
    public string? CoverLetter { get; set; }
    public long LetterRevision { get; set; }

    public static AssessmentVacancy From(Vacancy vacancy) => new()
    {
        Id = vacancy.Id, Title = vacancy.Title, CompanyName = vacancy.CompanyName, CompanyId = vacancy.CompanyId,
        Blacklisted = vacancy.Company?.Blacklisted == true, Industries = [.. vacancy.Company?.Industries ?? []],
        Description = vacancy.Description, Url = vacancy.Url, Fingerprint = vacancy.Fingerprint,
        SalaryFrom = vacancy.SalaryFrom, SalaryTo = vacancy.SalaryTo, Currency = vacancy.Currency, SalaryGross = vacancy.SalaryGross,
        City = vacancy.City, CountryCode = vacancy.CountryCode, WorkFormats = vacancy.WorkFormats, EnglishRequired = vacancy.EnglishRequired,
        Experience = vacancy.Experience, Skills = [.. vacancy.Skills], Contacts = [.. vacancy.Contacts],
        Sources = vacancy.Sources.OrderBy(source => source.Id).Select(source => new AssessmentSource
        {
            Id = source.Id, Source = source.Source, ExternalId = source.ExternalId, Host = source.Host, Url = source.Url,
            LetterRequired = source.LetterRequired, HasEmployerTest = source.HasEmployerTest,
        }).ToList(),
        PublishedAt = vacancy.PublishedAt, OriginalPostedAt = vacancy.OriginalPostedAt, ResponsesCount = vacancy.ResponsesCount,
        UpdatedAt = vacancy.UpdatedAt, Status = vacancy.Status, Revision = vacancy.AssessmentRevision, CoverLetter = vacancy.CoverLetter, LetterRevision = vacancy.LetterRevision,
    };

    public Vacancy ToVacancy() => new()
    {
        Id = Id, Title = Title, CompanyName = CompanyName, CompanyId = CompanyId,
        Company = CompanyName is null ? null : new Company { Name = CompanyName, Blacklisted = Blacklisted, Industries = [.. Industries] },
        Description = Description, Url = Url, Fingerprint = Fingerprint,
        SalaryFrom = SalaryFrom, SalaryTo = SalaryTo, Currency = Currency, SalaryGross = SalaryGross,
        City = City, CountryCode = CountryCode, WorkFormats = WorkFormats, EnglishRequired = EnglishRequired,
        Experience = Experience, Skills = [.. Skills], Contacts = [.. Contacts],
        Sources = Sources.Select(source => new VacancySource
        {
            Id = source.Id, VacancyId = Id, Source = source.Source, ExternalId = source.ExternalId, Host = source.Host, Url = source.Url,
            LetterRequired = source.LetterRequired, HasEmployerTest = source.HasEmployerTest,
        }).ToList(),
        PublishedAt = PublishedAt, OriginalPostedAt = OriginalPostedAt, ResponsesCount = ResponsesCount,
        UpdatedAt = UpdatedAt, Status = Status, AssessmentRevision = Revision, CoverLetter = CoverLetter, LetterRevision = LetterRevision,
    };
}

public sealed record AssessmentSource
{
    public long Id { get; set; }
    public SourceKind Source { get; set; }
    public string ExternalId { get; set; } = "";
    public string? Host { get; set; }
    public string Url { get; set; } = "";
    public bool LetterRequired { get; set; }
    public bool HasEmployerTest { get; set; }
}
