# Plan: Environment variables as the only configuration source (drop appsettings*.json, move local-dev values into launchSettings.json)

## Goal
JNotes will read every setting from environment variables. `appsettings.json` and `appsettings.Development.json` are deleted, and the host is changed so that JSON files and command-line args are never configuration sources, even if a stray file turns up. Local development (Visual Studio F5, `dotnet run`, `dotnet watch`) gets its values from `src/JNotes.Web/Properties/launchSettings.json`. All documented defaults stay in code, so existing Docker and compose deployments behave exactly as they do today.

## Context found
- **Where settings are read (all flat keys, no `__` nesting except Logging):**
  - `src/JNotes.Core/ServiceCollectionExtensions.cs` `AddJNotesCore(IServiceCollection, IConfiguration)`, lines 42–120:
    - `NOTES_PATH`, `TZ`, `POSTGRES_HOST/PORT/DB/USER/PASSWORD`, `AUTO_INDEX_INTERVAL_MINUTES` go into `JNotesOptions`.
    - `OIDC_*` goes into `OidcOptions`.
    - `GOTENBERG_URL` goes into `PdfExportOptions`.
    - `OLLAMA_URL/MODEL/TIMEOUT_SECONDS` and `AUTO_TAG_INTERVAL_MINUTES/BATCH_SIZE` go into `AutoTagOptions`.
    - The connection string is built only when `POSTGRES_HOST` is non-blank. The `AddDbContextFactory` delegate (lines 127–141) throws `InvalidOperationException` when `PostgresConnectionString` is blank. That throw fires first at `HostExtensions.MigrateJNotesDatabaseAsync`.
    - The comment at lines 38–41 says "appsettings.json declares them the same way for local dev".
  - `src/JNotes.Web/Program.cs`:
    - line 25 `WebApplication.CreateBuilder(args)`
    - lines 51–53 read `NOTES_PATH` (fallback `"notes"`) and `DATA_PROTECTION_KEYS_PATH` (fallback: a `keys` folder beside `NOTES_PATH`)
    - lines 77–79 read `OIDC_AUTHORITY/CLIENT_ID/CLIENT_SECRET`
    - line 207 reads the deprecated `AUTO_TAG_ENABLED`
    - line 121 `app.Environment.IsDevelopment()`
  - Code defaults:
    - `src/JNotes.Core/JNotesOptions.cs`: `NotesFolderPath="notes"`, `TimeZoneId="UTC"`, `AutoIndexIntervalMinutes=AutoIndexInterval.DefaultMinutes` (=5, `src/JNotes.Core/Indexing/AutoIndexInterval.cs:13`). The Postgres parts have no defaults, and port 5432 is applied at build time. The doc comment at line 5 mentions `appsettings.json`.
    - `src/JNotes.Core/Users/OidcOptions.cs`: DisplayName `"OIDC"`.
    - `src/JNotes.Core/Tagging/AutoTagOptions.cs`: phi4 / 120 s / 10 / 10, with clamping.
    - `src/JNotes.Core/Export/PdfExportOptions.cs`: no default.
  - `src/JNotes.Core/HostExtensions.cs` reads no configuration itself.
  - `src/JNotes.Core/Data/JNotesDbContextFactory.cs` (the `IDesignTimeDbContextFactory` used by `dotnet ef`) already reads `POSTGRES_*` straight from `Environment.GetEnvironmentVariable`, with defaults localhost/5432/jnotes/jnotes/jnotes. It never used appsettings and needs **no change**.
- **Contents of `src/JNotes.Web/appsettings.json` today:**
  - `Logging:LogLevel:Default=Information`, `Logging:LogLevel:Microsoft.AspNetCore=Warning`
  - `AllowedHosts="*"`
  - `NOTES_PATH="App_Data/notes"`
  - `POSTGRES_HOST=localhost`, `POSTGRES_PORT=5432`, `POSTGRES_DB=jnotes`, `POSTGRES_USER=jnotes`, `POSTGRES_PASSWORD=jnotes`
  - `DATA_PROTECTION_KEYS_PATH="App_Data/keys"`
  - `TZ=UTC`
  - `AUTO_INDEX_INTERVAL_MINUTES=5`
  - **`GOTENBERG_URL="http://localhost:3000"`** (so PDF export is currently on in local dev, pointing at `jnotes-debug-pdf`)
  - `OLLAMA_URL=""`, `OLLAMA_MODEL=phi4`, `OLLAMA_TIMEOUT_SECONDS=120`, `AUTO_TAG_INTERVAL_MINUTES=10`, `AUTO_TAG_BATCH_SIZE=10`
- `appsettings.Development.json` holds only the same two Logging levels. There is no `DetailedErrors`, Kestrel section or connection strings.
- **The Web csproj has no `UserSecretsId`**, so no user-secrets source exists today. It has no explicit Content/CopyToOutputDirectory items for appsettings (they are SDK-default Content). Stale copies exist under `src/JNotes.Web/bin/Debug/net10.0/`.
- **`launchSettings.json`** has one `http` profile (`http://localhost:5230`) with env `ASPNETCORE_ENVIRONMENT=Development` and `TZ=Australia/Melbourne`. `.gitignore` does not exclude it (only `*.user`, `*.config`, `.env`, etc.), so it is committed. `.gitignore` line 10's comment references `appsettings.json`.
- **Docker:**
  - `Dockerfile` sets `ASPNETCORE_URLS=http://+:8080`, `NOTES_PATH=/data/notes`, `DATA_PROTECTION_KEYS_PATH=/data/keys`, `TZ=UTC`, `AUTO_INDEX_INTERVAL_MINUTES=5`. It has no appsettings COPY lines (`COPY src/ src/` picks up whatever exists).
  - `.dockerignore` has no appsettings entry.
  - `docker-compose.yml` already passes every app variable explicitly.
  - `docker-compose.debug.yml` holds only infrastructure containers.
  - There is no `.env.example`.
- **Tests are already hermetic:** `CoreServicesFixture.cs:116`, `Users/EfUserRepositoryTests.cs:22` and `Users/UserManagementServiceTests.cs:28` call `AddJNotesCore(new ConfigurationBuilder().Build())`, which reads no files and no environment variables, then set `PostgresConnectionString` directly. `PdfExportOptionsTests` and `AutoTagOptionsTests` use `AddInMemoryCollection`. `TestPostgresDatabase` reads only `JNOTES_TEST_POSTGRES_CONNECTION_STRING`. No test depends on appsettings.json.
- **Doc statements that go stale:**
  - `README.md` lines 71–73 and 82–83, the configuration table at 101–125 (including a POSTGRES_DB/USER/PASSWORD "default `jnotes`" that is really only compose's default), and line 232.
  - `CLAUDE.md` line 540 ("`appsettings.json` ships `OLLAMA_URL` empty so dev and tests never auto-enable").
- **Project rules that apply:**
  - Web never touches `System.IO.File`/`Directory` (BannedSymbols). The new helper uses none.
  - CLAUDE.md says the `jnotes` database on `jnotes-debug-db` is the user's real dev database, that manual runs must use a throwaway DB and folder, and that auto-tagging must never be enabled against real notes.
  - Never stop or recreate the debug containers.

## Decisions
1. **Strip the file and command-line sources in code, and also delete the files** (deleting alone is not enough).
   - `CreateBuilder` silently loads any `appsettings*.json` it finds, including stale ones in `bin/`, a file someone re-adds, or a future user-secrets id. Removing the sources makes "environment only" a guarantee rather than a convention.
   - **Don't use `Sources.Clear()`.** `CreateBuilder` also registers the `DOTNET_`- and `ASPNETCORE_`-prefixed environment providers (these supply `urls`/`http_ports`, so `ASPNETCORE_URLS`, the base image's `ASPNETCORE_HTTP_PORTS` and the launch profile's `applicationUrl` all work) plus an in-memory host-defaults source. Clearing and re-adding a bare `AddEnvironmentVariables()` would lose the prefix stripping and break Kestrel binding in Docker.
   - Instead, **remove only the sources whose type is `FileConfigurationSource`** (this covers `JsonConfigurationSource` for appsettings, appsettings.{env} and user secrets) and `CommandLineConfigurationSource`, leaving the three environment-variable providers in their default order.
   - Also call `WebApplication.CreateBuilder()` without `args`, so command-line args never reach host or app configuration.
2. **Flat keys stay flat.** The unprefixed `AddEnvironmentVariables()` provider already exposes `POSTGRES_HOST` under that exact key. Only framework sections use the standard `__` form: `Logging__LogLevel__<Category>`, `Kestrel__...`, and `AllowedHosts` (flat).
3. **Logging defaults move into code as the lowest-priority source**, not into `builder.Logging.AddFilter`. Code filters are added after the configuration rules and would beat an env override for the same category. Instead, insert a `MemoryConfigurationSource` at index 0 with `Logging:LogLevel:Default=Information` and `Logging:LogLevel:Microsoft.AspNetCore=Warning`, so `Logging__LogLevel__Microsoft.AspNetCore=Information` still overrides it.
4. **`AllowedHosts` gets no replacement.** ASP.NET Core's host-filtering post-configure already defaults to `*` when the key is absent, so dropping it changes nothing. It is documented as an optional env var.
5. **Required vs optional Postgres settings:**
   - Required: `POSTGRES_HOST`, `POSTGRES_DB`, `POSTGRES_USER`. Today DB and USER silently become null outside compose, and Npgsql then guesses from the OS user.
   - `POSTGRES_PASSWORD` stays optional (trust/pgpass auth are legitimate).
   - `POSTGRES_PORT` keeps its 5432 default.
   - The fail-fast message names exactly which ones are missing and points to the launch profile and compose. No working setup is broken, because compose and the launch profile set all of them.
6. **Whitespace-only counts as unset** for `NOTES_PATH`, `TZ`, `POSTGRES_HOST/DB/USER`, `OIDC_DISPLAY_NAME` and `DATA_PROTECTION_KEYS_PATH`. Env-only configuration makes `KEY=${KEY:-}` passthroughs common, and an empty `NOTES_PATH` or keys path would otherwise crash or misbehave. `GOTENBERG_URL`/`OLLAMA_URL` are unchanged: they already treat blank as off, and their warnings key on the raw value.
7. **Launch profile:**
   - It reproduces today's local behaviour exactly. That means the `jnotes` DB and `App_Data/notes`: they are the user's real dev data, and switching would make it "disappear".
   - `GOTENBERG_URL=http://localhost:3000` is included, because appsettings has it today and it targets `jnotes-debug-pdf`.
   - `OLLAMA_URL` is present and **explicitly empty**, for discoverability and to shadow any user-level variable. JSON has no comments, so the README and CLAUDE.md explain it.
   - The existing `TZ=Australia/Melbourne` is kept.
   - Two profiles, `http` (first, so `dotnet run`/`dotnet watch` keep using it) and `https`, with identical env blocks. No IIS Express profile: there is none today and nothing needs it.
   - `OIDC_*` is deliberately absent, so secrets set in the user's own environment flow through and never get committed.
   - The debug password `jnotes/jnotes` is already public in CLAUDE.md and compose, so committing it is acceptable.
8. **Unchanged:** Dockerfile, compose files and `JNotesDbContextFactory`. They are already environment-only and consistent with the code defaults.

## Steps

### Phase 1: Core (builds on its own)
1. **`src/JNotes.Core/JNotesOptions.cs`**
   - Add `public IReadOnlyList<string> GetMissingRequiredPostgresSettings()`. It returns, in this order, the env var names `"POSTGRES_HOST"`, `"POSTGRES_DB"`, `"POSTGRES_USER"` for each of `PostgresHost`/`PostgresDatabase`/`PostgresUsername` that `IsNullOrWhiteSpace`.
   - Rewrite the class summary (line 5) to "Populated from environment variables (flat names, e.g. NOTES_PATH/POSTGRES_HOST) — the only configuration source; see AddJNotesCore and JNotes.Web's EnvironmentOnlyConfiguration."
   - Mark `PostgresDatabase`/`PostgresUsername` as required in their summaries.
2. **`src/JNotes.Core/ServiceCollectionExtensions.cs`**
   - Add `private static string? ReadNonBlank(IConfiguration configuration, string key) => string.IsNullOrWhiteSpace(configuration[key]) ? null : configuration[key];` (no trimming of the value).
   - Use it for `NOTES_PATH`, `TZ`, `POSTGRES_HOST`, `POSTGRES_DB`, `POSTGRES_USER`, and `OIDC_DISPLAY_NAME`. Keep `?? options.X`.
   - Leave `POSTGRES_PASSWORD`, `OIDC_AUTHORITY/CLIENT_ID/CLIENT_SECRET`, `GOTENBERG_URL` and `OLLAMA_*` reading raw, as today.
   - Build `PostgresConnectionString` only when `GetMissingRequiredPostgresSettings().Count == 0`, instead of only checking the host.
   - In the `AddDbContextFactory` delegate, keep the `IsNullOrWhiteSpace(PostgresConnectionString)` guard, since tests set the string directly and must bypass it. Change the message to use the missing list, e.g. `$"Missing required environment variable(s): {string.Join(", ", missing)}. JNotes keeps user accounts, settings and the note index in PostgreSQL. Local dev: run with a launch profile from src/JNotes.Web/Properties/launchSettings.json; Docker: see docker-compose.yml."` If the list is empty but the string is blank (only when a caller cleared it), fall back to the current wording.
   - Replace the comment at lines 38–41 with: environment variables are the only source; Program.cs strips JSON files and command-line args; flat names need no `__`.

### Phase 2: Web
3. **New `src/JNotes.Web/Hosting/EnvironmentOnlyConfiguration.cs`** (namespace `JNotes.Web.Hosting`; avoid a `Configuration` folder name, which would shadow `Microsoft.Extensions.Configuration` inside `JNotes.Web`).
   - It is an `internal static class` with `public static WebApplicationBuilder UseEnvironmentVariablesOnly(this WebApplicationBuilder builder)`. The method:
     - (a) snapshots `builder.Configuration.Sources.Where(s => s is FileConfigurationSource or CommandLineConfigurationSource).ToList()` and `Remove`s each one;
     - (b) `builder.Configuration.Sources.Insert(0, new MemoryConfigurationSource { InitialData = new Dictionary<string, string?> { ["Logging:LogLevel:Default"] = "Information", ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning" } })`;
     - (c) returns the builder.
   - Usings: `Microsoft.Extensions.Configuration`, `Microsoft.Extensions.Configuration.CommandLine`, `Microsoft.Extensions.Configuration.Memory`. `FileConfigurationSource` lives in namespace `Microsoft.Extensions.Configuration`. All are in the ASP.NET Core shared framework; no package needed.
   - Put the rationale from Decisions 1 and 3 in the doc comment, in particular why this is not `Sources.Clear()`.
4. **`src/JNotes.Web/Program.cs`**
   - Line 25 becomes `var builder = WebApplication.CreateBuilder();` followed immediately by `builder.UseEnvironmentVariablesOnly();`. This must run before line 28 and before lines 51–52 read configuration. Add `using JNotes.Web.Hosting;`.
   - Lines 51–53: treat blank as unset. `var notesPath = string.IsNullOrWhiteSpace(builder.Configuration["NOTES_PATH"]) ? "notes" : builder.Configuration["NOTES_PATH"]!;`, and similarly for `DATA_PROTECTION_KEYS_PATH`, falling back to the existing derived path.
   - Update the comment at line 49 ("Read directly off IConfiguration" still holds).
5. **Delete** `src/JNotes.Web/appsettings.json` and `src/JNotes.Web/appsettings.Development.json`. No csproj edit is needed, since they are SDK-default Content.
6. **Replace `src/JNotes.Web/Properties/launchSettings.json`** with the following. It is load-bearing, so write it exactly; `https` reuses `http`'s env block verbatim (write it out in full, not with the placeholder shown here).
   ```json
   {
     "$schema": "https://json.schemastore.org/launchsettings.json",
     "profiles": {
       "http": {
         "commandName": "Project",
         "dotnetRunMessages": true,
         "launchBrowser": true,
         "applicationUrl": "http://localhost:5230",
         "environmentVariables": {
           "ASPNETCORE_ENVIRONMENT": "Development",
           "TZ": "Australia/Melbourne",
           "NOTES_PATH": "App_Data/notes",
           "DATA_PROTECTION_KEYS_PATH": "App_Data/keys",
           "POSTGRES_HOST": "localhost",
           "POSTGRES_PORT": "5432",
           "POSTGRES_DB": "jnotes",
           "POSTGRES_USER": "jnotes",
           "POSTGRES_PASSWORD": "jnotes",
           "AUTO_INDEX_INTERVAL_MINUTES": "5",
           "GOTENBERG_URL": "http://localhost:3000",
           "OLLAMA_URL": ""
         }
       },
       "https": {
         "commandName": "Project",
         "dotnetRunMessages": true,
         "launchBrowser": true,
         "applicationUrl": "https://localhost:7230;http://localhost:5230",
         "environmentVariables": { "...": "identical to http" }
       }
     }
   }
   ```
   All values must be JSON strings. The `https` profile needs `dotnet dev-certs https --trust` once.
7. **`.gitignore`** line 10: change "see src/JNotes.Web/appsettings.json" to "see NOTES_PATH in src/JNotes.Web/Properties/launchSettings.json".

### Phase 3: Docs (no code impact)
8. **`README.md`**
   - Lines 71–73: say the launch profile sets `NOTES_PATH=App_Data/notes`, and that all configuration is environment variables only (no appsettings.json, no command-line args).
   - Lines 82–83: "the launch profiles in `src/JNotes.Web/Properties/launchSettings.json` already point `POSTGRES_*` at that container".
   - Replace the table at lines 101–125 with the catalogue below: rename the header to "Environment variable", add a "Required" column, and add the ASP.NET Core rows.
   - Add one sentence: "Running without the launch profile (`dotnet run --no-launch-profile`, a published build) requires setting these yourself; a missing required one stops startup with a message naming it."
   - Line 197: "set `GOTENBERG_URL` (the launch profiles already use `http://localhost:3000`)".
   - Line 232: replace "`appsettings.json` ships it empty" with "the launch profiles set it empty; set it only against a throwaway database and notes folder".
9. **`CLAUDE.md`**
   - Line 540: replace the appsettings sentence with "The launch profiles set `OLLAMA_URL` empty and tests pass an empty `ConfigurationBuilder`, so dev and tests never auto-enable."
   - Add a short **"Configuration is environment variables only"** paragraph under Architecture. It should cover:
     - `EnvironmentOnlyConfiguration` strips JSON and command-line sources (why not `Clear()`);
     - the Logging defaults live there as the lowest-priority source;
     - local values live in `launchSettings.json` (committed, no secrets; OIDC comes from the user's own environment);
     - a re-added appsettings.json is ignored;
     - the design-time factory reads env vars directly;
     - the `http` and `https` profile env blocks are duplicated and must be edited together.
   - In Commands, note that `dotnet run`/`dotnet watch` use the first launch profile (`http`) and therefore the real `jnotes` DB.
10. **Dockerfile / compose:** no change. The executor only confirms the table matches their values (`NOTES_PATH=/data/notes`, `TZ=UTC`, and the rest).

## Environment variable catalogue

| Name | Required | Default in code | Launch profile | Docker/compose | Purpose / used by |
|---|---|---|---|---|---|
| `POSTGRES_HOST` | **Yes** | none (startup fails) | `localhost` | `postgres` | DB host. `JNotesOptions`, `JNotesDbContextFactory` (design-time default `localhost`) |
| `POSTGRES_DB` | **Yes** | none (startup fails) | `jnotes` | `${POSTGRES_DB:-jnotes}` | Database name |
| `POSTGRES_USER` | **Yes** | none (startup fails) | `jnotes` | `${POSTGRES_USER:-jnotes}` | DB user |
| `POSTGRES_PASSWORD` | No (needed for password auth) | none | `jnotes` | `${POSTGRES_PASSWORD:-jnotes}` | DB password; never logged |
| `POSTGRES_PORT` | No | `5432` | `5432` | `5432` | DB port |
| `NOTES_PATH` | No | `notes` (relative to working dir) | `App_Data/notes` | `/data/notes` | Notes root, until overridden by the DB `NotesFolderPath` setting |
| `DATA_PROTECTION_KEYS_PATH` | No | `keys` beside `NOTES_PATH` | `App_Data/keys` | `/data/keys` | Cookie-signing keys (`Program.cs`) |
| `TZ` | No | `UTC` | `Australia/Melbourne` | `${TZ:-UTC}` | Display time zone (`AppTimeZoneProvider`) |
| `AUTO_INDEX_INTERVAL_MINUTES` | No | `5` (0 = off) | `5` | `5` | `AutoIndexBackgroundService` |
| `OIDC_AUTHORITY` / `OIDC_CLIENT_ID` / `OIDC_CLIENT_SECRET` | No (all three for OIDC) | unset | absent (inherit from the user's environment) | commented out | `Program.cs`, `OidcOptions` |
| `OIDC_DISPLAY_NAME` | No | `OIDC` | absent | commented out | Login button label |
| `GOTENBERG_URL` | No | unset = PDF off | `http://localhost:3000` | `${GOTENBERG_URL:-}` | `PdfExportOptions` |
| `OLLAMA_URL` | No | unset = auto-tag off | `""` (off) | `${OLLAMA_URL:-}` | `AutoTagOptions`. Never set against real notes |
| `OLLAMA_MODEL` | No | `phi4` | absent | `${…:-phi4}` | `AutoTagOptions` |
| `OLLAMA_TIMEOUT_SECONDS` | No | `120` (10–600) | absent | `${…:-120}` | `AutoTagOptions` |
| `AUTO_TAG_INTERVAL_MINUTES` | No | `10` (1–1440) | absent | `${…:-10}` | `AutoTagOptions` |
| `AUTO_TAG_BATCH_SIZE` | No | `10` (1–100) | absent | `${…:-10}` | `AutoTagOptions` |
| `AUTO_TAG_ENABLED` | Deprecated | ignored; warning if set | absent | absent | `Program.cs` warning |
| `ASPNETCORE_ENVIRONMENT` | No | `Production` | `Development` | unset (Production) | Host environment (dev exception page, DI validation, static web assets) |
| `ASPNETCORE_URLS` / `ASPNETCORE_HTTP_PORTS` | No | Kestrel default / base image `8080` | set from `applicationUrl` | `ASPNETCORE_URLS=http://+:8080` (Dockerfile) | Listen addresses |
| `Logging__LogLevel__Default` | No | `Information` | absent | absent | Standard logging |
| `Logging__LogLevel__Microsoft.AspNetCore` | No | `Warning` | absent | absent | Standard logging (a name with a dot is fine in env and compose) |
| `AllowedHosts` | No | `*` (framework default) | absent | absent | Host filtering |
| `JNOTES_TEST_POSTGRES_CONNECTION_STRING` | Tests only | `Host=localhost;Port=5432;Database=jnotes;Username=jnotes;Password=jnotes` | n/a | n/a | `TestPostgresDatabase` |

## Tests to add or update
- **New `tests/JNotes.Tests/Startup/JNotesOptionsTests.cs`** (namespace `JNotes.Tests.Startup`). These are pure unit tests with no DB. They mirror the `Resolve(...)` helper in `Tagging/AutoTagOptionsTests.cs:107`: in-memory config, `AddLogging()`, `AddJNotesCore`, then resolve `IOptions<JNotesOptions>`.
  - All of HOST/DB/USER/PASSWORD set, PORT unset: the connection string contains `Host=h`, `Database=d`, `Username=u`, `Port=5432`, and the missing list is empty.
  - `POSTGRES_DB` missing: `PostgresConnectionString` is null and the missing list is `["POSTGRES_DB"]`.
  - `POSTGRES_HOST = "  "`: it counts as missing.
  - No keys: `NotesFolderPath == "notes"`, `TimeZoneId == "UTC"`, `AutoIndexIntervalMinutes == 5`, missing list = HOST, DB, USER.
  - `NOTES_PATH = " "` and `TZ = ""`: the defaults are kept.
  - Missing keys, then `provider.GetRequiredService<IDbContextFactory<JNotesDbContext>>().CreateDbContext()`: throws `InvalidOperationException` whose message contains `POSTGRES_DB` and `POSTGRES_USER`. The throw is in the options delegate, before any connection.
  - A second `services.Configure<JNotesOptions>(o => o.PostgresConnectionString = "Host=x;Database=y;Username=z")` with no keys: `CreateDbContext()` does not throw. This locks in the path the fixtures rely on.
- **Optional, in `Users/`:** `OIDC_DISPLAY_NAME = "   "` resolves to `"OIDC"`.
- **No changes** to `CoreServicesFixture`, `EfUserRepositoryTests`, `UserManagementServiceTests`, `PdfExportOptionsTests` or `AutoTagOptionsTests`. They already pass an empty or in-memory `ConfigurationBuilder` and set `PostgresConnectionString` themselves.
- **No Web tests exist** (by design), so `EnvironmentOnlyConfiguration` is verified manually below.

## Manual verification (for the tester)
PowerShell from `C:\Data\Code\JNotes`. **Never** run against the `jnotes` database or `src\JNotes.Web\App_Data`, and never set `OLLAMA_URL`. Don't stop or recreate the `jnotes-debug-*` containers.

1. **Build, test, format**
   - `dotnet build JNotes.slnx`: 0 warnings introduced.
   - `dotnet test JNotes.slnx`: `jnotes-debug-db` must be running; everything passes, including the new `JNotesOptionsTests`.
   - `dotnet format JNotes.slnx --verify-no-changes`: exits 0.
2. **Launch profile parses:** `(Get-Content src\JNotes.Web\Properties\launchSettings.json -Raw | ConvertFrom-Json).profiles.PSObject.Properties.Name` gives `http, https`. Check that every value in both env blocks is a string and that the two blocks are identical.
3. **No appsettings in outputs**
   - `Get-ChildItem src\JNotes.Web -Filter appsettings*.json` returns nothing.
   - `dotnet publish src\JNotes.Web -c Release -o $env:TEMP\jnotes-envcheck\publish`, then `Get-ChildItem $env:TEMP\jnotes-envcheck\publish -Filter appsettings*.json` returns nothing.
   - (`bin\Debug` may keep stale copies until `dotnet clean`; the next check proves they are ignored.)
4. **Scratch database and folder**
   - `docker exec jnotes-debug-db createdb -U jnotes jnotes_envcheck`
   - Clear any inherited values: `Get-ChildItem Env:POSTGRES_*, Env:OLLAMA_*, Env:NOTES_PATH -ErrorAction SilentlyContinue | Remove-Item`
5. **Fail-fast:**
   - Run `dotnet run --project src\JNotes.Web --no-launch-profile` with no `POSTGRES_*` set.
   - Expected: startup aborts with `InvalidOperationException` "Missing required environment variable(s): POSTGRES_HOST, POSTGRES_DB, POSTGRES_USER. …".
   - Repeat with only `$env:POSTGRES_HOST="localhost"`. The message lists `POSTGRES_DB, POSTGRES_USER`.
6. **Happy path, environment only**
   - Set these variables:
     - `$env:ASPNETCORE_ENVIRONMENT="Development"`
     - `$env:ASPNETCORE_URLS="http://localhost:5231"`
     - `$env:POSTGRES_HOST="localhost"; $env:POSTGRES_DB="jnotes_envcheck"; $env:POSTGRES_USER="jnotes"; $env:POSTGRES_PASSWORD="jnotes"`
     - `$env:NOTES_PATH="$env:TEMP\jnotes-envcheck\notes"`
     - `$env:TZ="Australia/Melbourne"`
   - Run `dotnet run --project src\JNotes.Web --no-launch-profile`.
   - Expected:
     - The log shows "Now listening on: http://localhost:5231", so `ASPNETCORE_URLS` is honoured.
     - There are **no** `Microsoft.AspNetCore.*` Information lines (e.g. no "Request starting"), so the Warning default applies.
     - No auto-tag "enabled" line and no PDF route; `GOTENBERG_URL` is unset here.
   - In a browser, open `http://localhost:5231/`. It redirects to `/setup`. Create an admin; you land on the notes list. Create a text note and confirm the file exists under `%TEMP%\jnotes-envcheck\notes\<userId>\`. Confirm `%TEMP%\jnotes-envcheck\keys\` was created.
   - Sign out, reach `/login`, sign in again: the cookie works.
7. **A stray appsettings.json is ignored**
   - Stop the app. Create a temporary `src\JNotes.Web\appsettings.json` containing `{"POSTGRES_DB":"does_not_exist","Logging":{"LogLevel":{"Default":"Trace"}}}` and rerun step 6's command.
   - Expected: startup still uses `jnotes_envcheck` (no connect retries or failure) and the log is not at Trace.
   - **Delete the temporary file** afterwards.
8. **Environment variables override logging defaults:**
   - Set `${env:Logging__LogLevel__Microsoft.AspNetCore}="Information"` and rerun.
   - "Request starting …" lines now appear when you load a page.
   - Then `Remove-Item Env:Logging__LogLevel__Microsoft.AspNetCore`.
9. **Command-line args are no longer config:** `dotnet run --project src\JNotes.Web --no-launch-profile -- --POSTGRES_DB=does_not_exist` still connects to `jnotes_envcheck`.
10. **Design-time EF:** `dotnet ef migrations has-pending-model-changes --project src\JNotes.Core --startup-project src\JNotes.Core` reports no pending changes. This command doesn't connect to a database.
11. **Docker image (optional)**
    - `docker build -t jnotes-envcheck .`
    - `docker run --rm --entrypoint ls jnotes-envcheck /app`: no `appsettings*.json`.
    - `docker compose config`: the `jnotes` environment is unchanged from before.
    - Then `docker image rm jnotes-envcheck`. Do not `up` the debug file.
12. **Cleanup:** `docker exec jnotes-debug-db dropdb -U jnotes jnotes_envcheck`, then `Remove-Item -Recurse $env:TEMP\jnotes-envcheck`.
13. **User-only check (not the tester).** It uses the real `jnotes` DB and `App_Data`, as today.
    - In Visual Studio, pick `http`, then `https`, and press F5. `dotnet watch --project src/JNotes.Web run` should also work.
    - Existing notes and login work, the Export PDF button shows (with `jnotes-debug-pdf` up), and Settings → Administration → Auto-tagging says "Off".

## Risks and edge cases
- **Kestrel binding in Docker** depends on keeping the `ASPNETCORE_`-prefixed provider. If a future edit changes this to `Sources.Clear()`, the container falls back to Kestrel's default and stops listening on :8080 (a plain `curl` from inside the container may still look fine). Step 6's "Now listening" line and a real Docker run catch it.
- **Removing sources after `CreateBuilder`:** `ConfigurationManager.Sources.Remove` rebuilds providers, which is supported. **UNVERIFIED:** whether .NET 10's builder objects at `Build()` when a removed command-line source had carried host settings (`--environment`, `--contentRoot`). This is moot because `CreateBuilder()` is now called without args.
- **Launch-profile env vars vs. the shell:** `dotnet run` with a profile applies the profile's values. **UNVERIFIED:** whether they override an identically named variable already set in the shell. Hence the tester uses `--no-launch-profile`.
- **Windows and empty env values:** `"OLLAMA_URL": ""` on Windows may be dropped rather than set to empty (**UNVERIFIED**). If the user had a user-level `OLLAMA_URL`, it could then leak in. The mitigation is documented: never set `OLLAMA_URL` globally.
- **Running without a profile now fails fast** (`--no-launch-profile`, published build run by hand, a new VS profile), where appsettings used to fill in localhost/jnotes. This is intended, and the message says what to set.
- **Making `POSTGRES_DB`/`POSTGRES_USER` required** only affects setups that relied on Npgsql's implicit defaults. Compose, the Dockerfile docs and the profiles all set them.
- **Profile drift:** the `http` and `https` env blocks are duplicated because JSON can't share them. CLAUDE.md should note that both must be edited together.
- **`NOTES_PATH` is relative to the working directory.** `dotnet run` and VS run from the project directory, so `App_Data/notes` lands in `src\JNotes.Web\App_Data\notes` as before. A published build run from elsewhere resolves it differently (pre-existing behaviour).
- **Development-only features** (developer exception page, `ValidateOnBuild`, static web assets) depend only on `ASPNETCORE_ENVIRONMENT`, not on `appsettings.Development.json`, which held only logging levels. Nothing is lost.
- **`dotnet ef database update`** still defaults to the real `jnotes` DB via `JNotesDbContextFactory`, which is unchanged. Unrelated to this change, but worth remembering.

## Open questions (defaults chosen)
- **Add the `https` profile, or keep `http` only?** Default: add it. `http` stays first, so `dotnet run`/`dotnet watch` behave as today.
- **Keep `GOTENBERG_URL=http://localhost:3000` in the launch profiles?** Default: yes, to match today's appsettings. Without `jnotes-debug-pdf`, the button shows a 503 page, which is harmless.
- **Should `POSTGRES_PASSWORD` also be required?** Default: no, because of trust/pgpass auth.

## Out of scope
- No changes to Dockerfile, `docker-compose.yml`, `docker-compose.debug.yml`, `.dockerignore`, `JNotesDbContextFactory`, or any option's default value or clamping.
- No renaming of env vars and no new features (e.g. an `.env.example`).
- Don't edit historical `docs/plans/*.md` files, even though they mention appsettings.
- Don't start, stop, recreate or `down` the `jnotes-debug-*` containers, and don't touch the `jnotes` database or `src\JNotes.Web\App_Data`.
