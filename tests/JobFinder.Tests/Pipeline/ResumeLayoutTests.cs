using JobFinder.Core.Profile;
using JobFinder.Core.Resume;
using JobFinder.Pipeline.Documents;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace JobFinder.Tests.Pipeline;

/// <summary>
/// The printed resume read back: a job torn from its heading (what went to @CodeVal: «Elgrow» at the bottom of page 1, its experience on
/// page 2), a section title alone at a page bottom, too many pages — found in the PDF, with the block a page break fixes.
/// </summary>
public sealed class ResumeLayoutTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jf-layout-" + Guid.NewGuid().ToString("N"));

    public ResumeLayoutTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static readonly CandidateProfile Profile = new()
    {
        Name = "Гацуков Терентий Алексеевич", NameLatin = "Terentiy Gatsukov", Title = "Senior .NET Backend Developer",
        Contacts = new ProfileContacts { Email = "lilter96dotnet@gmail.com", Phone = "+375 33 385-44-32", Telegram = "@lilter96" },
    };

    private static ResumeDocument Doc() => new()
    {
        Language = "en",
        Headline = "Senior .NET Backend Developer",
        Summary = "Backend developer with five years in .NET and high load.",
        Experience =
        [
            new ResumeJob { Company = "Custom Games Studio", Role = ".NET Developer", Period = "Nov 2023 – Jul 2026", Bullets = ["Removed double charges under 5000 RPS.", "Sped up batch jobs six times."] },
            new ResumeJob { Company = "Elgrow", Role = "C# Developer", Period = "Jan 2021 – Jun 2022", Context = "Smart parking for Domodedovo airport.", Bullets = ["Designed the booking system on ASP.NET Core."] },
        ],
        Education = [new ResumeEducation { Institution = "BSUIR", Degree = "Higher education", Period = "2023" }],
    };

    /// <summary>A PDF whose pages hold these lines, top to bottom.</summary>
    private string Pdf(params string[][] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var lines in pages)
        {
            var page = builder.AddPage(PageSize.A4);
            for (var i = 0; i < lines.Length; i++) page.AddText(lines[i], 10, new PdfPoint(40, 800 - i * 16), font);
        }

        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    private static readonly string[] Head =
        ["Terentiy Gatsukov", "Senior .NET Backend Developer", "lilter96dotnet@gmail.com +375 33 385-44-32", "SUMMARY", "Backend developer with five years in .NET and high load."];

    private static readonly string[] FirstJob =
        ["EXPERIENCE", ".NET Developer — Custom Games Studio Nov 2023 – Jul 2026", "Removed double charges under 5000 RPS.", "Sped up batch jobs six times."];

    private static readonly string[] Tail = ["EDUCATION", "BSUIR, Higher education 2023"];

    [Fact]
    public void A_job_context_does_not_hide_a_first_bullet_on_the_next_page()
    {
        var pdf = Pdf([.. Head, .. FirstJob, "C# Developer — Elgrow Jan 2021 – Jun 2022", "Smart parking for Domodedovo airport."],
            ["Designed the booking system on ASP.NET Core.", .. Tail]);
        Assert.Contains(ResumePdfCheck.Check(pdf, Doc(), Profile).Issues, issue => issue.Key == "job:1");
    }

    [Fact]
    public void A_bullet_with_its_middle_missing_cannot_pass_by_its_prefix_and_suffix()
    {
        var doc = Doc();
        doc.Experience[1].Bullets = ["Designed the booking system with reliable payment handling and reservation expiry processing on ASP.NET Core."];
        var pdf = Pdf([.. Head, .. FirstJob], ["C# Developer — Elgrow Jan 2021 – Jun 2022", "Smart parking for Domodedovo airport.",
            "Designed the booking system on ASP.NET Core.", .. Tail]);
        Assert.Contains(ResumePdfCheck.Check(pdf, doc, Profile).Issues, issue => issue.Key is null && issue.Message.Contains("пункт не извлекается"));
    }

    [Fact]
    public void A_bullet_continuing_on_the_next_page_is_reported()
    {
        var pdf = Pdf([.. Head, .. FirstJob, "C# Developer — Elgrow Jan 2021 – Jun 2022", "Smart parking for Domodedovo airport.", "Designed the booking"],
            ["system on ASP.NET Core.", .. Tail]);
        Assert.Contains(ResumePdfCheck.Check(pdf, Doc(), Profile).Issues, issue => issue.Key == "job:1" && issue.Message.Contains("разорван"));
    }

    [Fact]
    public void A_job_torn_from_its_heading_is_found_with_the_block_to_move()
    {
        var pdf = Pdf([.. Head, .. FirstJob, "C# Developer — Elgrow Jan 2021 – Jun 2022"],
            ["Smart parking for Domodedovo airport.", "Designed the booking system on ASP.NET Core.", .. Tail]);

        var layout = ResumePdfCheck.Check(pdf, Doc(), Profile);

        var issue = Assert.Single(layout.Issues);
        Assert.Equal("job:1", issue.Key);
        Assert.Contains("Elgrow", issue.Message);
        Assert.Equal(2, layout.Pages);
    }

    [Fact]
    public void A_section_title_alone_at_the_bottom_of_a_page_is_found()
    {
        var pdf = Pdf([.. Head, .. FirstJob, "C# Developer — Elgrow Jan 2021 – Jun 2022", "Smart parking for Domodedovo airport.",
            "Designed the booking system on ASP.NET Core.", "EDUCATION"], ["BSUIR, Higher education 2023"]);

        var issue = Assert.Single(ResumePdfCheck.Check(pdf, Doc(), Profile).Issues);

        Assert.Equal("section:education", issue.Key);
    }

    [Fact]
    public void A_clean_print_has_no_issues_and_its_text_is_what_a_parser_reads()
    {
        var pdf = Pdf([.. Head, .. FirstJob], ["C# Developer — Elgrow Jan 2021 – Jun 2022", "Smart parking for Domodedovo airport.",
            "Designed the booking system on ASP.NET Core.", .. Tail]);

        var layout = ResumePdfCheck.Check(pdf, Doc(), Profile);

        Assert.Empty(layout.Issues);
        Assert.Contains("--- страница 2 ---", ResumePdfCheck.ForReview(layout));
    }

    [Fact]
    public void Three_pages_and_text_that_is_not_there_are_reported_without_a_break_to_fix_them()
    {
        var pdf = Pdf(Head, FirstJob, ["C# Developer — Elgrow Jan 2021 – Jun 2022", "Smart parking for Domodedovo airport.", .. Tail]);

        var issues = ResumePdfCheck.Check(pdf, Doc(), Profile).Issues;

        Assert.Contains(issues, i => i.Key is null && i.Message.Contains("3 страницы"));
        Assert.Contains(issues, i => i.Key is null && i.Message.Contains("не извлекается") && i.Message.Contains("booking"));
    }

    [Fact]
    public void A_jobs_heading_and_its_first_two_points_are_one_block_and_a_break_goes_where_the_check_asks()
    {
        var doc = Doc() with
        {
            Experience = [Doc().Experience[0] with { Bullets = ["One.", "Two.", "Three."], Stack = ["C#", "Redis"] }, Doc().Experience[1]],
        };

        var html = ResumeHtml.Render(doc, Profile, "", new HashSet<string> { "job:1" });

        var keep = html[html.IndexOf("<div class=\"keep\">", StringComparison.Ordinal)..];
        keep = keep[..keep.IndexOf("<ul class=\"more\">", StringComparison.Ordinal)];
        Assert.Contains("Custom Games Studio", keep);
        Assert.Contains("<li>Two.</li>", keep);
        Assert.DoesNotContain("Three.", keep);
        Assert.Contains("<div class=\"stack\"><b>Stack:</b> C#, Redis</div>", html);
        Assert.Contains("<div class=\"job force-break\">", html);
        Assert.Contains("href=\"mailto:lilter96dotnet@gmail.com\"", html);
    }

    [Fact]
    public void A_number_stays_with_its_digit_groups_and_unit_and_a_contact_never_breaks_inside()
    {
        var doc = Doc() with { Summary = "Устранил двойные списания при 5 000+ RPS, p99 <200 мс, в 6 раз быстрее." };
        var profile = Profile with { Contacts = Profile.Contacts with { LinkedIn = "linkedin.com/in/terentiy-gatsukov" } };

        var html = ResumeHtml.Render(doc, profile, "");
        var text = System.Net.WebUtility.HtmlDecode(html);

        Assert.Contains("5 000+ RPS", text);
        Assert.Contains("<200 мс", text);
        Assert.Contains("6 раз", text);
        Assert.Contains("<span class=\"nb\"><a href=\"https://linkedin.com/in/terentiy-gatsukov\">", html);
    }

    [Fact]
    public void A_review_remark_that_quotes_text_the_pdf_does_not_have_is_dropped()
    {
        // Seen live on #371: "Git and CI/CD are not listed" while page 1 listed them.
        var layout = new PdfLayout(1, ["Infrastructure: CI/CD, Git, Docker", "Removed double charges under 5000 RPS."], []);
        var review = new ResumeReview
        {
            Score = 7,
            Issues =
            [
                new ResumeIssue { Where = "skills", Quote = "Infrastructure: Docker, Kubernetes", Problem = "no Git", Fix = "add Git" },
                new ResumeIssue { Where = "summary", Quote = "Removed double charges", Problem = "repeats the first point", Fix = "say it once" },
                new ResumeIssue { Where = "skills", Quote = "", Problem = "no gRPC", Fix = "add gRPC" },
            ],
        };

        var grounded = ResumeTailor.Grounded(review, layout);

        Assert.Equal(["summary", "skills"], grounded.Issues.Select(i => i.Where));
        Assert.Equal("", grounded.Issues[1].Quote);
    }

    private static ResumeReview Checklist(params (string Id, bool Passed, string Quote)[] failed) => new()
    {
        Checks = [.. ResumeReview.Criteria.Keys.Select(id => failed.FirstOrDefault(f => f.Id == id) is { Id: not null } f
            ? new ReviewCheck { Id = id, Passed = f.Passed, Quote = f.Quote }
            : new ReviewCheck { Id = id, Passed = true })],
    };

    [Theory]
    [InlineData(null, 10)]
    [InlineData("no_repeat", 9)] // minor
    [InlineData("first_bullets", 8)] // major: no 9 with a major failure
    [InlineData("summary_proofs", 7)] // critical
    public void The_score_is_the_checklists_so_nine_means_no_major_failure(string? failed, int score) =>
        Assert.Equal(score, (failed is null ? Checklist() : Checklist((failed, false, ""))).Scored().Score);

    [Fact]
    public void A_failure_with_an_invalid_quote_does_not_turn_into_a_pass()
    {
        var layout = new PdfLayout(1, ["Infrastructure: CI/CD, Git, Docker"], []);

        var review = ResumeTailor.Grounded(Checklist(("keywords", false, "Infrastructure: Docker, Kubernetes")), layout);

        Assert.Equal(8, review.Score);
        Assert.Equal(8, ResumeTailor.Grounded(Checklist(("keywords", false, "CI/CD, Git")), layout).Score);
    }

    [Theory]
    [InlineData("Senior C#/.NET Backend Developer", "Terentiy_Gatsukov_Senior_NET_Backend_Developer_CV.pdf")]
    [InlineData("Senior C# Developer", "Terentiy_Gatsukov_Senior_CSharp_Developer_CV.pdf")]
    [InlineData("Ведущий разработчик", "Terentiy_Gatsukov_Vedushchiy_razrabotchik_CV.pdf")]
    public void The_file_name_is_plain_latin(string headline, string expected) =>
        Assert.Equal(expected, ResumeTailor.FileName(Profile, new ResumeDocument { Headline = headline }));
}
