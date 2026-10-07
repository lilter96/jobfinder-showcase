using System.Security.Cryptography;
using System.Text.Json;
using JobFinder.Core.Options;
using JobFinder.Core.Text;
using JobFinder.Pipeline.Modules.Preparation;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace JobFinder.Tests.Pipeline;

public sealed class InterviewPreparationArtifactTests : IDisposable
{
    private readonly string _directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "jobfinder-interview-artifact-" + Guid.NewGuid().ToString("N")));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("valid")]
    [InlineData("empty")]
    [InlineData("missing")]
    [InlineData("hash")]
    [InlineData("changed")]
    [InlineData("foreign-path")]
    public async Task Saved_pdf_requires_exact_operation_path_matching_bytes_and_readable_text(string damage)
    {
        var seed = new InterviewPreparationSeed { Source = new() { Id = 10 }, Operation = new() { OperationId = Guid.NewGuid() } };
        var paths = new PathsOptions { Data = _directory };
        var path = Path.Combine(_directory, "interviews", "10", seed.Operation.OperationId.ToString("N"), "pack.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        if (damage != "empty") page.AddText("Synthetic interview preparation", 12, new PdfPoint(40, 800), builder.AddStandard14Font(Standard14Font.Helvetica));
        var bytes = builder.Build();
        await File.WriteAllBytesAsync(path, bytes, Ct);
        var checkpoint = new InterviewPrintCheckpoint { Path = StoredPath.ToStored(path), Hash = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
        if (damage == "missing") File.Delete(path);
        if (damage == "hash") checkpoint.Hash = new string('0', 64);
        if (damage == "changed") await File.WriteAllTextAsync(path, "changed bytes", Ct);
        if (damage == "foreign-path") checkpoint.Path = Path.Combine(_directory, "other.pdf");
        if (damage == "valid") await InterviewPreparationSteps.VerifyPrintAsync(paths, seed, checkpoint, Ct);
        else await Assert.ThrowsAsync<UnsupportedInterviewPreparationException>(() => InterviewPreparationSteps.VerifyPrintAsync(paths, seed, checkpoint, Ct));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("malformed")]
    [InlineData("hash")]
    [InlineData("missing")]
    public void Saved_stage_json_requires_a_matching_canonical_checksum(string damage)
    {
        var json = JsonSerializer.Serialize(new InterviewPlanCheckpoint { Model = "synthetic/model" }, InterviewPreparationJsonContext.Default.InterviewPlanCheckpoint);
        var hash = CanonicalJson.Hash(json);
        if (damage == "valid")
            Assert.Equal("synthetic/model", InterviewPreparationRequests.ReadCheckpoint(json, hash, InterviewPreparationJsonContext.Default.InterviewPlanCheckpoint).Model);
        else
            Assert.Throws<UnsupportedInterviewPreparationException>(() => InterviewPreparationRequests.ReadCheckpoint(damage == "missing" ? null : damage == "malformed" ? "{" : json,
                damage == "hash" ? "invalid" : hash, InterviewPreparationJsonContext.Default.InterviewPlanCheckpoint));
    }

    public void Dispose()
    {
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, _directory, StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
