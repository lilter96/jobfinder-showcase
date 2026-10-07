using JobFinder.App;
using JobFinder.App.Cli;
using JobFinder.App.Components;
using JobFinder.Bot;
using JobFinder.Data;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Messaging;
using JobFinder.ServiceDefaults;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

// Everything relative (config/, prompts/, profile/, data/) resolves against the JobFinder home: the repo root locally, /app in the container.
var home = AppHome.Find();
Directory.SetCurrentDirectory(home);

if (CliCommands.TryGet(args, out var command)) return await CliHost.RunAsync(command, args, home);

// The content root is the JobFinder home (config/, data/…), not the project: the web root is next to the binary after a publish,
// and a local build serves its static assets (app.css, blazor.web.js) through the static-web-assets manifest in bin/.
var publishedWebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = home,
    WebRootPath = Directory.Exists(publishedWebRoot) ? publishedWebRoot : null,
});
AppHome.AddConfiguration(builder.Configuration);
if (!Directory.Exists(publishedWebRoot)) builder.WebHost.UseStaticWebAssets();
builder.Logging.ClearProviders().AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
// .NET Aspire service defaults: OpenTelemetry to the Aspire dashboard (OTLP), /metrics for Prometheus, /health and /alive.
builder.AddServiceDefaults();

builder.Services.AddJobFinderCore(builder.Configuration);
builder.AddJobFinderMessaging(consumer: true);
builder.Services.AddJobFinderPipeline();
builder.Services.AddJobFinderBot(builder.Configuration, withLoops: true);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
// Antiforgery/circuit keys survive a container re-create: they live in the database with the rest of the state.
builder.Services.AddDataProtection().PersistKeysToDbContext<JobFinderDbContext>().SetApplicationName("JobFinder");
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(20));

var app = builder.Build();
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/error", createScopeForErrors: true);
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapServiceDefaults();
// Generated documents by id: only paths the database knows are served (the dashboard listens on 127.0.0.1 only).
app.MapGet("/files/resume/{id:long}", async (long id, IDbContextFactory<JobFinderDbContext> db) =>
{
    await using var ctx = await db.CreateDbContextAsync();
    var r = await ctx.TailoredResumes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);
    return r is null || !JobFinder.Core.Options.StoredPath.Exists(r.PdfPath) ? Results.NotFound()
        : Results.File(JobFinder.Core.Options.StoredPath.Resolve(r.PdfPath), "application/pdf", r.FileName);
});
app.MapGet("/files/pack/{id:long}", async (long id, IDbContextFactory<JobFinderDbContext> db) =>
{
    await using var ctx = await db.CreateDbContextAsync();
    var path = await ctx.Interviews.AsNoTracking().Where(i => i.Id == id).Select(i => i.PackPdfPath).FirstOrDefaultAsync();
    return !JobFinder.Core.Options.StoredPath.Exists(path) ? Results.NotFound()
        : Results.File(JobFinder.Core.Options.StoredPath.Resolve(path!), "application/pdf", $"interview-{id}.pdf");
});
await app.RunAsync();
return 0;
