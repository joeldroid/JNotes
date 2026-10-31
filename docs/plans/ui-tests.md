# Plan: Opt-in Playwright browser test suite (`tests/JNotes.UiTests`) that drives the real JNotes.Web in-process

## Goal
Add an opt-in xUnit + Playwright (Chromium) suite that runs the real `JNotes.Web` app in the test process on a real Kestrel port. It uses a throwaway `jnotes_ui_<guid>` database plus temp notes and keys folders, seeds users through Core services, and signs in through the real `/login` form. It automates the "Manual verification" section of `docs/plans/remove-checklist-notes.md`. Plain `dotnet test JNotes.slnx` stays green and fast: the UI project builds, but every test reports Skipped unless `JNOTES_UI_TESTS=1` is set.

## Context found
- **Solution and test conventions**
  - `/Users/joel/Code/Github/JNotes/JNotes.slnx` has `/src/` (Core, Web) and `/tests/` (`tests/JNotes.Tests/JNotes.Tests.csproj`).
  - `JNotes.Tests.csproj` pins xunit 2.9.3, xunit.runner.visualstudio 3.1.4, Microsoft.NET.Test.Sdk 17.14.1, Npgsql 10.0.3 and coverlet 6.0.4, plus `<Using Include="Xunit" />`.
  - `Directory.Build.props` sets net10.0, Nullable, ImplicitUsings and `EnforceCodeStyleInBuild=true`. `.editorconfig` prefers file-scoped namespaces. `global.json` pins SDK 10.0.100 with `latestMinor`.
- **`tests/JNotes.Tests/Infrastructure/TestPostgresDatabase.cs`** (internal) has `CreateAsync()`, which hard-codes the `"jnotes_test_"` prefix, reads `JNOTES_TEST_POSTGRES_CONNECTION_STRING` (default `Host=localhost;Port=5432;Database=jnotes;Username=jnotes;Password=jnotes`), retries `CREATE DATABASE`, and its `DisposeAsync` does `ClearAllPools` then `DROP … WITH (FORCE)`. It exposes `ConnectionString`.
- **`src/JNotes.Web/Program.cs`**
  - `WebApplication.CreateBuilder()` (no args), then `UseEnvironmentVariablesOnly()`.
  - Eager reads of `NOTES_PATH`, `DATA_PROTECTION_KEYS_PATH` and `OIDC_*` happen before `Build()`.
  - After `Build()`, before `app.Run()`: `MigrateJNotesDatabaseAsync`, `InitializeSetupStateAsync`, `RunStartupReconciliationAsync`.
  - The PDF route is mapped only if `PdfExportOptions.IsEnabled` (read once at startup).
  - `UseHttpsRedirection()` is on. The `/setup` redirect middleware reads the singleton `ISetupStateProvider`.
  - There is no `public partial class Program` declaration.
- **`src/JNotes.Web/Hosting/EnvironmentOnlyConfiguration.cs`** removes only `FileConfigurationSource` and `CommandLineConfigurationSource`. The env-var providers stay, and they snapshot the environment when `CreateBuilder()` runs.
- **`src/JNotes.Core/ServiceCollectionExtensions.cs`** reads `NOTES_PATH`, `TZ`, `POSTGRES_HOST/PORT/DB/USER/PASSWORD`, `AUTO_INDEX_INTERVAL_MINUTES`, `OIDC_*`, `GOTENBERG_URL`, `OLLAMA_URL`/`OLLAMA_MODEL`/… inside `Configure<>` lambdas. `JNotesOptions.TimeZoneId` defaults to `"UTC"`.
- **`src/JNotes.Core/HostExtensions.cs`**
  - `MigrateJNotesDatabaseAsync` retries the connection 10 × 2 s.
  - `InitializeSetupStateAsync` sets "setup complete" from `GetAllUsersAsync().Count > 0`.
  - **`EfUserRepository.CreateUserAsync` (line 82) calls `_setupStateProvider.SetSetupComplete(true)`.** So seeding users through the host's own `IUserRepository` after startup clears the `/setup` gate with no restart.
  - The signature is `CreateUserAsync(string username, UserRole role, string? email, string? password, CancellationToken)`. Usernames must be lowercase. `UserRole` is in `JNotes.Core.Data`.
- **Hosted services**
  - `Indexing/AutoIndexBackgroundService` returns immediately when `AUTO_INDEX_INTERVAL_MINUTES=0`.
  - `Tagging/AutoTagBackgroundService` returns when `!_options.IsEnabled` (empty `OLLAMA_URL`).
  - `Images/ImageCleanupBackgroundService` runs one check at startup. With no last-run row it only stores "now" and deletes nothing; after that it polls every 15 min.
  - All three are `public sealed`.
- **Storage helpers usable from tests** (all public)
  - `INoteFileStore.WriteAsync(Guid userId, Note note)` writes `{NotesRoot}/{userId}/{noteId}.md` (`FileSystemNoteFileStore` lines 251–259; the notes root is `ISettingsService.GetNotesFolderPathAsync`, falling back to `NOTES_PATH`).
  - `INoteIndexReconciler.RescanAsync(Guid userId)` returns `ReconciliationResult(Added, Updated, Removed, Errors)`.
  - `Note` (`JNotes.Core.Models`) has required `Id`, `Title`, `CreatedAt`, `UpdatedAt`, `Body`, and `Tags` (`IReadOnlyList<string>`).
  - The canonical front matter (from `NoteMarkdownSerializerTests.Serialize_of_a_canonical_file_is_byte_identical`) is `id`, `title`, `type`, `tags`, `created: …0000000Z`, `updated`.
- **UI selectors that already exist** (verified in markup)
  - Login (`Pages/Login.razor`, static SSR): `<label for="username">Username`, `<label for="password">Password`, submit "Sign in", error text "Invalid username or password.".
  - NavMenu: the search `MudTextField` has `Placeholder="Search"`. New Note is `<MudButton Href="/notes/new" …>New Note</MudButton>` inside `div.jn-nav-new`. Nav links are "All Notes", "Shared with Me" and "Trash". Recent is `h2#jn-nav-recent-heading` plus `.jn-nav-recent-list`, each item with the `Icons.Material.Outlined.Description` icon.
  - `MainLayout`:
    - The menu button has `aria-label="Toggle navigation"`.
    - The drawer is `MudDrawer` Responsive at Md, `_drawerOpen = true`, and closes on navigation at Xs/Sm.
    - `#blazor-error-ui` is present.
    - MudBlazor 9.10.0 CSS has `mud-drawer--closed` (verified in `~/.nuget/packages/mudblazor/9.10.0/staticwebassets/MudBlazor.min.css`).
  - `NoteEditor.razor` (`/notes/{Id:guid}/edit`, `/notes/new`):
    - Title field: `Placeholder="Untitled note"`, `aria-label="Title"`.
    - Tag input: `TagInput` with `Placeholder="Add a tag…"`; its "new tag" option text is `Add new tag “{tag}”`.
    - Draft buttons are "Save" and "Discard". Existing-note buttons are "Close" and "Delete".
    - Status texts: "Not saved yet", "Unsaved changes", "Saving…", "All changes saved".
    - Errors: "Give the note a title before saving." and "This note is no longer available to you …".
    - The discard dialog is "Discard this new note?" with "Discard" and "Keep editing".
    - Debounces are 800 ms (content) and 500 ms (metadata).
  - `TextNoteEditor.razor`:
    - The body is a native `<textarea aria-label="Note body">`.
    - The preview pane is `.note-editor-preview` (shown by default).
    - `OnAfterRenderAsync(firstRender)` imports `./js/image-paste.js`, so that import only happens once the component is interactive.
  - `NoteView.razor`:
    - Title is `h1`. Owner and Edit users see an "Edit" link; Read recipients don't.
    - Share button text is "Share" or "Shared with N". "Export PDF" is an `<a href="/notes/{id}/pdf" target="_blank">`. "Delete" or "Leave" depending on access.
    - `.jn-meta` holds a recipient-only chip "Shared by X · Can edit/View only".
    - Share-dialog text: "People you share with can read this note; with “Can edit” they can also change its text.", with a "Done" button. The delete confirmation is `Delete "{title}"?` with "Delete" and "Cancel".
  - `NoteSharePanel.razor`: autocomplete placeholder "Search people…"; select `aria-label="Permission for the new person"` with options "View only" and "Can edit"; "Add" button; per-row select `aria-label="Permission for {DisplayName}"`. The candidate text is `"{DisplayName} ({Username})"`, and `UserDisplayName.Format` falls back to the username, so bob appears as "bob (bob)".
  - `Trash.razor`: rows are `.jn-trash-item` with `<strong>{title}`, plus "Restore" and "Delete forever" (which confirms `Permanently delete "{title}"?` / "Delete forever").
  - `NoteListItem.razor`: `.jn-note-item` > `a.jn-note-link`; chips are only the share chip and the tags.
  - `NotFound.razor`: `h1` "Not Found".
- **Task lists:** `MarkdownRenderer.cs:25` has `.UseTaskLists()`. `markdown-preview.css` styles `.contains-task-list` and `.task-list-item > input[type="checkbox"]`.
- **PDF endpoint** (`Export/NotePdfEndpoints.cs`): 200 `application/pdf` (inline), 404 when the caller has no access, 503 HTML when Gotenberg fails. Gotenberg receives a self-contained multipart request and never calls back into the app, so an app on 127.0.0.1 works with Gotenberg in Docker.
- **Web project:** `JNotes.Web.csproj` references `Microsoft.AspNetCore.Authentication.OpenIdConnect` 10.0.12 and BannedApiAnalyzers with `PrivateAssets=all`. The analyzer does not flow to a referencing project, so the UI test project may use `System.IO.File` freely.
- **WebApplicationFactory (Mvc.Testing 10.x)**
  - `UseKestrel()`, `UseKestrel(int port)` and `UseKestrel(Action<KestrelServerOptions>)` must be called before `StartServer()`. Port `0` picks a free port.
  - After start, `ClientOptions.BaseAddress` is filled from the server's `IServerAddressesFeature`.
  - It forces the Development environment.
  - The minimal-hosting path runs Program's entry point via `HostFactoryResolver` with `stopApplication: false`, so all code after `Build()` (migrate, setup state, reconcile, pipeline, endpoints) runs before `StartServer()` returns.
  - `ConfigureWebHost` callbacks apply at `Build()`, which is fine for logging and services but too late for Program's eager configuration reads. The earlier hook `ConfigureWebApplicationBuilder` exists only in the .NET 11 docs. That is why configuration goes through process environment variables, as you asked.
  - The content root comes from the `MvcTestingAppManifest.json` that Mvc.Testing generates, so the `.slnx`-vs-`.sln` question of `UseSolutionRelativeContentRoot` shouldn't arise.
- **Package versions checked**
  - Microsoft.Playwright latest stable is **1.64.1** (netstandard2.0, fine on net10.0).
  - Microsoft.AspNetCore.Mvc.Testing latest is 10.0.14. **Pin 10.0.12** to match the Web project's existing ASP.NET 10.0.12 package (bump both together).
  - No `Xunit.SkippableFact` is needed (see Step 4).
- **Browser install:** `playwright.ps1` needs `pwsh`. The documented non-PowerShell route is `Microsoft.Playwright.Program.Main(new[] { "install", "chromium" })`. Browsers go to `~/Library/Caches/ms-playwright` on macOS.
- **CLAUDE.md rules that apply**
  - Never touch the `jnotes` database, `App_Data`, or the `jnotes-debug-*` containers.
  - Tests create and drop their own databases.
  - `dotnet format --verify-no-changes` must pass.
  - Line 660 currently says "No Razor component tests (no bUnit); UI changes are verified manually in a browser"; it gets rewritten in Step 13.

### Design decisions (with rationale)
1. **One app host per test run.** A single xUnit collection fixture `UiAppFixture` holds one host, one database and one Chromium. Tests run serially (`parallelizeTestCollections: false`). Each test is independent: it uses unique titles (`Ui.Unique("Groceries")` gives `"Groceries 3f9a1c2e"`), its own browser context, and seeds its own notes. Users (admin, bob) are seeded once. Restarting the host per class would add about 5–10 s each and would multiply environment-variable juggling for no gain.
2. **Configuration through process environment variables, set only while the host starts.**
   - `CreateBuilder()` snapshots the environment into its env-var providers, and Program's eager reads happen before `StartServer()` returns.
   - So the fixture takes a static `SemaphoreSlim`, saves the current values, sets the JNotes variables, calls `UseKestrel(0)` + `StartServer()`, then restores everything in `finally`.
   - Nothing reads `Environment` directly after startup. (`JNotesDbContextFactory` is design-time only, and the TZ display goes through `JNotesOptions`.)
   - `dotnet test` runs each test assembly in its own testhost process, so this can't leak into `JNotes.Tests`.
   - The lock keeps it safe if a second fixture is ever added.
3. **No DI overrides of app services, and no production auth bypass.** The host is exactly `Program.cs`. Determinism comes from configuration: `AUTO_INDEX_INTERVAL_MINUTES=0`, `OLLAMA_URL` unset, and image cleanup can't delete anything on a fresh database. The only `ConfigureWebHost` addition is a logging provider that records server-side Error logs, so unhandled circuit exceptions fail the test.
4. **Opt-in mechanism: a `[UiFact]` attribute that sets `Skip` unless `JNOTES_UI_TESTS=1`, plus a fixture that does nothing when disabled.**
   - Trade-off: `dotnet test JNotes.slnx` still builds the project, so it can't rot, and runs it in about a second, but prints "Skipped: N" for it.
   - Rejected alternatives:
     - A trait filter: the default run would execute the UI tests.
     - Leaving it out of the slnx: it would rot, and you asked for it to be in.
     - `<IsTestProject>false</IsTestProject>` when the variable is unset: this interacts with Microsoft.NET.Test.Sdk's entry-point generation (`OutputType=Exe`) and IDE discovery, which risks build breaks.
5. **Sign-in:** each user signs in once per run through the real `/login` form. The cookie is captured with `context.StorageStateAsync()` and reused in every test's new context. One explicit login test also covers the form itself, including a wrong password.
6. **Seeding notes:** tests that aren't about creating a note seed it through the host's `INoteFileStore.WriteAsync` + `INoteIndexReconciler.RescanAsync` (explicit userId). That is fast, deterministic, and avoids `NoteService`, which needs an HTTP-bound `ICurrentUserContext`.
7. **No `data-testid` attributes.** Every target already has a stable role, label, placeholder or text, or a semantic class (`.jn-note-item`, `.jn-trash-item`, `.jn-meta`, `.note-editor-preview`, `.markdown-preview`, `.jn-nav-new`, `.jn-nav-recent-list`). Note that MudBlazor puts unmatched attributes on a component's root element, not its `<input>`, so a test id on `MudTextField` would still need `.Locator("input")`. Placeholders are more direct.

## Steps

### Phase 1: Web and shared-helper changes (solution builds; `dotnet test` unchanged)
1. **`src/JNotes.Web/Program.cs`:** build the UI test project first (Step 3). If it fails with CS0122 ("'Program' is inaccessible"), append `public partial class Program;` as the last line with the comment `// Visible to tests/JNotes.UiTests (WebApplicationFactory<Program>).` If the build instead warns that the declaration is unnecessary (.NET 10's generated public `Program`), don't add it. No other Web change.
2. **`tests/JNotes.Tests/Infrastructure/TestPostgresDatabase.cs`:** change `CreateAsync()` to `CreateAsync(string namePrefix = "jnotes_test_")` and use `namePrefix + Guid.NewGuid().ToString("N")`. Update the doc line accordingly. Existing callers are unchanged.

### Phase 2: the UI test project
3. **Create `tests/JNotes.UiTests/JNotes.UiTests.csproj`:**
   - `Sdk="Microsoft.NET.Sdk"`, net10.0, ImplicitUsings, Nullable, `IsPackable=false`.
   - Packages: `Microsoft.AspNetCore.Mvc.Testing` 10.0.12, `Microsoft.NET.Test.Sdk` 17.14.1, `Microsoft.Playwright` 1.64.1, `Npgsql` 10.0.3, `xunit` 2.9.3, `xunit.runner.visualstudio` 3.1.4. No coverlet.
   - `<Using Include="Xunit" />`.
   - `<ProjectReference Include="..\..\src\JNotes.Web\JNotes.Web.csproj" />`.
   - `<Compile Include="..\JNotes.Tests\Infrastructure\TestPostgresDatabase.cs" Link="Infrastructure\TestPostgresDatabase.cs" />`: one shared source instead of a copy or a project reference to the test project.
   - `<Content Include="xunit.runner.json" CopyToOutputDirectory="PreserveNewest" />`.
   - Add it to `JNotes.slnx` under `/tests/`: `<Project Path="tests/JNotes.UiTests/JNotes.UiTests.csproj" />`.
4. **`tests/JNotes.UiTests/xunit.runner.json`:** `{ "parallelizeAssembly": false, "parallelizeTestCollections": false, "longRunningTestSeconds": 60 }`. Parallelization must be off for xUnit v2's `Timeout` to apply.
5. **`Infrastructure/UiSettings.cs`:** static reads of the opt-in variables (none of them app config):
   - `JNOTES_UI_TESTS` (`=="1"`).
   - `JNOTES_UI_HEADED` (`=="1"`) and `JNOTES_UI_SLOWMO` (ms).
   - `JNOTES_UI_GOTENBERG_URL` (default `http://localhost:3000`).
   - `JNOTES_UI_ARTIFACTS` (default `Path.Combine(AppContext.BaseDirectory, "ui-artifacts")`, i.e. under `bin/`, already gitignored).
   - `JNOTES_UI_SKIP_BROWSER_INSTALL` (`=="1"`).
6. **`Infrastructure/UiFactAttribute.cs`:** see the skeleton below. `UiFactAttribute : FactAttribute` sets `Skip` when disabled and `Timeout = 180_000`. `UiGotenbergFactAttribute : UiFactAttribute` additionally skips when `GotenbergProbe.IsReachable` is false. The probe is a cached `Lazy<bool>`: `GET {url}/health` with a 2 s timeout, any exception means false.
7. **`Infrastructure/ServerErrorLog.cs`:** a thread-safe `List<string>` with `Count` and `Since(int mark)`, plus an `ILoggerProvider` whose loggers record `LogLevel.Error`/`Critical` as `"{category}: {message} {exception?.GetType().Name}: {exception?.Message}"`. `IsEnabled` returns true only for Error and above.
8. **`Infrastructure/JNotesWebFactory.cs`:** a `WebApplicationFactory<Program>` subclass (skeleton below) that only adds the logging provider in `ConfigureWebHost`.
9. **`Infrastructure/UiAppFixture.cs`** (`IAsyncLifetime`), plus `UiCollection.cs` with `[CollectionDefinition(UiCollection.Name)] public sealed class UiCollection : ICollectionFixture<UiAppFixture> { public const string Name = "Ui"; }`. Skeleton below. Responsibilities:
   - Install Chromium.
   - Create the database and temp folders.
   - Start the host under the environment lock.
   - Seed admin and bob with random passwords.
   - Launch Chromium.
   - Sign in each user via `/login` and keep their storage state.
   - Expose `BaseUrl`, `Services`, `NotesPath`, `Admin`, `Bob`, `Browser`, `ServerErrors`, `SeedNoteAsync`, `NoteFilePath` and `ReadNoteFileEventuallyAsync`.
   - Dispose in reverse order: browser, Playwright, factory (stops the host), database, temp folder.
10. **`Infrastructure/UiSession.cs`:** per test, per user.
    - Creates a new context (BaseURL, storage state, optional viewport) and starts tracing (screenshots + snapshots).
    - Creates the page and sets `SetDefaultTimeout(15_000)` and `SetDefaultNavigationTimeout(30_000)`.
    - Collects problems:
      - `page.Console` where `Type == "error"`;
      - `page.PageError`;
      - `page.RequestFailed`;
      - `page.Response` with `Status >= 400`;
      - new `ServerErrors` entries since the session started.
    - `Allow(string regex)` adds a per-test allow-list entry. Default allow-list: `requestfailed .* net::ERR_ABORTED` (navigation cancelling in-flight requests, e.g. the login `forceLoad`).
    - `static Task RunAsync(Func<Task> body, params UiSession[] sessions)`: runs the body, then for each session checks that `#blazor-error-ui` is hidden and that no unallowed problems were recorded. On any exception it saves `{Artifacts}/{TestName}-{timestamp}/{user}-screenshot.png` (full page), `{user}-trace.zip` and `problems.txt`, writes their paths to `ITestOutputHelper`, then rethrows. On success it stops tracing without a path (discarded).
    - Also provides `GotoInteractiveAsync(string path)`, which waits for the Blazor circuit (see the skeleton).
    - `DisposeAsync` closes the context.
11. **`Infrastructure/Ui.cs`:** small helpers.
    - `Unique(prefix)`.
    - `WaitForAutosaveAsync(IPage)`: expect "Unsaved changes" visible, then "All changes saved" visible. The 800 ms debounce guarantees the first state is observable.
    - `NoteIdFromUrl(string url)`: regex `/notes/([0-9a-f-]{36})`.
    - `ConfirmDialogAsync(IPage, string buttonName)`: `page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = buttonName, Exact = true }).ClickAsync()`. If MudBlazor 9's message box has no `role="dialog"` (UNVERIFIED), fall back to `page.Locator(".mud-dialog")`.
    - `ListStyleAsync(ILocator)`: `EvaluateAsync<string>("e => getComputedStyle(e).listStyleType")`.
12. **Test classes** (all `[Collection(UiCollection.Name)]`, ctor `(UiAppFixture app, ITestOutputHelper output)`), in `tests/JNotes.UiTests/`. Details in the next section.

### Phase 3: docs and ignore rules
13. **`CLAUDE.md`:**
    - **Commands block:** add
      ```
      JNOTES_UI_TESTS=1 dotnet test tests/JNotes.UiTests   # opt-in Playwright browser suite (Chromium auto-installed on first run)
      ```
      and note that without it the UI tests report Skipped.
    - **Architecture "Three projects":** add `JNotes.UiTests` (xUnit + Playwright). It references Web and hosts `Program` in-process via `WebApplicationFactory<Program>` on Kestrel. It is the only project that references Web.
    - **Tests section, line ~660:** replace "No Razor component tests (no bUnit); UI changes are verified manually in a browser." with a paragraph covering:
      - There are still no bUnit component unit tests.
      - `tests/JNotes.UiTests` is an opt-in end-to-end suite: `JNOTES_UI_TESTS=1`, one in-process host per run, a `jnotes_ui_<guid>` database plus temp `NOTES_PATH`/`DATA_PROTECTION_KEYS_PATH`.
      - Configuration goes through process env vars set only while the host starts (because `CreateBuilder` snapshots the environment and Program reads some keys before `Build()`).
      - Users are seeded via `IUserRepository` (which also flips the setup gate), and sign-in goes through the real `/login`.
      - It needs `jnotes-debug-db`, and `jnotes-debug-pdf` only for the PDF test, which otherwise skips.
      - Failure artifacts go to `bin/…/ui-artifacts`.
      - UI behaviour it doesn't cover (image paste, the PDF's visual look, mermaid in the PDF, dark-theme legibility) is still checked manually.
    - Also list the `JNOTES_UI_*` variables.
14. **`README.md`:**
    - Project layout: add `JNotes.UiTests/  Opt-in Playwright browser tests (drive the real app in-process)`.
    - "Tests": add a subsection with the commands from "Manual verification" step 2, the Chromium install note, and the variables.
    - Line 157: replace "The one further variable, `JNOTES_TEST_POSTGRES_CONNECTION_STRING`, is read only by the test suite" with a sentence covering `JNOTES_TEST_POSTGRES_CONNECTION_STRING` and the `JNOTES_UI_*` variables (test-only, never read by the app).
15. **`.gitignore`:** under the JNotes section add `**/ui-artifacts/` (covers a custom `JNOTES_UI_ARTIFACTS` inside the repo; the default under `bin/` is already ignored). `TestResults/` is already ignored by `[Tt]est[Rr]esult*/`.
16. Run `dotnet format JNotes.slnx`, `dotnet build JNotes.slnx`, `dotnet test JNotes.slnx`, then the opt-in run.

### Skeletons (wording that matters; the executor fills in the rest)

```csharp
// Infrastructure/UiFactAttribute.cs
public class UiFactAttribute : FactAttribute
{
    public UiFactAttribute()
    {
        if (!UiSettings.Enabled)
            Skip = "UI tests are opt-in: JNOTES_UI_TESTS=1 dotnet test tests/JNotes.UiTests";
        Timeout = 180_000; // honoured because xunit.runner.json disables parallelization
    }
}

public sealed class UiGotenbergFactAttribute : UiFactAttribute
{
    public UiGotenbergFactAttribute()
    {
        if (Skip is null && !GotenbergProbe.IsReachable)
            Skip = $"Gotenberg isn't reachable at {UiSettings.GotenbergUrl} (GET /health). Start jnotes-debug-pdf or set JNOTES_UI_GOTENBERG_URL.";
    }
}
```

```csharp
// Infrastructure/JNotesWebFactory.cs
internal sealed class JNotesWebFactory(ServerErrorLog errors) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        // Applied at Build(): fine for logging. App configuration does NOT go here (Program reads some keys before Build()).
        builder.ConfigureLogging(logging => logging.AddProvider(new ServerErrorLogProvider(errors)));
}
```

```csharp
// Infrastructure/UiAppFixture.cs (essentials)
public sealed class UiAppFixture : IAsyncLifetime
{
    private static readonly SemaphoreSlim EnvironmentGate = new(1, 1);
    private string _root = "";
    private TestPostgresDatabase? _database;
    private JNotesWebFactory? _factory;
    private IPlaywright? _playwright;

    public ServerErrorLog ServerErrors { get; } = new();
    public string BaseUrl { get; private set; } = "";        // e.g. "http://127.0.0.1:54321/"
    public IServiceProvider Services => _factory!.Services;  // the app host's root provider
    public string NotesPath => Path.Combine(_root, "notes");
    public IBrowser Browser { get; private set; } = null!;
    public UiUser Admin { get; private set; } = null!;
    public UiUser Bob { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (!UiSettings.Enabled) return; // every test is skipped; start nothing

        if (!UiSettings.SkipBrowserInstall && Microsoft.Playwright.Program.Main(["install", "chromium"]) is var code && code != 0)
            throw new InvalidOperationException($"playwright install chromium exited with {code}");

        _root = Path.Combine(Path.GetTempPath(), "jnotes-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(NotesPath);
        Directory.CreateDirectory(Path.Combine(_root, "keys"));
        _database = await TestPostgresDatabase.CreateAsync("jnotes_ui_");
        var pg = new NpgsqlConnectionStringBuilder(_database.ConnectionString);

        var environment = new Dictionary<string, string?>
        {
            ["POSTGRES_HOST"] = pg.Host, ["POSTGRES_PORT"] = pg.Port.ToString(CultureInfo.InvariantCulture),
            ["POSTGRES_DB"] = pg.Database, ["POSTGRES_USER"] = pg.Username, ["POSTGRES_PASSWORD"] = pg.Password,
            ["NOTES_PATH"] = NotesPath, ["DATA_PROTECTION_KEYS_PATH"] = Path.Combine(_root, "keys"),
            ["GOTENBERG_URL"] = UiSettings.GotenbergUrl,   // route mapped; the PDF test itself skips if unreachable
            ["OLLAMA_URL"] = null, ["AUTO_TAG_ENABLED"] = null, ["AUTO_INDEX_INTERVAL_MINUTES"] = "0",
            ["TZ"] = "UTC", ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["OIDC_AUTHORITY"] = null, ["OIDC_CLIENT_ID"] = null, ["OIDC_CLIENT_SECRET"] = null, ["OIDC_DISPLAY_NAME"] = null,
            ["ASPNETCORE_URLS"] = null, ["ASPNETCORE_HTTP_PORTS"] = null, ["ASPNETCORE_HTTPS_PORTS"] = null, ["DOTNET_URLS"] = null,
        };

        _factory = new JNotesWebFactory(ServerErrors);
        await EnvironmentGate.WaitAsync();
        var saved = environment.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var (key, value) in environment) Environment.SetEnvironmentVariable(key, value); // null = unset
            _factory.UseKestrel(0);   // real socket on a free 127.0.0.1 port
            _factory.StartServer();   // runs Program.cs to app.Run(): migrate, setup state, reconcile, start Kestrel
        }
        finally
        {
            // CreateBuilder() has already snapshotted the environment, so restoring is safe now.
            foreach (var (key, value) in saved) Environment.SetEnvironmentVariable(key, value);
            EnvironmentGate.Release();
        }
        BaseUrl = _factory.ClientOptions.BaseAddress.ToString();

        await using (var scope = Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            Admin = await CreateAsync(users, "admin", UserRole.Admin, "admin@example.com");
            Bob = await CreateAsync(users, "bob", UserRole.Member, email: null);
        } // CreateUserAsync also flips ISetupStateProvider, so /setup no longer intercepts

        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new() { Headless = !UiSettings.Headed, SlowMo = UiSettings.SlowMo });
        Admin = Admin with { StorageState = await SignInAsync(Admin) };
        Bob = Bob with { StorageState = await SignInAsync(Bob) };
    }

    private static async Task<UiUser> CreateAsync(IUserRepository users, string name, UserRole role, string? email)
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

    // Writes a note file and indexes it through the host's own Core services (explicit userId; no HTTP).
    public async Task<Guid> SeedNoteAsync(UiUser owner, string title, string body, params string[] tags) { /* INoteFileStore.WriteAsync + INoteIndexReconciler.RescanAsync; Assert.Empty(result.Errors) */ }
    public string NoteFilePath(UiUser owner, Guid id) => Path.Combine(NotesPath, owner.Id.ToString(), $"{id}.md");
    public async Task<string> ReadNoteFileEventuallyAsync(UiUser owner, Guid id, Func<string, bool> until, TimeSpan? timeout = null) { /* poll every 200 ms, default 10 s; fail with the last content */ }

    public async Task DisposeAsync()
    {
        if (Browser is not null) await Browser.CloseAsync();
        _playwright?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync(); // stops the host and hosted services
        if (_database is not null) await _database.DisposeAsync(); // ClearAllPools also clears the in-process app's pools
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

public sealed record UiUser(Guid Id, string Username, string Password, string? StorageState);
```

```csharp
// UiSession.GotoInteractiveAsync: after a full document load, wait for the Blazor circuit before clicking OnClick handlers.
public async Task GotoInteractiveAsync(string path)
{
    var socket = Page.WaitForWebSocketAsync(new() { Predicate = ws => ws.Url.Contains("/_blazor", StringComparison.Ordinal) });
    await Page.GotoAsync(path);
    var ws = await socket;
    await ws.WaitForFrameReceivedAsync(); // SignalR handshake ack
    await ws.WaitForFrameReceivedAsync(); // first circuit traffic (render batch)
    await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
}
```
Client-side navigations after that (clicking links) reuse the live circuit and need no wait.

**Representative test 1** (scenario 1):

```csharp
[Collection(UiCollection.Name)]
public sealed class NewNoteButtonTests(UiAppFixture app, ITestOutputHelper output)
{
    [UiFact]
    public async Task New_Note_is_a_single_button_that_opens_an_empty_draft()
    {
        await using var s = await UiSession.StartAsync(app, app.Admin, output);
        await UiSession.RunAsync(async () =>
        {
            await s.GotoInteractiveAsync("/");
            var newNote = s.Page.Locator(".jn-nav-new").GetByRole(AriaRole.Link, new() { Name = "New Note" });
            await Expect(newNote).ToHaveCountAsync(1);
            await Expect(newNote).Not.ToHaveAttributeAsync("aria-haspopup", new Regex(".*"));
            await Expect(s.Page.Locator(".jn-nav-new .mud-menu, .jn-nav-new .mud-button-icon-end")).ToHaveCountAsync(0);

            await newNote.ClickAsync();
            await s.Page.WaitForURLAsync(new Regex("/notes/new$"));
            await Expect(s.Page.GetByPlaceholder("Untitled note")).ToHaveValueAsync("");
            await Expect(s.Page.GetByLabel("Note body")).ToHaveValueAsync("");
            await Expect(s.Page.GetByText("Not saved yet")).ToBeVisibleAsync();
            await Expect(s.Page.GetByRole(AriaRole.Button, new() { Name = "Save" })).ToBeVisibleAsync();
        }, s);
    }

    [UiTheory]   // same Skip/Timeout logic as UiFact, deriving from TheoryAttribute
    [InlineData("/notes/new/text")]
    [InlineData("/notes/new/checklist")]
    public async Task Old_typed_new_note_routes_show_not_found(string path)
    {
        await using var s = await UiSession.StartAsync(app, app.Admin, output);
        s.Allow(@"http 404: GET .*/notes/new/"); // the document itself is a 404
        s.Allow(@"console\.error: Failed to load resource: .*404");
        await UiSession.RunAsync(async () =>
        {
            var response = await s.Page.GotoAsync(path);
            Assert.Equal(404, response!.Status);
            await Expect(s.Page.GetByRole(AriaRole.Heading, new() { Name = "Not Found", Level = 1 })).ToBeVisibleAsync();
        }, s);
    }
}
```
(Add `UiTheoryAttribute : TheoryAttribute` with the same constructor logic as `UiFactAttribute`.)

**Representative test 2** (scenario 4, the editor race):

```csharp
[UiFact]
public async Task Clicking_New_Note_right_after_an_edit_keeps_the_edit_and_shows_no_error()
{
    var title = Ui.Unique("Race");
    var id = await app.SeedNoteAsync(app.Admin, title, "original body");
    await using var s = await UiSession.StartAsync(app, app.Admin, output);
    await UiSession.RunAsync(async () =>
    {
        await s.GotoInteractiveAsync($"/notes/{id}/edit");
        var body = s.Page.GetByLabel("Note body");
        await Expect(body).ToHaveValueAsync("original body");

        var marker = "edited " + Guid.NewGuid().ToString("N")[..8];
        var clock = Stopwatch.StartNew();
        await body.FillAsync("original body\n" + marker);  // one input event; the 800 ms content debounce starts
        await s.Page.Locator(".jn-nav-new").GetByRole(AriaRole.Link, new() { Name = "New Note" }).ClickAsync();
        Assert.True(clock.ElapsedMilliseconds < 700,
            $"New Note was clicked {clock.ElapsedMilliseconds} ms after the edit; the race window (800 ms debounce) was missed, so the test proved nothing.");

        await s.Page.WaitForURLAsync(new Regex("/notes/new$"));
        await Expect(s.Page.GetByLabel("Note body")).ToHaveValueAsync("");
        await s.Page.WaitForTimeoutAsync(1500); // past both debounces: a stale save would surface now
        await Expect(s.Page.GetByText("no longer available", new() { Exact = false })).ToHaveCountAsync(0);
        await Expect(s.Page.GetByText("Autosave failed", new() { Exact = false })).ToHaveCountAsync(0);

        var file = await app.ReadNoteFileEventuallyAsync(app.Admin, id, text => text.Contains(marker, StringComparison.Ordinal));
        Assert.Contains("original body", file);

        await s.GotoInteractiveAsync($"/notes/{id}");
        await Expect(s.Page.Locator(".markdown-preview")).ToContainTextAsync(marker);
    }, s);
}
```

## Tests to add or update
All new tests are in `tests/JNotes.UiTests/` (end-to-end, opt-in). The only change to `tests/JNotes.Tests` is the optional `namePrefix` parameter (Step 2).

| # | Class / test | What it does | Value / flake notes |
|---|---|---|---|
| 0 | `SmokeTests.Static_assets_and_login_page_are_served` | `context.APIRequest` GETs `/_framework/blazor.web.js`, `/_content/MudBlazor/MudBlazor.min.css` and `/css/app.css` (expect 200), then `/login` and checks "Welcome to JNotes". | Cheap. Fails fast and clearly if the static-web-assets manifest isn't found in-process. |
| 0b | `SmokeTests.Login_form_signs_in_and_rejects_a_wrong_password` | A fresh context without stored state: wrong password shows "Invalid username or password."; the right one lands on `/` with the New Note link. | Cheap. |
| 1 | `NewNoteButtonTests` (both tests above) | Scenario 1. | Cheap, stable. |
| 2 | `DraftGuardTests.Save_without_title_shows_error` | Body only, Save, expect "Give the note a title before saving.", URL still `/notes/new`. | Stable. |
| 2b | `DraftGuardTests.New_Note_on_a_dirty_draft_prompts_and_Keep_editing_keeps_content` | Fill body, click New Note, dialog "Discard this new note?", "Keep editing", body value unchanged. Then New Note again, "Discard", body empty. | Same-URL navigation. If no prompt appears, that is a product finding (the manual plan says it prompts), not a test bug. |
| 3 | `CreateNoteTests.Create_view_and_list_show_no_type_chip_and_read_only_task_lists` | See details below. | Mermaid loads 3.4 MB: give the SVG expect 20 s. |
| 3b (optional) | `CreateNoteTests.Image_button_uploads_and_renders` | On a draft, `page.Locator("input[type=file]").SetInputFilesAsync` with a generated 1×1 PNG (`FilePayload`). Expect the textarea to contain `](images/` and `.note-editor-preview img` to have `naturalWidth > 0`. | Medium value. MudFileUpload's hidden input is a reliable target. Paste via clipboard is not automated. |
| 4 | `EditorTests.Edit_autosave_Close_persists` | Seed a note, GotoInteractive `/notes/{id}/edit`, fill body, `WaitForAutosaveAsync`, click "Close", expect the view's `.markdown-preview` to contain the text. | Stable. |
| 4b | `EditorTests.Clicking_New_Note_right_after_an_edit…` (above) | Scenario 4. | Timing-based by design. The elapsed-time assertion makes a missed race window fail loudly rather than pass vacuously. |
| 5 | `SearchTests.Search_finds_by_body_text` and `…_by_tag` | Seed with a unique body token `zebra-{suffix}` and tag `tag-{suffix}`. Click `GetByPlaceholder("Search")`, fill, press Enter. Expect URL `/?q=…`, the "Search results for" line, and a `.jn-note-item` with the title. | Stable. |
| 6 | `SharingTests.Edit_share_lets_bob_edit_then_View_only_removes_Edit` | See details below. | Most flake-prone: MudAutocomplete and MudSelect popovers. Wait for each option to be visible before clicking, assert the resulting row or button text, and use `GotoInteractiveAsync` before any OnClick. |
| 7 | `PdfExportTests.Export_link_returns_a_pdf` (`[UiGotenbergFact]`) | See details below. | Skips cleanly without Gotenberg. Text extraction from the PDF is out of scope (no PDF library). |
| 8 | `TrashTests.Delete_restore_and_delete_forever` | See details below. | Stable. |
| 9 | `LegacyChecklistTests.Legacy_checklist_file_opens_as_text_and_is_rewritten_as_text` | See details below. | Stable. Uses host services only, no HTTP shortcut. |
| 10 | `ResponsiveTests.Drawer_closes_after_New_Note_on_a_narrow_viewport` | See details below. | Depends on MudBlazor's viewport JS. Expect with a 10 s timeout. |
| 11 | (built in) | Every test fails on console errors, page errors, failed requests, HTTP ≥ 400, server Error logs, or a visible `#blazor-error-ui`, minus the allow-list. | If Blazor logs a console error on a full-page `GotoAsync` while a circuit is open (e.g. "WebSocket closed with status code: 1006"), add exactly that regex to the default allow-list and document why. |

**Scenario 3 (`CreateNoteTests`) details:**
1. GotoInteractive `/notes/new`.
2. Fill the title `Groceries {suffix}`.
3. Tag `shop-{suffix}`: fill `GetByPlaceholder("Add a tag…")`, then click `GetByText("Add new tag “shop-{suffix}”")`.
4. Fill the body with `- [ ] milk\n- [x] eggs\n\n```mermaid\nflowchart LR\n  A-->B\n```\n`.
5. In the editor preview `.note-editor-preview .task-list-item input[type=checkbox]`: count 2, both `ToBeDisabledAsync`, first `Not.ToBeChecked`, second `ToBeChecked`. `ListStyleAsync(".note-editor-preview ul.contains-task-list")` is `"none"`.
6. Save, wait for URL `/notes/{guid}`.
7. On the view:
   - `.jn-meta .mud-chip` count 0;
   - `GetByText("Checklist", Exact)` count 0;
   - the same checkbox assertions under `.note-view .markdown-preview`, including clicking the first box with `Force = true` and checking it is still unchecked;
   - `.markdown-preview pre.mermaid svg` visible.
8. Go to "All Notes": the `.jn-note-item` with this title has exactly one `.mud-chip`, whose text is the tag.
9. Recent: the `.jn-nav-recent-list` icons' `innerHTML` collapses to one distinct value.

**Scenario 6 (`SharingTests`) details:**
1. Seed the admin's note. GotoInteractive the view, click "Share".
2. Dialog text contains "they can also change its text."
3. Type "bob" in "Search people…", click the option "bob (bob)".
4. Open the "Permission for the new person" select, click "Can edit", click "Add". The row shows bob, and the button reads "Shared with 1" after "Done".
5. Bob's session: GotoInteractive `/shared`, the item has the chip "Shared by admin · Can edit". Open it, click "Edit", fill the body with a marker, `WaitForAutosaveAsync`.
6. Admin: reload the view and expect the marker.
7. Admin: Share, open the "Permission for bob" select, choose "View only", "Done".
8. Bob: GotoInteractive `/notes/{id}`. The "Edit" link count is 0 and the chip shows "View only".

**Scenario 7 (`PdfExportTests`) details:**
1. Seed a note and GotoInteractive the view.
2. "Export PDF" has `href="/notes/{id}/pdf"` and `target="_blank"`.
3. `s.Context.APIRequest.GetAsync($"{app.BaseUrl}notes/{id}/pdf", new() { Timeout = 60_000 })`: status 200, `content-type` starts with `application/pdf`, the body starts with `%PDF`, and `content-disposition` contains `inline`.
4. Optional: Bob (not shared) gets 404 for the same URL. Allow that 404 in Bob's session.

**Scenario 8 (`TrashTests`) details:**
1. Seed note A, GotoInteractive the view, "Delete", confirm "Delete", URL `/`.
2. `/trash`: the `.jn-trash-item` with A's title shows "Deleted …", and its `.mud-chip` count equals its tag count (seed with zero tags, so 0).
3. "Restore": the row disappears, and A is back in All Notes.
4. Trash A again from the view, then on `/trash` click "Delete forever" and confirm "Delete forever". The row is gone and `File.Exists` is false for both `{notes}/{adminId}/{id}.md` and `{notes}/{adminId}/trash/{id}.md`.

**Scenario 9 (`LegacyChecklistTests`) details:**
1. Write raw `{notes}/{adminId}/{id}.md`:
   ```
   ---\nid: {id}\ntitle: Old list {suffix}\ntype: checklist\ntags: []\ncreated: 2026-01-02T03:04:05.0000000Z\nupdated: 2026-01-02T03:04:05.0000000Z\n---\n- [ ] a\n- [x] b
   ```
2. In a host scope, `INoteIndexReconciler.RescanAsync(app.Admin.Id)`: `Added == 1` and no `Errors`.
3. GotoInteractive `/`: the item is present. Open it: two disabled checkboxes, the first unchecked, the second checked, and no chips in `.jn-meta`.
4. "Edit": append ` c`, `WaitForAutosaveAsync`. `ReadNoteFileEventuallyAsync` until the file contains `type: text`. Assert it doesn't contain `checklist`, and does contain `- [ ] a` and `- [x] b`.

**Scenario 10 (`ResponsiveTests`) details:**
1. Context `ViewportSize = 800×1000` (MudBlazor's Sm breakpoint, below Md at 960).
2. GotoInteractive `/`.
3. If `.mud-drawer` has the class `mud-drawer--closed`, click `GetByRole(Button, Name="Toggle navigation")` and expect the drawer not to have that class.
4. Click New Note, expect URL `/notes/new`, then expect `.mud-drawer` `ToHaveClassAsync(new Regex("mud-drawer--closed"))`.

## Manual verification (for the tester)
Prerequisites:
- `jnotes-debug-db` (localhost:5432) is running. `jnotes-debug-pdf` (localhost:3000) is needed only for the PDF test.
- Do not start, stop, recreate or `down` any debug container.
- Do not touch the `jnotes` database or `src/JNotes.Web/App_Data`.

1. **Default run stays green and fast:**
   ```bash
   dotnet build JNotes.slnx                        # 0 errors, 0 new warnings
   dotnet test JNotes.slnx                         # JNotes.Tests passes; JNotes.UiTests: all Skipped, no browser launched, ~seconds
   dotnet format JNotes.slnx --verify-no-changes   # exit 0
   ```
   Confirm the skip reason in the output reads "UI tests are opt-in: JNOTES_UI_TESTS=1 …".
2. **Opt-in run** (the first run downloads Chromium to `~/Library/Caches/ms-playwright`; later runs are no-ops):
   ```bash
   JNOTES_UI_TESTS=1 dotnet test tests/JNotes.UiTests --logger "console;verbosity=normal"
   ```
   Expected: everything passes, and the PDF test passes too if Gotenberg is up.
   - CI-style variant:
     ```bash
     JNOTES_UI_TESTS=1 JNOTES_UI_ARTIFACTS="$PWD/TestResults/ui-artifacts" dotnet test tests/JNotes.UiTests -c Release --logger "trx;LogFileName=ui.trx" --results-directory TestResults/ui
     ```
   - Single class: add `--filter "FullyQualifiedName~SharingTests"`.
   - Watch it: add `JNOTES_UI_HEADED=1 JNOTES_UI_SLOWMO=250`.
   - Manual browser install instead of the automatic one: `pwsh tests/JNotes.UiTests/bin/Debug/net10.0/playwright.ps1 install chromium` (needs `brew install powershell`), then set `JNOTES_UI_SKIP_BROWSER_INSTALL=1`.
3. **Gotenberg skip** (without touching the container):
   ```bash
   JNOTES_UI_TESTS=1 JNOTES_UI_GOTENBERG_URL=http://localhost:1 dotnet test tests/JNotes.UiTests --filter "FullyQualifiedName~PdfExportTests"
   ```
   Expected: 1 Skipped, and the message names the URL.
4. **Failure artifacts:** temporarily change one expected string in `SearchTests`, run that class, and confirm:
   - the test fails;
   - the output prints paths to `…/bin/Debug/net10.0/ui-artifacts/<Test>-<time>/admin-screenshot.png`, `admin-trace.zip` and `problems.txt`;
   - the trace opens at https://trace.playwright.dev (it loads locally).

   Then revert with `git checkout -- tests/JNotes.UiTests/SearchTests.cs`.
5. **No leftovers after a run:**
   ```bash
   docker exec jnotes-debug-db psql -U jnotes -d postgres -c "SELECT datname FROM pg_database WHERE datname LIKE 'jnotes_ui_%';"   # 0 rows
   ls "${TMPDIR:-/tmp}" | grep jnotes-ui- ; echo "exit $?"                                                                        # nothing listed
   ```
   `git status` should show no files under `src/JNotes.Web/App_Data`, and the `jnotes` database should be untouched.
6. **Isolation from your shell:** run step 2 with `OLLAMA_URL=http://localhost:11434 OIDC_AUTHORITY=https://example.invalid` exported. It should still pass: the fixture unsets these during startup, so no auto-tagging and no OIDC button appear.
7. Run step 2 three times in a row and report any test that failed even once (flake triage).

## Risks and edge cases
- **Blazor prerender vs circuit:** clicking an `OnClick` button before the circuit attaches does nothing. Mitigations:
  - `GotoInteractiveAsync` after every full page load.
  - Every action is followed by an auto-retrying `Expect` on its outcome.
  - `Href` buttons (New Note, Edit, Close) work either way.
  - If the websocket-frame heuristic proves flaky, fall back to waiting for an interactive-only side effect such as the `/js/image-paste.js` request on editor pages (a fresh document only).
- **Static web assets in-process:** `MapStaticAssets` needs Web's `*.staticwebassets.endpoints.json` where the factory's host looks. Smoke test 0 detects a problem. The fix is in the test csproj (copy the manifest into the test output, or set content root via the `TEST_CONTENTROOT_JNOTES_WEB` setting), never in Web.
- **Environment mutation:** process-wide, but limited to the startup window, under a lock, restored in `finally`, and in a testhost process separate from `JNotes.Tests`. `TZ` is set to `UTC` (the app default anyway) only during startup.
- **`UseHttpsRedirection`** with an http-only Kestrel logs a Warning ("Failed to determine the https port"). That is not an Error, so the server-error check is unaffected.
- **Development environment** (forced by the factory) means the `UseExceptionHandler`/HSTS branch is not exercised. That is acceptable; it is the same as local dev.
- **Shared database across tests:** Recent and Shared with Me accumulate entries. Every assertion must scope to a unique title or id, never to counts of whole lists.
- **Autosave timing:** `WaitForAutosaveAsync` relies on the 800 ms debounce making "Unsaved changes" observable. For persistence checks, prefer polling the file on disk.
- **Microsoft.Playwright adds about 100+ MB of driver files** to `bin` on every build of the solution. Accepted cost of keeping the project in the slnx.
- **Mvc.Testing 10.0.12 vs the installed runtime:** pinning to the Web project's existing 10.0.12 avoids requiring a newer runtime than the one already in use.
- **`[UiFact]` Timeout** only works because parallelization is off. Don't re-enable it in `xunit.runner.json`.
- **Gotenberg probe** runs at test discovery (2 s max, cached). A Gotenberg that is up but broken gives a real failure (503), which is correct.
- **MudBlazor dialog role** is UNVERIFIED. If `GetByRole(AriaRole.Dialog)` matches nothing, use `.mud-dialog`.

## Open questions
1. **Automatic Chromium download:** the fixture calls `Microsoft.Playwright.Program.Main(["install", "chromium"])` on every opt-in run. It is a no-op once installed, but the first run downloads about 150 MB to `~/Library/Caches/ms-playwright`. Default: yes, with `JNOTES_UI_SKIP_BROWSER_INSTALL=1` to opt out. The alternative is to require `pwsh … playwright.ps1 install chromium` manually, since `pwsh` may not be on this Mac.
2. **Optional test 3b (image upload via the Image button):** default is to include it. It is cheap and covers `/images/{file}`. Clipboard paste stays manual.

## Out of scope
- Any production behaviour change beyond the optional `public partial class Program;` line. No test endpoints, no auth bypass, no `data-testid`s, no render-mode or prerender changes.
- bUnit or component unit tests. Visual or PDF-content checks (the light theme, mermaid inside the PDF, legibility in the dark theme stay manual). Clipboard image paste. OIDC.
- Docker, compose files, `launchSettings.json`, the debug containers, the `jnotes` database, `App_Data`.
- `docs/plans/*.md` (historical). Save this plan as `docs/plans/ui-tests.md` only if you ask for it.
- Adding a CI workflow (there is none in the repo). The CI-style command above is the hand-off.

Relevant files:
- `/Users/joel/Code/Github/JNotes/src/JNotes.Web/Program.cs`
- `/Users/joel/Code/Github/JNotes/src/JNotes.Web/Hosting/EnvironmentOnlyConfiguration.cs`
- `/Users/joel/Code/Github/JNotes/src/JNotes.Core/ServiceCollectionExtensions.cs`
- `/Users/joel/Code/Github/JNotes/src/JNotes.Core/HostExtensions.cs`
- `/Users/joel/Code/Github/JNotes/src/JNotes.Core/Users/EfUserRepository.cs`
- `/Users/joel/Code/Github/JNotes/tests/JNotes.Tests/Infrastructure/TestPostgresDatabase.cs`
- `/Users/joel/Code/Github/JNotes/tests/JNotes.Tests/JNotes.Tests.csproj`
- `/Users/joel/Code/Github/JNotes/JNotes.slnx`
- `/Users/joel/Code/Github/JNotes/.gitignore`
- `/Users/joel/Code/Github/JNotes/CLAUDE.md`
- `/Users/joel/Code/Github/JNotes/README.md`
- `/Users/joel/Code/Github/JNotes/src/JNotes.Web/Components/Pages/NoteEditor.razor`
- `/Users/joel/Code/Github/JNotes/src/JNotes.Web/Components/Pages/NoteView.razor`
- `/Users/joel/Code/Github/JNotes/src/JNotes.Web/Components/Pages/Trash.razor`
- `/Users/joel/Code/Github/JNotes/src/JNotes.Web/Components/Pages/Login.razor`
- `/Users/joel/Code/Github/JNotes/src/JNotes.Web/Components/Layout/NavMenu.razor`
- `/Users/joel/Code/Github/JNotes/src/JNotes.Web/Components/Layout/MainLayout.razor`
- `/Users/joel/Code/Github/JNotes/src/JNotes.Web/Components/Notes/NoteSharePanel.razor`
- `/Users/joel/Code/Github/JNotes/src/JNotes.Web/Components/Notes/TextNoteEditor.razor`
- `/Users/joel/Code/Github/JNotes/src/JNotes.Web/Components/Notes/TagInput.razor`

Sources:
- [WebApplicationFactory.UseKestrel (Microsoft Learn, aspnetcore-10.0)](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.mvc.testing.webapplicationfactory-1.usekestrel?view=aspnetcore-10.0&viewFallbackFrom=aspnetcore-7.0)
- [WebApplicationFactory.cs source (source.dot.net)](https://source.dot.net/Microsoft.AspNetCore.Mvc.Testing/WebApplicationFactory.cs.html)
- [WebApplicationFactory.ConfigureWebApplicationBuilder (aspnetcore-11.0 docs only)](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.mvc.testing.webapplicationfactory-1.configurewebapplicationbuilder?view=aspnetcore-11.0)
- [In-memory automated UI testing ASP.NET Core (DEV)](https://dev.to/jdinnovensa/in-memory-automated-ui-testing-aspnet-core-106d)
- [Testing ASP.NET Core Web API with WebApplicationFactory (Dev Leader, .NET 10)](https://www.devleader.ca/2026/06/08/testing-aspnet-core-web-api-webapplicationfactory-and-integration-tests)
- [Playwright .NET browsers / install docs](https://playwright.dev/dotnet/docs/browsers)
- [Microsoft.Playwright on NuGet](https://www.nuget.org/packages/Microsoft.Playwright)
- [Microsoft.AspNetCore.Mvc.Testing on NuGet](https://www.nuget.org/packages/Microsoft.AspNetCore.Mvc.Testing)
- [Xunit.SkippableFact on NuGet](https://www.nuget.org/packages/Xunit.SkippableFact) (considered, not used)

## Decisions
User-confirmed: in-process app + Playwright, users seeded through Core services, no test-only endpoints, opt-in run (`JNOTES_UI_TESTS=1`). Open questions resolved with the planner's defaults: (1) the fixture auto-installs Chromium on opt-in runs (`JNOTES_UI_SKIP_BROWSER_INSTALL=1` opts out); (2) include optional test 3b (image upload via the Image button).

## Implementation notes
Deviations and findings from the implementation (the plan above is unchanged):
- **`public partial class Program;` was not needed.** The UI test project compiled against `WebApplicationFactory<global::Program>` with no CS0122, so `src/JNotes.Web/Program.cs` is untouched. The factory writes `global::Program` because `Microsoft.Playwright.Program` would otherwise make the name ambiguous in files that import that namespace.
- **`ASPNETCORE_APPLICATIONNAME=JNotes.Web` is set with the other startup environment variables.** Under `dotnet test` the entry assembly is `testhost`, so `MapStaticAssets` looked for `testhost.staticwebassets.endpoints.json` and the host failed to start (the "static web assets in-process" risk). Naming the app after Web makes it use the `JNotes.Web.staticwebassets.*.json` manifests that are already copied next to the tests. No csproj copy step and no Web change.
- **`<Using Include="Xunit.Abstractions" />`** was added to the csproj (for `ITestOutputHelper`).
- **The factory also sets the minimum log level to Warning** (EF logs every query at Information and flooded the test output); the Error-and-above provider is unaffected.
- **Failure artifacts are named `{user}-screenshot.png`, `{user}-trace.zip` and `{user}-problems.txt`** (one problem file per session instead of a single `problems.txt`); the folder is `ui-artifacts/<Test>-<time>/` as planned.
- **Test 2b is split in three.** `New_Note_on_a_dirty_draft_prompts_and_Keep_editing_keeps_content`, `New_Note_then_Discard_gives_an_empty_draft` (title, tag chip and body all reset) and `New_Note_on_an_empty_draft_does_not_prompt`. The Discard test first exposed a genuine app bug: New Note on `/notes/new` is a same-URL navigation, the router re-gave the mounted `NoteEditor` identical parameters, `OnParametersSetAsync` never rebuilt the draft, and "Discard" left the old text on screen. Fixed afterwards (authorised) in `NoteEditor.razor`: `ConfirmLeaveAsync` calls the extracted `ResetDraft()` after a confirmed discard whose target is `/notes/new`; `_draftVersion` is keyed on `TagInput`. The suite is 20 tests, all passing.
- Everything else matches the plan; the tests were not skipped or weakened to get a green run.
- **Follow-up: note-to-note editor switch.** `EditorTests.Switching_straight_to_another_notes_editor_keeps_the_edit_and_leaves_the_other_note_alone` covers `/notes/A/edit` -> `/notes/B/edit` (client-side, via `Blazor.navigateTo`, so the editor instance is reused). Before the fix, A's last edit inside the autosave window was lost and A's pending debounce re-saved B's unchanged note. The flush/cancel/swap logic was generalised into `NoteEditor.SwapNoteAsync` (draft and note swaps), with `_noteGeneration` so a save already queued for the outgoing note skips itself. The test was confirmed to fail against the previous `NoteEditor.razor`.
