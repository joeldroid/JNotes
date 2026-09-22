using JNotes.Core.Data;
using JNotes.Core.Indexing;
using JNotes.Core.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JNotes.Core;

/// <summary>Startup-time operations run once by <c>JNotes.Web</c>'s <c>Program.cs</c> after building the host.</summary>
public static class HostExtensions
{
    private const int MaxDatabaseConnectAttempts = 10;
    private static readonly TimeSpan DatabaseConnectRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Waits (briefly, boundedly) for PostgreSQL to accept connections, then applies pending EF Core
    /// migrations. Uses Database.MigrateAsync() rather than EnsureCreated() so the schema can evolve
    /// against users' existing databases — safe here because this runs as a single-instance container
    /// with no multi-replica migration race.
    /// The wait exists because EF Core connects lazily: a well-formed but unreachable
    /// connection string (assembled from POSTGRES_HOST/PORT/DB/USER/PASSWORD) would otherwise surface
    /// much later, and a cold `docker compose up`
    /// can reach this line while Postgres is still running initdb. docker-compose.yml also gates the
    /// app on a Postgres healthcheck; this covers `dotnet run` against a just-started container.
    /// A blank connection string is NOT retried — CreateDbContextAsync throws straight out of the
    /// options delegate (see ServiceCollectionExtensions.AddJNotesCore).
    /// </summary>
    public static async Task MigrateJNotesDatabaseAsync(this IServiceProvider services)
    {
        // GetService, not GetRequiredService: some test providers build AddJNotesCore without a
        // logging provider registered.
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("JNotes.Core.HostExtensions");
        var dbContextFactory = services.GetRequiredService<IDbContextFactory<JNotesDbContext>>();

        for (var attempt = 1; ; attempt++)
        {
            await using var db = await dbContextFactory.CreateDbContextAsync();

            if (await db.Database.CanConnectAsync())
            {
                await db.Database.MigrateAsync();
                return;
            }

            if (attempt >= MaxDatabaseConnectAttempts)
            {
                throw new InvalidOperationException(
                    $"Could not reach PostgreSQL after {MaxDatabaseConnectAttempts} attempts — check POSTGRES_HOST/POSTGRES_PORT/POSTGRES_DB/POSTGRES_USER/POSTGRES_PASSWORD.");
            }

            logger?.LogInformation(
                "PostgreSQL isn't accepting connections yet (attempt {Attempt}/{MaxAttempts}); retrying in {DelaySeconds}s.",
                attempt, MaxDatabaseConnectAttempts, DatabaseConnectRetryDelay.TotalSeconds);
            await Task.Delay(DatabaseConnectRetryDelay);
        }
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
    /// added/edited/removed while the app wasn't running. See <see cref="INoteIndexReconciler"/>
    /// and <see cref="IAllUsersIndexReconciler"/> (the shared "loop over everyone" logic, also used
    /// by <c>JNotes.Web.Indexing.AutoIndexBackgroundService</c>'s periodic pass). Behavior change:
    /// one user's reconciliation failure is now logged and skipped rather than aborting startup for
    /// everyone else — see <see cref="AllUsersIndexReconciler"/>.
    /// </summary>
    public static async Task RunStartupReconciliationAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var reconciler = scope.ServiceProvider.GetRequiredService<IAllUsersIndexReconciler>();
        await reconciler.ReconcileAllAsync();
    }
}
