# Plan: periodic auto-indexing background service (JNotes)

## Background / why this is needed

Verified against the current code before planning:

- **Create, trash/delete, tag/metadata edits** (`NoteService.WriteAndIndexAsync`, file-then-index)
  are already indexed immediately — no gap.
- **Body-only edits** (debounced autosave, checklist toggles — `NoteSaveScope.ContentOnly` in
  `NoteService.AutosaveAsync`) write only the file and deliberately skip the index. Catch-up relies
  entirely on `NoteIndexReconciler.RescanAsync`, which today only runs at startup
  (`HostExtensions.RunStartupReconciliationAsync`) and from Settings → "Rescan Now" (manual,
  current-user-only).
- There is **no periodic/background reindexing today** — no `BackgroundService`/`IHostedService`/
  `PeriodicTimer` exists anywhere in the codebase.

So: add a configurable periodic background service that reconciles every user's index, catching up
`ContentOnly` autosaves and externally-edited/added/removed files without a restart or manual click.

## Key findings from investigation (decisions hinge on these)

1. **`Microsoft.Extensions.Hosting.Abstractions` is NOT available to `JNotes.Core` today.**
   Confirmed via `src/JNotes.Core/obj/project.assets.json` — Core resolves
   `Microsoft.Extensions.Options/Logging/DependencyInjection/Configuration/Diagnostics`
   (transitively via EF Core + Identity.Core), but not `Microsoft.Extensions.Hosting.Abstractions`,
   so `BackgroundService`/`IHostedService`/`AddHostedService` cannot compile in Core without a new
   `PackageReference`.
2. CLAUDE.md states the exact precedent for this situation: *"If you add a service that genuinely
   needs a hosting-layer dependency, this is the pattern: define the interface in Core, implement
   and register it in Web."* → **Recommendation: the reconcile-every-user logic goes in Core behind
   a new interface; the thin `BackgroundService` timer shell lives in `JNotes.Web` and is registered
   in `Program.cs`.** No new package reference anywhere. (Alternative A, if Core should own the
   timer instead: add `Microsoft.Extensions.Hosting.Abstractions` 10.0.12 to `JNotes.Core.csproj`
   and `AddHostedService` inside `AddJNotesCore` — documented under Risks/Q1.)
3. The reconciler object graph is safe to resolve in a background (non-HTTP) scope:
   `NoteIndexReconciler` → `FileSystemNoteFileStore(ISettingsService, INoteMarkdownSerializer)` →
   `SettingsService(IDbContextFactory, IOptions<JNotesOptions>, IOptions<OidcOptions>)`, plus
   singleton `RedisNoteIndexRepository(IConnectionMultiplexer)`. **Nothing in it resolves
   `ICurrentUserContext`**, so the "no HttpContext outside a request" gotcha doesn't apply — same
   reason `RunStartupReconciliationAsync` already works today.
4. Hosted services start at `app.Run()`, which is *after* `MigrateJNotesDatabaseAsync()` /
   `InitializeSetupStateAsync()` / `RunStartupReconciliationAsync()` in `Program.cs`. No migration
   race; the first timer tick should wait a full interval (startup already did pass #1).
5. **`PeriodicTimer`'s period ceiling is ~49.7 days** (`uint.MaxValue - 1` ms) — an absurd
   `AUTO_INDEX_INTERVAL_MINUTES` value would throw `ArgumentOutOfRangeException` in the constructor.
   The parse helper must clamp.
6. `JNotes.Web/BannedSymbols.txt` bans `File`/`Directory`/`DbContext`/`SqliteConnection`/
   `IConnectionMultiplexer`/`IDatabase`. The proposed Web-side service touches none of them
   (`IServiceScopeFactory`, `IOptions<JNotesOptions>`, `ILogger`, `PeriodicTimer`, and Core
   interfaces only).

## Config decision

| Item | Decision |
| --- | --- |
| Env var / config key | `AUTO_INDEX_INTERVAL_MINUTES` (flat, same style as `NOTES_PATH`/`DB_PATH`/`TZ`/`REDIS_CONNECTION_STRING`) |
| Default when unset/blank | `5` minutes |
| `0` or a negative value | **Disables** auto-indexing (service logs once and returns; startup + "Rescan Now" still work). Explicit 0 = off is more useful than "0 means default" |
| Unparseable value (`abc`, `5.5`) | Fall back to the `5`-minute default rather than failing startup — a bad interval isn't fatal the way a missing `REDIS_CONNECTION_STRING` is. The service logs the *effective* interval at Information on start, so a typo is visible in the logs |
| Clamp | Enabled values clamped to `[1, 10080]` minutes (1 min .. 7 days) — the upper bound keeps `PeriodicTimer` legal, the lower bound stops a pathological sub-minute rescan loop |
| Where the value lives | `JNotesOptions.AutoIndexIntervalMinutes` (a single scalar, like `TimeZoneId` — not worth a new options class the way the 4-value cohesive `OidcOptions` was). Parsed in `AddJNotesCore` alongside the other flat keys |

---

## Ordered implementation steps

### 1. `src/JNotes.Core/Indexing/AutoIndexInterval.cs` (new) — pure static parse/clamp helper

Same "plain static class, no DI, unit-testable" shape as `UserValidation`/`NoteGrouping`/
`UserInitials`. Exists so the config semantics are testable without a host, a timer, or Redis.

```csharp
namespace JNotes.Core.Indexing;

public static class AutoIndexInterval
{
    public const int DefaultMinutes = 5;
    public const int MinMinutes = 1;
    public const int MaxMinutes = 10_080; // 7 days; PeriodicTimer's own period ceiling is ~49.7 days.
    public const int DisabledMinutes = 0;

    /// <summary>null/blank/unparseable -> DefaultMinutes; &lt;= 0 -> DisabledMinutes; otherwise clamped to [MinMinutes, MaxMinutes].</summary>
    public static int ParseMinutes(string? rawValue);

    public static bool IsEnabled(int minutes) => minutes > 0;
}
```

Implementation notes: `rawValue?.Trim()`, `int.TryParse(..., NumberStyles.Integer,
CultureInfo.InvariantCulture, ...)`; failure → `DefaultMinutes`; `<= 0` → `DisabledMinutes`; else
`Math.Clamp`.

### 2. `src/JNotes.Core/JNotesOptions.cs` (modify) — add the option

```csharp
/// <summary>
/// How often the background auto-indexing pass reconciles every user's index against their note
/// files, in minutes. 0 disables it entirely (startup reconciliation and Settings -> "Rescan Now"
/// are unaffected). Populated from AUTO_INDEX_INTERVAL_MINUTES — see AutoIndexInterval.ParseMinutes.
/// </summary>
public int AutoIndexIntervalMinutes { get; set; } = Indexing.AutoIndexInterval.DefaultMinutes;
```

### 3. `src/JNotes.Core/Indexing/IAllUsersIndexReconciler.cs` + `AllUsersIndexReconciler.cs` (new)

The shared "reconcile every user" loop, extracted so the startup pass and the periodic pass share
one implementation (no duplicated loop), and so failure-isolation is directly testable without a
timer.

```csharp
namespace JNotes.Core.Indexing;

public interface IAllUsersIndexReconciler
{
    Task<AllUsersReconciliationResult> ReconcileAllAsync(CancellationToken cancellationToken = default);
}

public sealed record AllUsersReconciliationResult(
    int UsersReconciled,
    int UsersFailed,
    int Added,
    int Updated,
    int Removed,
    IReadOnlyList<string> Errors);
```

`AllUsersIndexReconciler` (sealed, **scoped**) ctor:
`(IUserRepository userRepository, INoteIndexReconciler reconciler, ILogger<AllUsersIndexReconciler> logger)`.

Body:
1. `var users = await _userRepository.GetAllUsersAsync(cancellationToken);` (empty pre-`/setup` →
   returns a zeroed result, no-op).
2. `foreach (var user in users)`:
   - `cancellationToken.ThrowIfCancellationRequested();` (`NoteIndexReconciler.RescanAsync` threads
     the token to the store/Redis but has no check of its own, so this is what actually makes
     shutdown snappy between users).
   - `try { var result = await _reconciler.RescanAsync(user.Id, cancellationToken); }` → accumulate
     `Added/Updated/Removed`, append each per-note error prefixed with the user id,
     `UsersReconciled++`.
   - `catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }`
     — shutdown must propagate, not be counted as a per-user failure.
   - `catch (Exception ex) { _logger.LogWarning(ex, "Index reconciliation failed for user {UserId}; continuing with the remaining users.", user.Id); UsersFailed++; errors.Add($"{user.Id}: {ex.Message}"); }`
     — **one user's Redis/IO failure must not abort the rest**.
3. Return the aggregate.

Per-user warning logging lives here (not in the background service) so the startup path gets it
too.

### 4. `src/JNotes.Core/ServiceCollectionExtensions.cs` (modify)

- Inside the existing `services.Configure<JNotesOptions>(...)` delegate, after
  `RedisConnectionString`:
  ```csharp
  // Parsed (not just "?? default") because the raw string needs validating/clamping — see AutoIndexInterval.
  options.AutoIndexIntervalMinutes = AutoIndexInterval.ParseMinutes(configuration["AUTO_INDEX_INTERVAL_MINUTES"]);
  ```
  (Add `using JNotes.Core.Indexing;` — already imported.) Note this line always assigns, unlike the
  `?? options.X` pattern, because the helper owns the default. Test/fixture overrides still win:
  `CoreServicesFixture` calls `services.Configure<JNotesOptions>` *after* `AddJNotesCore`, and later
  `Configure` delegates run last.
- Register the new service next to the existing reconciler line:
  ```csharp
  services.AddScoped<IAllUsersIndexReconciler, AllUsersIndexReconciler>();
  ```
  (Scoped, matching `INoteIndexReconciler`/`IUserRepository`; the singleton hosted service must
  never capture it — it creates a scope per tick.)
- Do **not** register a hosted service here (see step 6 rationale / Risks/Q1).

### 5. `src/JNotes.Core/HostExtensions.cs` (modify) — `RunStartupReconciliationAsync` delegates to the new service

Replace the inline loop with:

```csharp
using var scope = services.CreateScope();
var reconciler = scope.ServiceProvider.GetRequiredService<IAllUsersIndexReconciler>();
await reconciler.ReconcileAllAsync();
```

The `IUserRepository` resolution moves into `AllUsersIndexReconciler`. **Intentional behavior
change to call out in the doc comment and commit message:** one user's reconciliation failure no
longer aborts startup reconciliation for everyone (previously an exception propagated out of
`Program.cs` and failed startup). Update the existing summary comment to say failures are now
logged per-user and startup continues.

### 6. `src/JNotes.Web/Indexing/AutoIndexBackgroundService.cs` (new folder + file) — the timer shell

Lives in Web (not Core) because `BackgroundService` is a hosting-layer type Core doesn't reference
— the `ICurrentUserContext` precedent from CLAUDE.md. New `Indexing/` folder alongside the existing
`Auth/` folder (Web already has non-`Components/` folders).

```csharp
namespace JNotes.Web.Indexing;

/// <summary>
/// Periodically reconciles every user's Redis index against their note files, catching up
/// ContentOnly autosaves and hand-edited files without waiting for a restart or a manual
/// "Rescan Now". Interval comes from AUTO_INDEX_INTERVAL_MINUTES (0 disables). Lives in
/// JNotes.Web rather than Core because BackgroundService is a hosting-layer type Core has never
/// depended on — the same split as ICurrentUserContext; all the actual work is Core's
/// IAllUsersIndexReconciler.
/// </summary>
public sealed class AutoIndexBackgroundService : BackgroundService
{
    public AutoIndexBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptions<JNotesOptions> options,
        ILogger<AutoIndexBackgroundService> logger);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken);
}
```

`ExecuteAsync` shape (order matters):

1. `var minutes = _options.AutoIndexIntervalMinutes;`
   `if (!AutoIndexInterval.IsEnabled(minutes)) { _logger.LogInformation("Automatic index reconciliation is disabled (AUTO_INDEX_INTERVAL_MINUTES=0)."); return; }`
2. `_logger.LogInformation("Automatic index reconciliation enabled: every {IntervalMinutes} minute(s).", minutes);`
3. `using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));`
4. `try { while (await timer.WaitForNextTickAsync(stoppingToken)) { ...tick... } } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { /* normal shutdown */ }`
   - Waiting **before** the first tick is deliberate: `RunStartupReconciliationAsync` just ran, so
     an immediate tick would be redundant work during startup.
   - `WaitForNextTickAsync` returning false / throwing OCE is the only exit — no `Task.Delay` loop,
     no `stoppingToken.IsCancellationRequested` polling.
5. Tick body, in its own `try`:
   ```csharp
   await using var scope = _scopeFactory.CreateAsyncScope();
   var reconciler = scope.ServiceProvider.GetRequiredService<IAllUsersIndexReconciler>();
   var result = await reconciler.ReconcileAllAsync(stoppingToken);
   ```
   - `catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }`
   - `catch (Exception ex) { _logger.LogError(ex, "Automatic index reconciliation pass failed; will retry on the next interval."); }`
     — **never rethrow**: the default `BackgroundServiceExceptionBehavior.StopHost` would otherwise
     take the whole app down because the index (a derived cache) hiccuped.
6. Tick logging policy — avoid a log line every 5 minutes forever:
   - `Information` when `Added + Updated + Removed > 0` **or** `UsersFailed > 0`:
     `"Auto-index: {Added} added, {Updated} updated, {Removed} removed across {UsersReconciled} user(s); {UsersFailed} failed."`
   - `Debug` otherwise ("no changes").
7. Graceful stop: the `stoppingToken` is threaded into `ReconcileAllAsync`, whose per-user
   `ThrowIfCancellationRequested` bounds worst-case shutdown to one user's in-flight rescan —
   comfortably inside the host's default 30s shutdown timeout. No `StopAsync` override needed;
   `BackgroundService`'s own is sufficient.

### 7. `src/JNotes.Web/Program.cs` (modify)

After `builder.Services.AddJNotesCore(builder.Configuration);` (grouped with it, with a short
comment pointing at `AUTO_INDEX_INTERVAL_MINUTES`):

```csharp
builder.Services.AddHostedService<AutoIndexBackgroundService>();
```

plus `using JNotes.Web.Indexing;`. Nothing else in `Program.cs` changes —
`RunStartupReconciliationAsync()` stays exactly where it is (it must still run *before*
`app.Run()`, i.e. before the hosted service's first tick).

### 8. Config files (modify)

- `src/JNotes.Web/appsettings.json` — add `"AUTO_INDEX_INTERVAL_MINUTES": 5` next to the other flat
  keys (they're all declared there for local dev).
- `Dockerfile` — add `ENV AUTO_INDEX_INTERVAL_MINUTES=5` next to the existing
  `ENV NOTES_PATH`/`DB_PATH`/`TZ` block.
- `docker-compose.yml` — add to the `jnotes` service's `environment:` list:
  `- AUTO_INDEX_INTERVAL_MINUTES=${AUTO_INDEX_INTERVAL_MINUTES:-5}` with a one-line comment ("set to
  0 to disable background index reconciliation"), matching the `TZ=${TZ:-UTC}` style.

### 9. `README.md` (modify)

- Add a row to the Configuration table (currently ends with the `OIDC_*` rows):
  `| \`AUTO_INDEX_INTERVAL_MINUTES\` | \`5\` | How often the background pass reconciles every
  user's note index against the files on disk. \`0\` disables it (the startup pass and Settings →
  "Rescan Now" still work). |`
- Add a sentence to the paragraph that currently says the index "is a rebuildable cache … the
  startup reconciliation pass rebuilds it": mention reconciliation also runs on a timer, so
  externally edited/added/removed `.md` files are picked up without a restart.

### 10. `src/JNotes.Web/Components/Pages/Settings.razor` (optional copy tweak)

Line currently reads *"The index is rebuilt automatically at startup."* — could become "…at
startup and every N minutes" by injecting `IOptions<JNotesOptions>` (not a banned symbol) and
rendering `AutoIndexIntervalMinutes`, with the sentence dropping the interval clause when it's
`0`/disabled. Low value, zero risk; include or skip.

---

## Testing checklist (`tests/JNotes.Tests/`)

**New: `tests/JNotes.Tests/Indexing/AutoIndexIntervalTests.cs` — pure unit tests, no
fixture/Redis/DB** (same tier as `NoteGroupingTests`/`UserValidationTests`). Single `[Theory]` over
`ParseMinutes`:

| Input | Expected |
| --- | --- |
| `null`, `""`, `"   "` | `5` (default) |
| `"10"`, `" 10 "` | `10` |
| `"1"` | `1` |
| `"0"` | `0` (disabled) |
| `"-5"` | `0` (normalized to disabled) |
| `"abc"`, `"5.5"`, `"5m"` | `5` (default fallback) |
| `"99999999"` | `10080` (clamped — the `PeriodicTimer` ceiling guard) |

Plus `IsEnabled(0) == false`, `IsEnabled(5) == true`.

**New: `tests/JNotes.Tests/Integration/AllUsersIndexReconcilerTests.cs` — uses
`CoreServicesFixture`** (real DI graph, real SQLite temp file, real Redis, two seeded users):

1. `ReconcileAllAsync_indexes_files_for_every_user` — write a note file for `UserAId` and one for
   `UserBId` directly through `fixture.FileStore` (bypassing the index, as the existing reconciler
   tests do), call `ReconcileAllAsync`, assert `Added == 2`, `UsersReconciled == 2`,
   `UsersFailed == 0`, and that both notes are listed via `NoteService` after
   `ActAsUserA()`/`ActAsUserB()`.
2. `ReconcileAllAsync_continues_after_one_user_fails` — the important one. Construct
   `AllUsersIndexReconciler` **by hand** (public ctor) with `fixture.UserRepository`, a small
   in-test `INoteIndexReconciler` decorator that throws for one user id and delegates to
   `fixture.Reconciler` for the rest, and `NullLogger<AllUsersIndexReconciler>.Instance`
   (`Microsoft.Extensions.Logging.Abstractions` is already transitively available to the test
   project via the Core project reference). Assert `UsersFailed == 1`, `UsersReconciled == 1`, and
   that the non-failing user's externally-added file *was* indexed. Deliberately failing the
   **first**/oldest user (`GetAllUsersAsync` is `CreatedAt`-ascending, so `user-a` comes first)
   proves the loop didn't abort. This avoids hand-stubbing the 12-method `IUserRepository`.
3. `ReconcileAllAsync_honours_a_cancelled_token` — pass an already-cancelled `CancellationToken`,
   assert `OperationCanceledException` (or `TaskCanceledException`) rather than a swallowed
   per-user failure.
4. `ReconcileAllAsync_is_a_noop_with_no_changes` — mirrors the existing
   `RescanAsync_is_a_noop_when_nothing_changed_since_the_last_index` test, asserting all-zero
   counts across both users.

**Optional fixture tweak:** add
`public IAllUsersIndexReconciler AllUsersReconciler => _scope.ServiceProvider.GetRequiredService<IAllUsersIndexReconciler>();`
to `tests/JNotes.Tests/Integration/CoreServicesFixture.cs`, matching its existing accessor style
(needed for tests 1/3/4; test 2 constructs the type directly).

**Deliberately NOT tested:** real timer ticks / `AutoIndexBackgroundService` itself. Tests only
cover Core (`JNotes.Tests` has a single project reference to `JNotes.Core`), the service is a
~40-line shell over already-covered logic, and a real-clock test would mean sleeping for minutes.
All the logic worth asserting was extracted into `AutoIndexInterval` (config) and
`AllUsersIndexReconciler` (loop + failure isolation) precisely so the untested part is trivial.

**Regression runs:** `dotnet build JNotes.slnx`, `dotnet test JNotes.slnx` (needs a local Redis on
`localhost:6379`, or `JNOTES_TEST_REDIS_CONNECTION_STRING`), `dotnet format JNotes.slnx
--verify-no-changes`. Existing `NoteIndexReconcilerTests` must stay green untouched (`RescanAsync`
itself is not modified). Manual smoke test: run with `AUTO_INDEX_INTERVAL_MINUTES=1`, edit a note
body only (ContentOnly autosave), confirm within ~1 minute that the list re-orders / search finds
the new body text without a restart; then run with `AUTO_INDEX_INTERVAL_MINUTES=0` and confirm the
"disabled" log line and no ticks.

---

## Risks and open questions

1. **Placement of the hosted service (main open decision).** Recommended: Web
   (`AutoIndexBackgroundService`) + Core (`IAllUsersIndexReconciler`), matching CLAUDE.md's stated
   "hosting-layer dependency → implement in Web" rule and requiring zero new packages.
   *Alternative A* if Core should own it end-to-end instead: add
   `<PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.0.12" />` to
   `src/JNotes.Core/JNotes.Core.csproj`, move the service to
   `src/JNotes.Core/Indexing/AutoIndexBackgroundService.cs`, and call
   `services.AddHostedService<AutoIndexBackgroundService>()` from `AddJNotesCore` (keeping the "all
   Core DI lives in one place" rule intact). Steps 1-5 and 8-10 are identical either way; only steps
   6-7 move. Minor caveat for A: `Microsoft.Extensions.Hosting.Abstractions` is part of the
   ASP.NET Core shared framework, so a `PackageReference` in a `net10.0` library is fine but pins a
   patch version that the host's shared framework will override at runtime — keep it at `10.0.12`
   to match the sibling `Microsoft.Extensions.*` refs.
2. **Concurrency with live edits.** A tick can run while a user is typing. `RescanAsync` reads the
   file and upserts; a half-written file surfaces as a tolerated `NoteParseException` (existing row
   preserved, error recorded) and converges on the next tick — no new risk, but the per-tick error
   log could get noisy on a busy instance. Mitigated by logging aggregate counts, not individual
   errors, at Information.
3. **Concurrency with "Rescan Now."** Both paths are idempotent and last-write-wins per note hash; a
   simultaneous manual rescan and auto tick is harmless. No locking proposed. If strict single-flight
   is wanted, a `SemaphoreSlim(1,1)` inside a singleton would be needed — deliberately not proposed
   (adds state for no observed problem).
4. **Cost at scale.** Every tick does one `GetAllUsersAsync` (SQLite), then per user one directory
   enumeration + one Redis read of indexed mtimes. Unchanged files are skipped without re-parsing, so
   a 5-minute default is cheap for a self-hosted instance; very large collections may want a larger
   interval — which is exactly what the env var is for. Worth a sentence in the README row.
5. **Behavior change at startup** (step 5): a single user's failure no longer fails app startup.
   This is a strict improvement (a one-user Redis/parse problem shouldn't prevent everyone from
   signing in) but it *is* a change — call it out in the commit message.
6. **CLAUDE.md.** This repo's CLAUDE.md documents every architectural decision of this kind, and
   this feature (a new background service, a new env var, the Core/Web split rationale, the
   "startup no longer aborts" change) fits its existing narrative. **Do not edit CLAUDE.md without
   asking the user first** — it's project configuration, not something to update unilaterally.
7. **Testability of the timer itself (deferred).** `PeriodicTimer` has a `TimeProvider` overload
   (.NET 8+), so the service could take a `TimeProvider` and be driven by `FakeTimeProvider` in
   tests. Not proposed: the service would have to live in Core (or a new Web test project would be
   needed) for `JNotes.Tests` to reach it, and the logic worth testing is already extracted. Open
   question only if tick-level coverage is wanted later.
8. **Naming.** `IAllUsersIndexReconciler` / `AllUsersIndexReconciler` / `AllUsersReconciliationResult`
   / `ReconcileAllAsync` were chosen to sit beside the existing `INoteIndexReconciler` /
   `ReconciliationResult` / `RescanAsync` without shadowing them. Alternatives: `INoteIndexMaintenanceService`,
   or `RescanAllAsync` for symmetry with `RescanAsync`.

## File-by-file summary

| Action | Path |
| --- | --- |
| New | `src/JNotes.Core/Indexing/AutoIndexInterval.cs` |
| New | `src/JNotes.Core/Indexing/IAllUsersIndexReconciler.cs` |
| New | `src/JNotes.Core/Indexing/AllUsersIndexReconciler.cs` |
| Modify | `src/JNotes.Core/JNotesOptions.cs` |
| Modify | `src/JNotes.Core/ServiceCollectionExtensions.cs` |
| Modify | `src/JNotes.Core/HostExtensions.cs` |
| New | `src/JNotes.Web/Indexing/AutoIndexBackgroundService.cs` |
| Modify | `src/JNotes.Web/Program.cs` |
| Modify | `src/JNotes.Web/appsettings.json` |
| Modify (optional) | `src/JNotes.Web/Components/Pages/Settings.razor` |
| Modify | `Dockerfile` |
| Modify | `docker-compose.yml` |
| Modify | `README.md` |
| New | `tests/JNotes.Tests/Indexing/AutoIndexIntervalTests.cs` |
| New | `tests/JNotes.Tests/Integration/AllUsersIndexReconcilerTests.cs` |
| Modify (optional) | `tests/JNotes.Tests/Integration/CoreServicesFixture.cs` |
