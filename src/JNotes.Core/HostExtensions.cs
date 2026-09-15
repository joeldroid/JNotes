using JNotes.Core.Data;
using JNotes.Core.Indexing;
using JNotes.Core.Users;
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
    /// race to worry about. Also switches the database to WAL journal mode (see below).
    /// </summary>
    public static async Task MigrateJNotesDatabaseAsync(this IServiceProvider services)
    {
        var dbContextFactory = services.GetRequiredService<IDbContextFactory<JNotesDbContext>>();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        await db.Database.MigrateAsync();

        // WAL mode: readers (note list, search) never block on a writer (autosave) and vice versa
        // — SQLite's default rollback-journal mode takes an exclusive lock on the whole file for
        // every write. This is persisted in the database file itself (sticky across restarts, not
        // per-connection), so running it here is really only doing work once — cheap and
        // self-healing to run on every startup regardless (e.g. if the file was ever replaced by a
        // plain-mode backup). Two extra files appear alongside the .db file (`-wal`, `-shm`) —
        // anything that backs up jnotes.db needs to account for them (see README).
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    }

    /// <summary>
    /// Seeds the singleton <see cref="ISetupStateProvider"/> from whether any
    /// <see cref="Data.UserAccount"/> exists yet, so the "redirect everything to /setup until an
    /// admin exists" gate costs zero database I/O per request. No account is auto-created here —
    /// unlike the old "act as" prototype, the very first admin is created through the real /setup
    /// screen, with a real password. Must run before the app starts serving requests.
    /// </summary>
    public static async Task InitializeSetupStateAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var setupStateProvider = scope.ServiceProvider.GetRequiredService<SetupStateProvider>();

        var users = await userRepository.GetAllUsersAsync();
        setupStateProvider.SetSetupComplete(users.Count > 0);
    }

    /// <summary>
    /// Runs an index reconciliation pass for every user, picking up any note files
    /// added/edited/removed while the app wasn't running. See <see cref="INoteIndexReconciler"/>.
    /// </summary>
    public static async Task RunStartupReconciliationAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var reconciler = scope.ServiceProvider.GetRequiredService<INoteIndexReconciler>();

        var users = await userRepository.GetAllUsersAsync();
        foreach (var user in users)
        {
            await reconciler.RescanAsync(user.Id);
        }
    }
}
