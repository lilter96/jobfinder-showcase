using System.Text;
using JobFinder.Core.Profile;
using JobFinder.Core.Resume;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace JobFinder.Pipeline.Documents;

/// <param name="Key">What a page break before would fix ("job:1", "section:education"); null when a break does not help.</param>
public sealed record LayoutIssue(string? Key, string Message);

/// <param name="PageTexts">The text of each page as an ATS extracts it: what the review reads.</param>
public sealed record PdfLayout(int Pages, IReadOnlyList<string> PageTexts, IReadOnlyList<LayoutIssue> Issues);

/// <summary>
/// Reads the printed resume back (PdfPig, content order — how a parser gets it) and checks what the print rules promise: every job's
/// heading on the page of its first line, no point cut between pages, no section title alone at the bottom of a page, no more than
/// <see cref="MaxPages"/> pages, and every point, the name and the contacts present as text in reading order. Elements are looked up in
/// document order with a moving cursor, so a phrase repeated in the summary is not taken for the point further down.
/// </summary>
public static class ResumePdfCheck
{
    public const int MaxPages = 2;

    public static PdfLayout Check(string pdfPath, ResumeDocument doc, CandidateProfile profile)
        => Check(File.ReadAllBytes(pdfPath), doc, profile);

    public static PdfLayout Check(byte[] content, ResumeDocument doc, CandidateProfile profile)
    {
        using var pdf = PdfDocument.Open(content);
        var texts = pdf.GetPages().Select(p => ContentOrderTextExtractor.GetText(p)).ToList();
        var issues = new List<LayoutIssue>();
        if (content.Length > 2_500_000) issues.Add(new(null, "PDF больше 2,5 MB: превышен лимит разбора Greenhouse"));
        if (texts.Count > MaxPages) issues.Add(new(null, $"{texts.Count} страницы: резюме должно уместиться на {MaxPages}"));

        var text = new Cursor(texts);
        if (!text.Find(ResumeHtml.DisplayName(profile, doc.Language == "en"), out _)) issues.Add(new(null, "имя не извлекается из PDF как текст"));
        foreach (var contact in new[] { profile.Contacts.Email, profile.Contacts.Phone }.Where(c => !string.IsNullOrWhiteSpace(c)))
            if (!new Cursor(texts).Find(contact!, out _))
                issues.Add(new(null, $"контакт «{contact}» не извлекается из PDF как текст"));

        var sections = ResumeHtml.Sections(doc).ToDictionary(s => s.Key, s => s.Title);
        void Heading(string key, string? firstLine)
        {
            if (!sections.TryGetValue(key, out var title) || firstLine is null) return;
            if (!text.Find(title, out var headingPage)) return;
            var after = text.Mark();
            if (text.Find(firstLine, out var page) && page != headingPage)
                issues.Add(new(key, $"заголовок «{title}» внизу страницы {headingPage + 1}, а сам раздел — на странице {page + 1}"));
            text.Reset(after);
        }

        Heading("section:summary", doc.Summary);
        if (!string.IsNullOrWhiteSpace(doc.Summary) && !text.Find(doc.Summary, out _)) issues.Add(new(null, "«О себе» не извлекается из PDF полностью"));
        Heading("section:skills", doc.Skills.FirstOrDefault()?.Group);
        foreach (var group in doc.Skills)
            if (!text.Find(group.Group + " " + string.Join(" ", group.Items), out _)) issues.Add(new(null, $"группа навыков «{group.Group}» не извлекается полностью"));
        if (doc.Familiar.Count > 0 && !text.Find(string.Join(" ", doc.Familiar), out _)) issues.Add(new(null, "«Также знаком» не извлекается полностью"));
        Heading("section:experience", doc.Experience.FirstOrDefault()?.Role);
        for (var i = 0; i < doc.Experience.Count; i++)
        {
            var job = doc.Experience[i];
            // A break before the first job would leave the section title alone: the section starts the page instead.
            var key = i == 0 ? "section:experience" : $"job:{i}";
            if (!text.Find(job.Role + " " + job.Company, out var headPage))
            {
                issues.Add(new(null, $"заголовок места работы «{job.Company}» не извлекается из PDF как текст"));
                continue;
            }

            if (!text.Find(job.Period, out var periodPage)) issues.Add(new(null, $"«{job.Company}»: период работы не извлекается полностью"));
            else if (periodPage != headPage) issues.Add(new(key, $"«{job.Company}»: период оторван от заголовка места работы"));

            var first = job.Context ?? job.Bullets.FirstOrDefault();
            var mark = text.Mark();
            if (job.Context is { Length: > 0 } context && !text.Find(context, out _)) issues.Add(new(null, $"«{job.Company}»: описание проекта не извлекается полностью"));
            text.Reset(mark);
            if (first is not null && text.Find(first, out var firstPage) && firstPage != headPage)
                issues.Add(new(key, $"«{job.Company}»: название на странице {headPage + 1}, а опыт — на странице {firstPage + 1}"));
            text.Reset(mark);
            foreach (var bullet in job.Bullets)
            {
                if (!text.FindRange(bullet, out var startPage, out var endPage))
                {
                    issues.Add(new(null, $"пункт не извлекается из PDF как текст: «{Core.Text.TextUtil.Truncate(bullet, 60)}»"));
                    continue;
                }

                if (bullet == job.Bullets.FirstOrDefault() && startPage != headPage && !issues.Any(issue => issue.Key == key))
                    issues.Add(new(key, $"«{job.Company}»: заголовок и первый пункт опыта на разных страницах"));
                if (endPage != startPage)
                    issues.Add(new(key, $"«{job.Company}»: пункт разорван между страницами {startPage + 1} и {endPage + 1}"));
            }
            if (job.Stack.Count > 0 && !text.Find(string.Join(" ", job.Stack), out _)) issues.Add(new(null, $"«{job.Company}»: стек не извлекается полностью"));
        }

        Heading("section:education", doc.Education.FirstOrDefault()?.Institution);
        foreach (var education in doc.Education)
            if (!text.Find(education.Institution + " " + education.Degree + " " + education.Period, out _)) issues.Add(new(null, "образование не извлекается полностью"));
        Heading("section:languages", doc.Languages.FirstOrDefault());
        if (doc.Languages.Count > 0 && !text.Find(string.Join(" ", doc.Languages), out _)) issues.Add(new(null, "языки не извлекаются полностью"));
        Heading("section:extras", doc.Extras.FirstOrDefault());
        foreach (var extra in doc.Extras)
            if (!text.Find(extra, out _)) issues.Add(new(null, "дополнительный пункт не извлекается полностью"));
        return new PdfLayout(texts.Count, texts, issues);
    }

    /// <summary>The page texts as one string for the review: "--- страница 1 ---" between pages.</summary>
    public static string ForReview(PdfLayout layout)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < layout.PageTexts.Count; i++) sb.Append("--- страница ").Append(i + 1).Append(" ---\n").Append(layout.PageTexts[i]).Append('\n');
        return sb.ToString();
    }

    /// <summary>Letters, digits, # and +, lower case: line breaks, spaces and punctuation of the printed text do not matter.</summary>
    public static string Compact(string s) =>
        string.Concat(s.ToLowerInvariant().Replace('ё', 'е').Where(c => char.IsLetterOrDigit(c) || c is '#' or '+'));

    /// <summary>The document's text with its page boundaries and a position that only moves forward.</summary>
    private sealed class Cursor
    {
        private readonly string _all;
        private readonly List<int> _pageStarts = [];
        private int _position;

        public Cursor(IReadOnlyList<string> pages)
        {
            var sb = new StringBuilder();
            foreach (var p in pages)
            {
                _pageStarts.Add(sb.Length);
                sb.Append(Compact(p));
            }

            _all = sb.ToString();
        }

        public int Mark() => _position;

        public void Reset(int position) => _position = position;

        /// <summary>The next occurrence of <paramref name="what"/> (its first letters) after the cursor; the cursor moves past it.</summary>
        public bool Find(string what, out int page)
        {
            page = -1;
            var probe = Compact(what);
            if (probe.Length == 0) return false;
            var at = _all.IndexOf(probe, _position, StringComparison.Ordinal);
            if (at < 0) return false;
            _position = at + probe.Length;
            page = _pageStarts.FindLastIndex(s => s <= at);
            return true;
        }

        public bool FindRange(string what, out int startPage, out int endPage)
        {
            startPage = endPage = -1;
            var probe = Compact(what);
            if (probe.Length == 0) return false;
            var at = _all.IndexOf(probe, _position, StringComparison.Ordinal);
            if (at < 0) return false;
            _position = at + probe.Length;
            startPage = _pageStarts.FindLastIndex(start => start <= at);
            endPage = _pageStarts.FindLastIndex(start => start < _position);
            return true;
        }
    }
}
