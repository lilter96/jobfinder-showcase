using JobFinder.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace JobFinder.Tests.Pipeline;

internal static class MigrationTestRunner
{
    // EF commits each earlier Down separately. A later guarded refusal does not restore those schemas.
    // Preserve the original exception/assertion, but restore the current disposable fixture before read-back.
    public static async Task DowngradeAndRestoreAsync(JobFinderDbContext context, string target, CancellationToken ct)
    {
        try { await context.GetService<IMigrator>().MigrateAsync(target, ct); }
        finally { await context.Database.MigrateAsync(CancellationToken.None); }
    }
}
