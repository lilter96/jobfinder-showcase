using System.Diagnostics;
using System.Globalization;
using System.Text;
using JobFinder.App.Cli;
using JobFinder.Core.Domain;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableCommentPreparationIntegrationTests
{
    [Theory]
    [InlineData("--comment-test")]
    [InlineData("--comment-refresh")]
    public async Task Real_cli_uses_the_running_native_consumer_without_a_provider_or_recipient_transport(string command)
    {
        var consumer = await HostAsync(true);
        var profiles = consumer.Services.GetRequiredService<ProfileProvider>();
        await profiles.SaveAsync(Profile, (await profiles.GetSnapshotAsync(Ct))?.Version ?? 0, "synthetic-cli-test", Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var existingCalls = await context.LlmCalls.CountAsync(Ct);
        var draft = new CommentDraft
        {
            PostId = Guid.NewGuid().ToString("N"), PostUrl = "https://threads.invalid/post/synthetic",
            Author = "synthetic-author", PostText = Post, Variants = ["Старый синтетический вариант"],
            Status = CommentStatus.Pending, CreatedAt = DateTimeOffset.UtcNow,
        };
        context.CommentDrafts.Add(draft);
        await context.SaveChangesAsync(Ct);
        var home = Path.Combine(OperatingSystem.IsWindows() ? @"E:\Temp" : Path.GetTempPath(), "jobfinder-comment-cli", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(home, "config"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json"), Path.Combine(home, "config", "appsettings.json"));
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = home, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("JOBFINDER_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["JOBFINDER_HOME"] = home;
        start.Environment["JOBFINDER_Database__ConnectionString"] = context.Database.GetConnectionString();
        start.Environment["JOBFINDER_Paths__Prompts"] = Fixture.PromptsDir;
        start.Environment["JOBFINDER_Llm__ApiKey"] = "synthetic-cli-key";
        start.Environment["JOBFINDER_Llm__Endpoint"] = "https://provider.invalid/completions";
        start.Environment["JOBFINDER_Llm__Proxy"] = "";
        start.Environment["JOBFINDER_Bot__Token"] = "";
        start.Environment["JOBFINDER_Telegram__ApiHash"] = "";
        start.Environment["JOBFINDER_Threads__AccessToken"] = "";
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "JobFinder.Tests.runtimeconfig.json"));
        start.ArgumentList.Add("--depsfile");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "JobFinder.Tests.deps.json"));
        start.ArgumentList.Add(typeof(CliHost).Assembly.Location);
        start.ArgumentList.Add(command);
        start.ArgumentList.Add(draft.Id.ToString(CultureInfo.InvariantCulture));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("isolated comment CLI did not start");
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var error = process.StandardError.ReadToEndAsync(Ct);
        try
        {
            await process.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.True(process.ExitCode == 0, await output + await error);
            Assert.Contains(Good().Variants[0], await output);
            var request = Assert.Single(await context.CommentPreparationRequests.ToListAsync(Ct));
            Assert.Equal(CommentPreparationStage.Completed, request.Stage);
            Assert.Equal(1, _steps.Drafts[request.Id]);
            Assert.Equal(existingCalls, await context.LlmCalls.CountAsync(Ct));
            Assert.Empty(await context.LlmCalls.Where(call => call.OperationId == request.Id).ToListAsync(Ct));
            Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
            Assert.Empty(await context.TelegramSessions.ToListAsync(Ct));
            context.ChangeTracker.Clear();
            var original = await context.CommentDrafts.SingleAsync(row => row.Id == draft.Id, Ct);
            Assert.Equal(command == "--comment-test" ? CommentStatus.Pending : CommentStatus.Expired, original.Status);
            if (command == "--comment-refresh")
            {
                var fresh = await context.CommentDrafts.SingleAsync(row => row.PostId == draft.PostId, Ct);
                Assert.Equal(CommentStatus.Pending, fresh.Status);
                Assert.Equal(Good().Variants, fresh.Variants);
                Assert.Single(await context.Notifications.Where(row => row.Kind == NotificationKind.CommentDraft && row.EntityId == fresh.Id).ToListAsync(Ct));
            }
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            Directory.Delete(home, recursive: true);
        }
    }
}
