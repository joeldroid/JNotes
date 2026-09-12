using JNotes.Core.Data;
using JNotes.Core.Indexing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JNotes.Core;

/// <summary>Startup-time operations run once by <c>JNotes.Web</c>'s <c>Program.cs</c> after building the host.</summary>
public static class HostExtensions
{
    /// <summary>
    /// Applies pending EF Core migrations. Uses <c>Database.MigrateAsync()</c> rather than
    /// <c>EnsureCreated()</c> so the schema can evolve later against users' existing databases —
    /// safe here because this runs as a single-instance container with no multi-replica migration
    /// race to worry about.
    /// </summary>
    public static async Task MigrateJNotesDatabaseAsync(this IServiceProvider services)
    {
        var dbContextFactory = services.GetRequiredService<IDbContextFactory<JNotesDbContext>>();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
    }

    /// <summary>
    /// Runs an index reconciliation pass, picking up any note files added/edited/removed while
    /// the app wasn't running. See <see cref="INoteIndexReconciler"/>.
    /// </summary>
    public static async Task<ReconciliationResult> RunStartupReconciliationAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var reconciler = scope.ServiceProvider.GetRequiredService<INoteIndexReconciler>();
        return await reconciler.RescanAsync();
    }
}
