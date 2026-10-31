using System.Globalization;
using System.Security.Cryptography;
using JNotes.Core.Data;
using JNotes.Core.Indexing;
using JNotes.Core.Models;
using JNotes.Core.Storage;
using JNotes.Core.Users;
using JNotes.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Npgsql;

namespace JNotes.UiTests.Infrastructure;

/// <summary>
/// One real JNotes.Web per test run (hosted in this process on a Kestrel port), one throwaway PostgreSQL database,
/// temp notes and keys folders, and one Chromium. Nothing here is mocked: the host is exactly <c>Program.cs</c>,
/// users are created through <see cref="IUserRepository"/> and sign in through the real <c>/login</c> form.
/// Every test seeds its own notes with unique titles; the users are shared and created once.
/// </summary>
public sealed class UiAppFixture : IAsyncLifetime
{
    // The environment is process-wide, so two fixtures starting at once would trample each other's values.
    private static readonly SemaphoreSlim EnvironmentGate = new(1, 1);

    private string _root = "";
    private TestPostgresDatabase? _database;
    private JNotesWebFactory? _factory;
    private IPlaywright? _playwright;

    public ServerErrorLog ServerErrors { get; } = new();

    /// <summary>e.g. <c>http://127.0.0.1:54321/</c>.</summary>
    public string BaseUrl { get; private set; } = "";

    /// <summary>The app host's root provider.</summary>
    public IServiceProvider Services => _factory!.Services;

    public string NotesPath => Path.Combine(_root, "notes");

    public IBrowser Browser { get; private set; } = null!;

    public UiUser Admin { get; private set; } = null!;

    public UiUser Bob { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (!UiSettings.Enabled)
        {
            return; // every test is skipped; start nothing
        }

        if (!UiSettings.SkipBrowserInstall)
        {
            var code = Microsoft.Playwright.Program.Main(["install", "chromium"]);
            if (code != 0)
            {
                throw new InvalidOperationException($"playwright install chromium exited with {code}");
            }
        }

        _root = Path.Combine(Path.GetTempPath(), "jnotes-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(NotesPath);
        Directory.CreateDirectory(Path.Combine(_root, "keys"));
        _database = await TestPostgresDatabase.CreateAsync("jnotes_ui_");
        var pg = new NpgsqlConnectionStringBuilder(_database.ConnectionString);

        // A null value is removed from the environment for the duration, so a developer's own OLLAMA_URL or OIDC_*
        // can't leak into the app (and can't turn on auto-tagging or the OIDC button).
        var environment = new Dictionary<string, string?>
        {
            ["POSTGRES_HOST"] = pg.Host,
            ["POSTGRES_PORT"] = pg.Port.ToString(CultureInfo.InvariantCulture),
            ["POSTGRES_DB"] = pg.Database,
            ["POSTGRES_USER"] = pg.Username,
            ["POSTGRES_PASSWORD"] = pg.Password,
            ["NOTES_PATH"] = NotesPath,
            ["DATA_PROTECTION_KEYS_PATH"] = Path.Combine(_root, "keys"),
            ["GOTENBERG_URL"] = UiSettings.GotenbergUrl, // the route is mapped; the PDF test itself skips if unreachable
            ["OLLAMA_URL"] = null,
            ["AUTO_TAG_ENABLED"] = null,
            ["AUTO_INDEX_INTERVAL_MINUTES"] = "0",
            ["TZ"] = "UTC",
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            // Under `dotnet test` the entry assembly is testhost, so the app's name would default to "testhost" and
            // MapStaticAssets would look for testhost.staticwebassets.endpoints.json. Name it after Web instead,
            // whose manifests are copied next to the tests.
            ["ASPNETCORE_APPLICATIONNAME"] = typeof(global::Program).Assembly.GetName().Name,
            ["OIDC_AUTHORITY"] = null,
            ["OIDC_CLIENT_ID"] = null,
            ["OIDC_CLIENT_SECRET"] = null,
            ["OIDC_DISPLAY_NAME"] = null,
            ["ASPNETCORE_URLS"] = null,
            ["ASPNETCORE_HTTP_PORTS"] = null,
            ["ASPNETCORE_HTTPS_PORTS"] = null,
            ["DOTNET_URLS"] = null,
        };

        _factory = new JNotesWebFactory(ServerErrors);
        await EnvironmentGate.WaitAsync();
        var saved = environment.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var (key, value) in environment)
            {
                Environment.SetEnvironmentVariable(key, value);
            }

            _factory.UseKestrel(0); // a real socket on a free 127.0.0.1 port
            _factory.StartServer(); // runs Program.cs up to app.Run(): migrate, setup state, reconcile, start Kestrel
        }
        finally
        {
            // CreateBuilder() has already snapshotted the environment, so restoring it is safe now.
            foreach (var (key, value) in saved)
            {
                Environment.SetEnvironmentVariable(key, value);
            }

            EnvironmentGate.Release();
        }

        BaseUrl = _factory.ClientOptions.BaseAddress.ToString();

        await using (var scope = Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            Admin = await CreateUserAsync(users, "admin", UserRole.Admin, "admin@example.com");
            Bob = await CreateUserAsync(users, "bob", UserRole.Member, email: null);
        } // CreateUserAsync also flips ISetupStateProvider, so /setup no longer intercepts

        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new() { Headless = !UiSettings.Headed, SlowMo = UiSettings.SlowMo });
        Admin = Admin with { StorageState = await SignInAsync(Admin) };
        Bob = Bob with { StorageState = await SignInAsync(Bob) };
    }

    private static async Task<UiUser> CreateUserAsync(IUserRepository users, string name, UserRole role, string? email)
    {
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        var account = await users.CreateUserAsync(name, role, email, password);
        return new UiUser(account.Id, name, password, StorageState: null);
    }

    // The real static-SSR /login form; the cookie it sets is reused by every test's context.
    private async Task<string> SignInAsync(UiUser user)
    {
        await using var context = await Browser.NewContextAsync(new() { BaseURL = BaseUrl });
        var page = await context.NewPageAsync();
        await page.GotoAsync("/login");
        await page.GetByLabel("Username").FillAsync(user.Username);
        await page.GetByLabel("Password").FillAsync(user.Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await page.WaitForURLAsync(url => !url.Contains("/login", StringComparison.Ordinal));
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "New Note" })).ToBeVisibleAsync();
        return await context.StorageStateAsync();
    }

    /// <summary>Writes a note file and indexes it through the host's own Core services (explicit user id; no HTTP, no circuit).</summary>
    public async Task<Guid> SeedNoteAsync(UiUser owner, string title, string body, params string[] tags)
    {
        var now = DateTimeOffset.UtcNow;
        var note = new Note { Id = Guid.NewGuid(), Title = title, Tags = tags, CreatedAt = now, UpdatedAt = now, Body = body };

        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<INoteFileStore>().WriteAsync(owner.Id, note);
        var result = await scope.ServiceProvider.GetRequiredService<INoteIndexReconciler>().RescanAsync(owner.Id);
        Assert.Empty(result.Errors);
        return note.Id;
    }

    public string NoteFilePath(UiUser owner, Guid id) => Path.Combine(NotesPath, owner.Id.ToString(), $"{id}.md");

    public string TrashedNoteFilePath(UiUser owner, Guid id) => Path.Combine(NotesPath, owner.Id.ToString(), "trash", $"{id}.md");

    /// <summary>Polls the note's file until <paramref name="until"/> holds (autosave is debounced), failing with the last content.</summary>
    public async Task<string> ReadNoteFileEventuallyAsync(UiUser owner, Guid id, Func<string, bool> until, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        var path = NoteFilePath(owner, id);
        var last = "(file not found)";
        while (true)
        {
            try
            {
                if (File.Exists(path))
                {
                    last = await File.ReadAllTextAsync(path);
                    if (until(last))
                    {
                        return last;
                    }
                }
            }
            catch (IOException)
            {
                // Caught mid-write; try again.
            }

            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"The file of note {id} never reached the expected content. Last content:{Environment.NewLine}{last}");
            }

            await Task.Delay(200);
        }
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.CloseAsync();
        }

        _playwright?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync(); // stops the host and its hosted services
        }

        if (_database is not null)
        {
            await _database.DisposeAsync(); // ClearAllPools also clears the in-process app's pools
        }

        if (_root.Length > 0 && Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

public sealed record UiUser(Guid Id, string Username, string Password, string? StorageState);

[CollectionDefinition(Name)]
public sealed class UiCollection : ICollectionFixture<UiAppFixture>
{
    public const string Name = "Ui";
}
