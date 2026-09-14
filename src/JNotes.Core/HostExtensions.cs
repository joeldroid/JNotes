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
    /// race to worry about.
    /// </summary>
    public static async Task MigrateJNotesDatabaseAsync(this IServiceProvider services)
    {
        var dbContextFactory = services.GetRequiredService<IDbContextFactory<JNotesDbContext>>();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
    }

    /// <summary>
    /// Ensures at least one <see cref="Data.UserAccount"/> exists (bootstrapping one named "admin"
    /// if the table is empty — not a data migration, there's nothing to preserve, just "the app
    /// must be usable out of the box"), then caches the oldest user's id, plus every user's id, into
    /// the singleton <see cref="IDefaultUserProvider"/> so per-circuit "current user" resolution
    /// never needs its own database round trip. Must run before
    /// <see cref="RunStartupReconciliationAsync"/> and before the app starts serving requests.
    /// </summary>
    public static async Task EnsureDefaultUserAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var defaultUserProvider = scope.ServiceProvider.GetRequiredService<DefaultUserProvider>();

        var users = await userRepository.GetAllUsersAsync();
        var defaultUser = users.Count > 0 ? users[0] : await userRepository.CreateUserAsync("admin");

        defaultUserProvider.SetDefaultUserId(defaultUser.Id);
        defaultUserProvider.SetKnownUserIds(users.Count > 0 ? users.Select(u => u.Id) : [defaultUser.Id]);
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
