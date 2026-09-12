using JNotes.Core;
using JNotes.Core.Indexing;
using JNotes.Core.Notes;
using JNotes.Core.Serialization;
using JNotes.Core.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JNotes.Tests.Integration;

/// <summary>
/// Wires up the real <c>AddJNotesCore</c> DI graph against a temp folder and a real temp-file
/// SQLite database (not the in-memory provider), so reconciliation/service tests exercise actual
/// SQLite and migration behavior rather than a fake.
/// </summary>
internal sealed class CoreServicesFixture : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;
    private readonly string _rootDir;

    private CoreServicesFixture(ServiceProvider provider, IServiceScope scope, string rootDir, string notesFolder)
    {
        _provider = provider;
        _scope = scope;
        _rootDir = rootDir;
        NotesFolder = notesFolder;
    }

    public string NotesFolder { get; }

    public INoteService NoteService => _scope.ServiceProvider.GetRequiredService<INoteService>();

    public INoteIndexReconciler Reconciler => _scope.ServiceProvider.GetRequiredService<INoteIndexReconciler>();

    public INoteFileStore FileStore => _scope.ServiceProvider.GetRequiredService<INoteFileStore>();

    public INoteMarkdownSerializer Serializer => _scope.ServiceProvider.GetRequiredService<INoteMarkdownSerializer>();

    public static async Task<CoreServicesFixture> CreateAsync()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "jnotes-tests-" + Guid.NewGuid());
        var notesFolder = Path.Combine(rootDir, "notes");
        var dbPath = Path.Combine(rootDir, "jnotes.db");

        var services = new ServiceCollection();
        services.AddJNotesCore(new ConfigurationBuilder().Build());
        services.Configure<JNotesOptions>(o =>
        {
            o.NotesFolderPath = notesFolder;
            o.DbPath = dbPath;
        });

        var provider = services.BuildServiceProvider();
        await provider.MigrateJNotesDatabaseAsync();

        return new CoreServicesFixture(provider, provider.CreateScope(), rootDir, notesFolder);
    }

    public async ValueTask DisposeAsync()
    {
        _scope.Dispose();
        await _provider.DisposeAsync();

        if (Directory.Exists(_rootDir))
        {
            Directory.Delete(_rootDir, recursive: true);
        }
    }
}
