using JNotes.Core;
using JNotes.Core.Data;
using JNotes.Core.Indexing;
using JNotes.Core.Notes;
using JNotes.Core.Serialization;
using JNotes.Core.Storage;
using JNotes.Core.Users;
using JNotes.Tests.TestDoubles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace JNotes.Tests.Integration;

/// <summary>
/// Wires up the real <c>AddJNotesCore</c> DI graph against a temp folder, a real temp-file
/// SQLite database (not the in-memory provider), and a real Redis instance (see
/// <see cref="RedisConnectionStringEnvVar"/>), so reconciliation/service tests exercise actual
/// SQLite/Redis and migration behavior rather than a fake. Seeds two users (<see cref="UserAId"/>,
/// <see cref="UserBId"/>) so tests can verify per-user scoping; <see cref="ActAsUserA"/>/
/// <see cref="ActAsUserB"/> switch which one <see cref="NoteService"/> (implicit-current-user)
/// acts as — <c>INoteFileStore</c>/<c>INoteIndexReconciler</c> take explicit userIds instead, so
/// tests exercising those pass <see cref="UserAId"/>/<see cref="UserBId"/> directly.
/// </summary>
internal sealed class CoreServicesFixture : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;
    private readonly string _rootDir;
    private readonly TestCurrentUserContext _currentUserContext;

    private CoreServicesFixture(
        ServiceProvider provider,
        IServiceScope scope,
        string rootDir,
        string notesFolder,
        TestCurrentUserContext currentUserContext,
        Guid userAId,
        Guid userBId)
    {
        _provider = provider;
        _scope = scope;
        _rootDir = rootDir;
        NotesFolder = notesFolder;
        _currentUserContext = currentUserContext;
        UserAId = userAId;
        UserBId = userBId;
    }

    public string NotesFolder { get; }

    public Guid UserAId { get; }

    public Guid UserBId { get; }

    public INoteService NoteService => _scope.ServiceProvider.GetRequiredService<INoteService>();

    public INoteIndexReconciler Reconciler => _scope.ServiceProvider.GetRequiredService<INoteIndexReconciler>();

    public IAllUsersIndexReconciler AllUsersReconciler => _scope.ServiceProvider.GetRequiredService<IAllUsersIndexReconciler>();

    public INoteFileStore FileStore => _scope.ServiceProvider.GetRequiredService<INoteFileStore>();

    public INoteMarkdownSerializer Serializer => _scope.ServiceProvider.GetRequiredService<INoteMarkdownSerializer>();

    public IUserRepository UserRepository => _scope.ServiceProvider.GetRequiredService<IUserRepository>();

    public void ActAsUserA() => _currentUserContext.UserId = UserAId;

    public void ActAsUserB() => _currentUserContext.UserId = UserBId;

    /// <summary>
    /// Overridable so CI/a dev machine can point tests at a Redis instance other than the default
    /// local one — mirrors how <c>REDIS_CONNECTION_STRING</c> itself works for the app.
    /// </summary>
    private const string RedisConnectionStringEnvVar = "JNOTES_TEST_REDIS_CONNECTION_STRING";

    public static async Task<CoreServicesFixture> CreateAsync()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "jnotes-tests-" + Guid.NewGuid());
        var notesFolder = Path.Combine(rootDir, "notes");
        var dbPath = Path.Combine(rootDir, "jnotes.db");
        var redisConnectionString = Environment.GetEnvironmentVariable(RedisConnectionStringEnvVar) ?? "localhost:6379";

        var currentUserContext = new TestCurrentUserContext();

        var services = new ServiceCollection();
        // AddJNotesCore resolves ILogger<T> for a couple of services (e.g. AppTimeZoneProvider,
        // AllUsersIndexReconciler) — a plain ServiceCollection has no logging provider registered
        // by default the way a real host (Program.cs) does, so this is needed for those to resolve.
        services.AddLogging();
        services.AddJNotesCore(new ConfigurationBuilder().Build());
        services.Configure<JNotesOptions>(o =>
        {
            o.NotesFolderPath = notesFolder;
            o.DbPath = dbPath;
            o.RedisConnectionString = redisConnectionString;
        });
        // AddJNotesCore deliberately doesn't register this (see its doc comment) — normally
        // JNotes.Web does, backed by a cookie; here a simple mutable stub stands in.
        services.AddSingleton<ICurrentUserContext>(currentUserContext);

        var provider = services.BuildServiceProvider();
        await provider.MigrateJNotesDatabaseAsync();

        var scope = provider.CreateScope();
        var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var userA = await userRepository.CreateUserAsync("user-a", UserRole.Member, email: null, password: "password123");
        var userB = await userRepository.CreateUserAsync("user-b", UserRole.Member, email: null, password: "password123");
        currentUserContext.UserId = userA.Id; // default to acting as user A

        return new CoreServicesFixture(provider, scope, rootDir, notesFolder, currentUserContext, userA.Id, userB.Id);
    }

    public async ValueTask DisposeAsync()
    {
        await CleanUpRedisAsync();

        _scope.Dispose();
        await _provider.DisposeAsync();

        if (Directory.Exists(_rootDir))
        {
            Directory.Delete(_rootDir, recursive: true);
        }
    }

    /// <summary>
    /// Unlike the temp SQLite file/notes folder, Redis is a shared instance across test runs — leftover
    /// keys wouldn't break anything (the index is a rebuildable cache either way), but there's no
    /// harm in tidying up after ourselves rather than letting a dev's local Redis grow unbounded.
    /// </summary>
    private async Task CleanUpRedisAsync()
    {
        var redis = _scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
        var db = redis.GetDatabase();

        foreach (var userId in new[] { UserAId, UserBId })
        {
            var setKey = (RedisKey)$"jnotes:user:{userId}:notes";
            var noteIds = await db.SetMembersAsync(setKey);
            foreach (var noteId in noteIds)
            {
                await db.KeyDeleteAsync((RedisKey)$"jnotes:note:{noteId}");
            }

            await db.KeyDeleteAsync(setKey);
        }
    }
}
