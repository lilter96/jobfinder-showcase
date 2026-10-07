using JobFinder.Core.Domain;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed record LetterEditSnapshot
{
    public long VacancyId { get; set; }
    public string? Text { get; set; }
    public VacancyStatus Status { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long AssessmentRevision { get; set; }
    public long LetterRevision { get; set; }
    public List<LetterEditApplication> Applications { get; set; } = [];

    public static LetterEditSnapshot Capture(Vacancy vacancy, IReadOnlyCollection<Application>? applications = null) => new()
    {
        VacancyId = vacancy.Id, Text = vacancy.CoverLetter, Status = vacancy.Status, UpdatedAt = vacancy.UpdatedAt,
        AssessmentRevision = vacancy.AssessmentRevision, LetterRevision = vacancy.LetterRevision,
        Applications = (applications ?? vacancy.Applications).OrderBy(application => application.Id).Select(application => new LetterEditApplication
        {
            Id = application.Id, Revision = application.Revision, Status = application.Status, RequestId = application.DeliveryRequestId,
            Text = application.CoverLetter, Channel = application.Channel, Target = application.Target,
        }).ToList(),
    };

    public bool Matches(Vacancy vacancy, IReadOnlyCollection<Application>? applications = null)
    {
        var current = Capture(vacancy, applications);
        return VacancyId == current.VacancyId && Text == current.Text && Status == current.Status && UpdatedAt == current.UpdatedAt
            && AssessmentRevision == current.AssessmentRevision && LetterRevision == current.LetterRevision
            && Applications is not null && Applications.SequenceEqual(current.Applications);
    }
}

public sealed record LetterEditApplication
{
    public long Id { get; set; }
    public long Revision { get; set; }
    public ApplicationStatus Status { get; set; }
    public Guid? RequestId { get; set; }
    public string? Text { get; set; }
    public ApplicationChannel Channel { get; set; }
    public string? Target { get; set; }
}
