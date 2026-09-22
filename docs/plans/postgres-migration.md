# Plan: migrate SQLite + Redis onto a single PostgreSQL database (JNotes)

**Status:** Ready for implementation. No code written yet.
**Scope:** `JNotes.Core` (config, EF Core model + migrations, note index repository, host startup), `JNotes.Web` (Data Protection keys, banned symbols, comments), `tests/JNotes.Tests` (fixture + two self-provisioning suites), `Dockerfile`, `docker-compose.yml`, `appsettings.json`, `launchSettings.json`, `.gitignore`, `README.md`.
**Author context:** planned against commit `c1f1d32` (`main`, clean tree).

> **Note on `CLAUDE.md`:** §9 proposes edits. Those are **held for the user's approval** — the executor must NOT edit `CLAUDE.md` unless the user explicitly confirms. `README.md` updates are in scope.

---

## 1. Goal, scope, non-goals

**Goal.** Collapse JNotes's two datastores onto one PostgreSQL database:

* EF Core relational data (user accounts, app settings, per-user settings) moves from SQLite to Postgres — genuinely persistent data that must be backed up.
* The note index moves from Redis to tables in the *same* Postgres database. It stays, conceptually and operationally, a **derived, rebuildable cache**: `NoteIndexReconciler.RescanAsync` must still fully rebuild it from the `.md` files on disk if the tables are truncated.
* Redis is removed entirely (package, DI registration, compose service, env var, docs). SQLite is removed entirely (`Microsoft.EntityFrameworkCore.Sqlite`, `DB_PATH`, the SQLite-specific workarounds).

**The markdown note files on disk are untouched and remain the source of truth.** No change to `INoteFileStore`, `NoteMarkdownSerializer`, `FrontMatterSplitter`, `ChecklistMarkdownParser`, the file layout, or the front-matter format.

**Non-goals (explicitly out of scope).**

* **No data-migration tooling.** This is a deliberate clean break: an existing install upgrading to this version loses its SQLite accounts/settings and must re-run `/setup`. No import/export script, no dual-read path, no `DB_PATH` compatibility shim. Notes on disk survive untouched — only the accounts that own them are gone, so a user re-created at `/setup` gets a *new* user id and their old `notes/<old-user-id>/` folder must be renamed by hand (README callout, §4.14).
* **No search-semantics change.** `ILIKE '%term%'`, matching today's case-insensitive `Contains` exactly. Explicitly **not** `tsvector`/`plainto_tsquery`: token-based FTS would silently stop matching partial words and start stemming. Noted as a possible future enhancement in §11, out of scope here.
* **No `pg_trgm` GIN index** in this change (§2 D5, §11 Q2). Plain btree indexes only.
* **No Testcontainers.** Local/CI Postgres is a manually-run container, mirroring exactly how Redis works today.
* No connection pooler (pgbouncer), no read replica, no schema-per-tenant, no row-level security, no change to auth, the UI, autosave scopes, the trash, images, or mermaid.

**Verified current state (read, not assumed).**

| Concern | Today | File |
| --- | --- | --- |
| Relational store | `AddDbContextFactory<JNotesDbContext>` → `options.UseSqlite($"Data Source={DbPath}")`, with a `Directory.CreateDirectory` for the parent folder | `src/JNotes.Core/ServiceCollectionExtensions.cs:59-72` |
| Note index store | `AddSingleton<IConnectionMultiplexer>` → eager `ConnectionMultiplexer.Connect`, throwing `InvalidOperationException` on a blank `REDIS_CONNECTION_STRING`; `AddSingleton<INoteIndexRepository, RedisNoteIndexRepository>` | `src/JNotes.Core/ServiceCollectionExtensions.cs:74-90, 101` |
| Config keys | Flat `NOTES_PATH` / `DB_PATH` / `TZ` / `REDIS_CONNECTION_STRING` / `AUTO_INDEX_INTERVAL_MINUTES` read off `IConfiguration` into `JNotesOptions` | `ServiceCollectionExtensions.cs:38-46`, `JNotesOptions.cs` |
| Index schema | Redis hash `jnotes:note:{noteId}` (8 fields; `Tags` is an inline JSON array) + set `jnotes:user:{userId}:notes`. All filtering/sorting client-side after loading a user's notes | `src/JNotes.Core/Indexing/RedisNoteIndexRepository.cs` |
| Index contract | `SearchAsync`, `GetDistinctTagsAsync`, `UpsertAsync(entry, tags)`, `DeleteAsync(userId, noteId)`, `GetIndexedFileWriteTimesAsync(userId)`. Explicit `userId` on every method | `src/JNotes.Core/Indexing/INoteIndexRepository.cs` |
| Search matching | `Title.Contains(text, OrdinalIgnoreCase) \|\| Body.Contains(...) \|\| Tags.Any(t => t.Contains(...))`; `query.Tag` is exact `Tags.Contains(tag)` (case-**sensitive**); order `UpdatedAt` desc; `GetDistinctTagsAsync` is `Distinct().OrderBy(StringComparer.Ordinal)` | `RedisNoteIndexRepository.cs:29-76` |
| Parse errors | `NoteIndexReconciler` catches `NoteParseException`, appends to `ReconciliationResult.Errors`, and **leaves the existing row untouched**. There is **no parse-error column** in the index today | `src/JNotes.Core/Indexing/NoteIndexReconciler.cs:76-81` |
| Startup sequence | `MigrateJNotesDatabaseAsync()` → `InitializeSetupStateAsync()` → `RunStartupReconciliationAsync()`, all before `app.Run()` | `src/JNotes.Web/Program.cs:100-102` |
| Migration/startup | `Database.MigrateAsync()` then `ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;")` | `src/JNotes.Core/HostExtensions.cs:18-32` |
| Existing migration | One migration, `20260917204544_InitialCreate`, with SQLite-provider SQL (`type: "TEXT"`, `collation: "NOCASE"`), plus its `.Designer.cs` and `JNotesDbContextModelSnapshot.cs` | `src/JNotes.Core/Data/Migrations/` |
| SQLite-isms in the model | `Property(e => e.Username).UseCollation("NOCASE")`; `HasIndex(e => e.Email).IsUnique().HasFilter("\"Email\" IS NOT NULL")` (double-quoted identifier — **already valid Postgres**) | `src/JNotes.Core/Data/JNotesDbContext.cs:25, 32-33` |
| SQLite-isms in code | `GetAllUsersAsync` materializes then orders client-side because the SQLite provider can't `ORDER BY` a `DateTimeOffset`; `GetUserByUsernameAsync` relies on the NOCASE column collation | `src/JNotes.Core/Users/EfUserRepository.cs:87-96, 104-109` |
| Data Protection keys | `{Path.GetDirectoryName(Path.GetFullPath(DB_PATH))}/keys`, read straight off `IConfiguration` before the container exists | `src/JNotes.Web/Program.cs:31-38` |
| Banned symbols | `System.IO.File`, `System.IO.Directory`, `Microsoft.EntityFrameworkCore.DbContext`, `Microsoft.Data.Sqlite.SqliteConnection`, `StackExchange.Redis.IConnectionMultiplexer`, `StackExchange.Redis.IDatabase` | `src/JNotes.Web/BannedSymbols.txt` |
| Packages | Core: `Microsoft.EntityFrameworkCore.Sqlite` 10.0.12, `StackExchange.Redis` 2.8.24, `Microsoft.EntityFrameworkCore.Design` 10.0.12. Tests: `Microsoft.EntityFrameworkCore.Sqlite` 10.0.12 (only for provider parity — nothing references a Sqlite type) | `*.csproj` |
| `dotnet ef` tool | `dotnet-ef` 10.0.12, `rollForward: false`. **Provider-agnostic — no change needed** | `.config/dotnet-tools.json` |
| Test infra | `CoreServicesFixture`: temp dir + temp-file SQLite + real Redis (`JNOTES_TEST_REDIS_CONNECTION_STRING`, default `localhost:6379`), seeds `user-a`/`user-b`, `CleanUpRedisAsync()` in teardown. `EfUserRepositoryTests` and `UserManagementServiceTests` build **their own** `ServiceCollection` (no `AddLogging()`, no Redis connection string — they never resolve `IConnectionMultiplexer`, which is why they work with no Redis running today) | `tests/JNotes.Tests/Integration/CoreServicesFixture.cs`, `tests/JNotes.Tests/Users/*.cs` |
| `DateTimeOffset` values | Every one is offset-zero: `DateTimeOffset.UtcNow`; `new DateTimeOffset(DateTime.SpecifyKind(..., Utc))` in the serializer; `new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero)` in the file store | `NoteMarkdownSerializer.cs:50-51`, `FileSystemNoteFileStore.cs:73, 169` |

**Verified external facts** (do not re-litigate):

* `Npgsql.EntityFrameworkCore.PostgreSQL` **10.0.3** (2026-07-10) is the latest stable for EF Core 10 / .NET 10; it requires `Microsoft.EntityFrameworkCore >= 10.0.4` (the repo pins 10.0.12 — compatible). 11.0.0 is rc-only.
* PostgreSQL **18** is the current stable major (18.6, 2026-08-13); 19 was still in beta as of August 2026. → `postgres:18-alpine`.
* `postgres:18`+ changed the image's `PGDATA` to `/var/lib/postgresql/18/docker` and moved the declared `VOLUME` to `/var/lib/postgresql`. **A `/var/lib/postgresql/data` mount is silently ignored on 18** — data lands in an anonymous volume and `initdb` re-runs on every container recreate, with no error or warning.
* `EF.Functions.ILike` has two overloads: `ILike(match, pattern)` → `match ILIKE pattern`, and `ILike(match, pattern, escapeCharacter)` → `... ESCAPE escapeCharacter`. (`NpgsqlDbFunctionsExtensions`, namespace `Microsoft.EntityFrameworkCore`.)
* Npgsql's model builder has `HasCollation(string name, string locale, string provider, bool? deterministic)`, emitting `CREATE COLLATION x (provider = icu, locale = '…', deterministic = false)`. **This is not** EF Core's one-argument relational `HasCollation`, which sets the *database default* collation — both live in namespace `Microsoft.EntityFrameworkCore`, so the wrong overload binds silently.
* Non-deterministic collations historically rejected `LIKE` ("nondeterministic collations are not supported for LIKE"); PostgreSQL 18 added support. JNotes never pattern-matches `Username`, so this is moot either way.

---

## 2. Design decisions

### D1 — One database, one schema, one `DbContext`

**Decision:** the note-index tables live in the **same** PostgreSQL database and the same (default `public`) schema as `Users`/`Settings`/`UserSettings`, as two new `DbSet`s on the existing `JNotesDbContext`. Named `NoteIndexEntries` and `NoteIndexTags`.

**Rationale**

* One connection string, one migration history, one `MigrateAsync()` at startup. A separate schema (or a second `DbContext`) means a second provider registration, a second `MigrationsHistoryTable`, and a second thing to get wrong in `docker-compose.yml`, for zero gain.
* "The index is a derived cache" is a **code** invariant, enforced by `NoteIndexReconciler` being able to rebuild it from the files. A schema boundary would not add a single guarantee — you can already `TRUNCATE` the two tables and get the whole index back on the next pass (that is an explicit acceptance test, §8.9).
* The backup story stays a documentation statement, not a schema one, and it stays honest: `pg_dump` the database; the index part of that dump is redundant but harmless.
* Table names are prefixed `NoteIndex*` precisely so "these two are the throwaway ones" is obvious in `psql`, and a comment on the entity says so.

### D2 — `POSTGRES_CONNECTION_STRING` replaces both `DB_PATH` and `REDIS_CONNECTION_STRING`

One flat key, same style as every other (`NOTES_PATH`, `TZ`, `OIDC_*`). `JNotesOptions.DbPath` and `JNotesOptions.RedisConnectionString` are both deleted; `JNotesOptions.PostgresConnectionString` (nullable, no default) replaces them.

**Fail-fast is preserved, and the mechanism is documented:**

1. A null/blank value throws `InvalidOperationException` from inside the `AddDbContextFactory` options delegate — so it fires the first time any `JNotesDbContext` is created, which is `MigrateJNotesDatabaseAsync()`, i.e. **before `app.Run()`**. This is exactly when the old Redis check fired (the eager `ConnectionMultiplexer.Connect` was in a lazily-resolved singleton; the first resolution was the startup reconciliation pass).
2. EF Core is lazy about *connecting*, so an unreachable-but-well-formed connection string needs an explicit probe: `MigrateJNotesDatabaseAsync` gains a bounded `Database.CanConnectAsync()` wait (10 attempts, 2 s apart) before `MigrateAsync()`, throwing a message that names `POSTGRES_CONNECTION_STRING`.

**Why a retry loop and not just "crash and let `restart: unless-stopped` handle it":** Postgres now holds non-derived data, and a cold `docker compose up` on a slow host would otherwise crash-loop the app until Postgres finishes `initdb`. The compose file also gets a `healthcheck` + `depends_on: condition: service_healthy`, which is the real fix; the retry is the belt-and-braces for `dotnet run` against a container you just started by hand.

### D3 — Migrations: delete the SQLite baseline, generate a fresh Postgres `InitialCreate`

**Decision:** delete all three files under `src/JNotes.Core/Data/Migrations/` and regenerate a single `InitialCreate` with the Npgsql provider.

**Rationale:** the existing migration is literal SQLite DDL (`type: "TEXT"`, `collation: "NOCASE"`) — `MigrateAsync` would emit it against Postgres and fail. Given the clean-break decision (no existing database is ever upgraded in place), there is nothing to preserve, and a one-migration baseline is the honest representation of "this schema has never shipped on Postgres before".

### D4 — Tags become a child table, not `jsonb` and not `text[]`

**Decision:** `NoteIndexTags(NoteIndexEntryId, Ordinal, Tag)`, PK `(NoteIndexEntryId, Ordinal)`, FK to `NoteIndexEntries` with `OnDelete(Cascade)`, non-unique index on `Tag`.

**Rationale**

* **It is the only shape where all four repository operations are plain, translatable LINQ.** Search has to be one query matching `Title` OR `Body` OR *any tag* with `ILIKE`. Over `jsonb` that needs `jsonb_array_elements_text` in a lateral join — not expressible in LINQ, so it would mean raw SQL or client-side evaluation. Npgsql's native `text[]` mapping translates `array.Contains(x)` to `= ANY(...)` but has no translation for "any element `ILIKE` a pattern".
* `GetDistinctTagsAsync` and the exact-tag filter become indexed SQL instead of "load every note for the user into memory and filter" — a strict improvement with **no behavioural change**.
* This is not a novel shape for this codebase: `RedisNoteIndexRepository`'s own remarks say "there's no separate join table **the way SQLite had one**". This is a return to a shape the pre-Redis index used.
* `Ordinal` exists so the note's own front-matter tag order (which the note list renders) survives the round trip. Duplicate tags remain possible, exactly as a JSON array allowed.

**Accepted cost:** `UpsertAsync` is no longer a single row write — it deletes and re-inserts the child rows, so it needs an explicit transaction (§4.6).

### D5 — `ILIKE '%term%'`, with the term escaped; no `pg_trgm`

**Decision:** `EF.Functions.ILike(column, pattern, "\\")` where `pattern` comes from a new pure static `SqlLikePattern.ContainsPattern(text)`. No `pg_trgm`, no GIN index.

**Rationale**

* `ILIKE` is the only construct that matches today's `Contains(..., OrdinalIgnoreCase)` semantics. `to_tsvector`/`plainto_tsquery` would stem words and stop matching partial words — a silent search behaviour change, which the brief forbids.
* **Escaping is not optional.** `%`, `_` and `\` are wildcards in `LIKE`/`ILIKE` but were literal characters under `Contains`. Searching for `100%` or `snake_case` would otherwise quietly match far too much. Hence a tested helper rather than string concatenation at the call site.
* **No `pg_trgm`:** `CREATE EXTENSION` inside the startup `MigrateAsync()` introduces a new privilege requirement that can hard-fail startup on a managed Postgres where the app user isn't the database owner. For a self-hosted note app, a sequential scan over one user's notes is not the bottleneck — and the Redis implementation was doing a *full client-side scan* of every note, so even an unindexed `ILIKE` is strictly faster than today. Recorded as a documented follow-up with the exact DDL (§11 Q2).

### D6 — `IndexedFileWriteTimeUtc` must be truncated to the index's resolution (**the non-obvious one**)

**Problem.** `NoteIndexReconciler.RescanAsync` decides "unchanged, skip re-parsing" with:

```csharp
if (!isNew && lastIndexedWriteTime >= currentWriteTime) { continue; }
```

`lastIndexedWriteTime` is read back from the index; `currentWriteTime` comes from `File.GetLastWriteTimeUtc`, which has 100-nanosecond tick precision. Redis stored the value as `ToString("O")` — full tick fidelity, so the two are byte-equal for an unchanged file. **PostgreSQL `timestamptz` has microsecond precision**, so the stored value comes back truncated (1 µs = 10 ticks), `lastIndexed >= current` is false roughly 9 times out of 10, and **every note looks modified on every rescan**: a full re-parse + re-upsert of the entire collection at startup, on every "Rescan Now", and every `AUTO_INDEX_INTERVAL_MINUTES`, permanently reporting a non-zero `Updated` count.

**Decision:** add a pure static `IndexTimestamp.ToIndexResolution(DateTimeOffset)` (rounds down to whole microseconds) and apply it in **two** places:

* `PostgresNoteIndexRepository.UpsertAsync` — normalize `IndexedFileWriteTimeUtc` on write, so what is stored equals what will be read.
* `NoteIndexReconciler.RescanAsync` — normalize `currentWriteTime` before comparing *and* before putting it on the new entry.

Both, not one, so the invariant holds no matter which path wrote the row. The cost is that a file whose mtime advanced by under 1 µs is treated as unchanged — irrelevant, since any real write moves mtime by orders of magnitude more.

**Regression guard already exists:** `NoteIndexReconcilerTests.RescanAsync_is_a_noop_when_nothing_changed_since_the_last_index` (and `AllUsersIndexReconcilerTests.ReconcileAllAsync_is_a_noop_with_no_changes`) fail without this fix.

**Related, and fine:** Npgsql refuses to write a `DateTimeOffset` whose `Offset != TimeSpan.Zero` to a `timestamptz`. Verified that **every** `DateTimeOffset` in JNotes is offset-zero (see the table in §1), so nothing needs converting. Worth knowing, not worth code.

### D7 — Case-insensitive usernames: an ICU non-deterministic collation, the direct NOCASE analogue

**Decision:**

```csharp
modelBuilder.HasCollation("jnotes_case_insensitive", locale: "und-u-ks-level2", provider: "icu", deterministic: false);
entity.Property(e => e.Username).UseCollation("jnotes_case_insensitive");
```

**Rationale:** it is a 1:1 replacement for `UseCollation("NOCASE")` — both the unique index *and* every `Username == x` comparison stay case-insensitive, so **`EfUserRepository` needs no change at all** and `GetUserByUsernameAsync_returns_the_matching_user_case_insensitively` keeps passing unmodified. It keeps the guarantee at the database layer, matching this codebase's stated "enforced in two layers" habit. ICU is compiled into the official Postgres images.

**Documented fallback if ICU turns out unavailable** (`ERROR: ICU is not supported in this build`): drop the collation, and instead lowercase the *input* in `GetUserByUsernameAsync`/`VerifyPasswordAsync`/the `CreateUserAsync` duplicate check. That is provably equivalent because `CreateUserAsync` already rejects any non-lowercase username via `UserValidation.IsLowercase`, so only lowercase rows can exist. It is the strictly simpler option but trades away the DB-level guarantee.

### D8 — Drop the SQLite `ORDER BY DateTimeOffset` workaround

Npgsql maps `DateTimeOffset` to `timestamptz` and orders it in SQL. `EfUserRepository.GetAllUsersAsync` becomes `await db.Users.AsNoTracking().OrderBy(u => u.CreatedAt).ToListAsync(ct)` and the client-side `OrderBy` and its explanatory comment go. `GetAllUsersAsync_returns_every_user_ordered_by_CreatedAt_ascending` is the existing guard.

### D9 — `NoteIndexEntry` stays the entity; `INoteIndexRepository` and every caller are unchanged

**Decision:** map the existing `JNotes.Core.Indexing.NoteIndexEntry` class directly as the EF entity (it is already a mutable class of `required` scalar properties — EF-friendly), and give `NoteIndexTag` an **FK-only relationship with no navigation property on either side**, exactly like the existing `UserSetting` → `UserAccount` configuration.

**Rationale:** zero changes to `NoteIndexEntry`, to `INoteIndexRepository`'s signatures, to `NoteService`, to `NoteIndexReconciler`, or to any existing test. A navigation collection on `NoteIndexEntry` would change its public shape and duplicate the `UpsertAsync(entry, tags)` parameter. The three queries that need tags use correlated subqueries / an explicit join instead — all standard translatable LINQ.

**Deliberately no FK from `NoteIndexEntries.UserId` to `Users.Id`.** Keeps the derived-cache tables referentially independent of the source-of-truth tables (nothing can ever fail to index because of an account-row ordering issue), and preserves exactly today's behaviour, including the pre-existing wart that deleting a user leaves their index rows behind (Redis leaves the keys behind identically). Flagged in §11 Q3, not fixed here.

### D10 — Lifetime unchanged: `INoteIndexRepository` stays a singleton

`PostgresNoteIndexRepository` takes `IDbContextFactory<JNotesDbContext>` (registered singleton by `AddDbContextFactory`) and creates a context per call — exactly like `SettingsService` and `EfUserRepository` already do. So `services.AddSingleton<INoteIndexRepository, …>()` keeps its lifetime and no other registration moves. `NoteService` (scoped) capturing a singleton stays correct.

### D11 — Data Protection keys get their own env var

There is no "`DB_PATH`'s directory" once Postgres may be on another host. New key **`DATA_PROTECTION_KEYS_PATH`**, read straight off `IConfiguration` in `Program.cs` as today. When unset it defaults to a `keys` folder **beside `NOTES_PATH`** — which reproduces the old location for both existing defaults (`App_Data/notes` → `App_Data/keys`; `/data/notes` → `/data/keys`), so nobody's keys move. It is also declared explicitly in `appsettings.json`, the `Dockerfile` and `docker-compose.yml`, so the derivation is a fallback rather than load-bearing.

**This is the easiest thing in the whole migration to miss.** Getting it wrong is silent: the app starts fine, and then every container restart regenerates the keys that sign the auth cookie and logs every user out. `DirectoryInfo` is used (not banned — only `System.IO.Directory` is), same as today.

### D12 — Postgres gets a persistent named volume; this is a behaviour change from the Redis service it replaces

The `redis` service deliberately had **no volume**, with a comment explaining that an empty cache after a restart is a one-time rescan and never data loss. **That reasoning does not transfer.** Postgres now holds user accounts and settings, which exist nowhere else — losing the volume means re-running `/setup` and re-creating every user. A named volume (`jnotes-pgdata`), not a bind mount: a Postgres data directory is not human-readable (unlike the notes, which is why *those* are bind-mounted) and bind-mounted `PGDATA` has well-known ownership problems on macOS/Windows. The compose comment must say all of this explicitly, and must say where to mount it (see the `postgres:18` `PGDATA` trap, §1/§4.11/§10 R1).

### D13 — Test isolation: a fresh database per fixture instance, dropped in teardown

**Decision:** a shared `TestPostgresDatabase` helper does `CREATE DATABASE "jnotes_test_<guid:N>"` in setup and `DROP DATABASE IF EXISTS … WITH (FORCE)` in teardown, over a raw `NpgsqlConnection` built from the base connection string. Connection string env var **`JNOTES_TEST_POSTGRES_CONNECTION_STRING`**, defaulting to `Host=localhost;Port=5432;Database=jnotes;Username=jnotes;Password=jnotes` — the exact naming convention already used for `JNOTES_TEST_REDIS_CONNECTION_STRING`.

**Why database-per-run rather than schema-per-run:** migrations run against a **virgin database**, identical to a real deployment — so `CREATE COLLATION`, the unique indexes and the filtered indexes are exercised the same way production exercises them, which is precisely the fidelity argument CLAUDE.md already makes for using a real temp-file SQLite database over the in-memory provider. Schema-per-run would make the tests depend on `search_path` semantics and on where EF Core's `__EFMigrationsHistory` lands when the model declares no default schema — subtleties with no upside here. It also maps 1:1 onto the "temp file per run" model it replaces.

**Two gotchas the helper must handle:**

* Npgsql pools connections per connection string, and disposing the `ServiceProvider` only *returns* EF's connections to the pool. `DROP DATABASE` then fails with "database is being accessed by other users". Hence: dispose the provider, then `NpgsqlConnection.ClearAllPools()`, then `DROP … WITH (FORCE)` (PG13+) as the final backstop.
* xUnit runs test classes in parallel, so several `CREATE DATABASE` statements can overlap. Wrap the create in a small bounded retry (3 attempts, 500 ms apart) rather than serializing all the DB-touching classes into one xUnit collection.

`CleanUpRedisAsync()` is deleted outright — a dropped database needs no key housekeeping.

---

## 3. Files to create / modify

| # | Path | Action |
| --- | --- | --- |
| 1 | `src/JNotes.Core/JNotes.Core.csproj` | Modify — swap Sqlite+Redis for Npgsql |
| 2 | `src/JNotes.Core/JNotesOptions.cs` | Modify — drop `DbPath`/`RedisConnectionString`, add `PostgresConnectionString` |
| 3 | `src/JNotes.Core/ServiceCollectionExtensions.cs` | Modify — `UseNpgsql`, delete the Redis block, swap the index repo |
| 4 | `src/JNotes.Core/HostExtensions.cs` | Modify — drop the WAL pragma, add the bounded connect wait |
| 5 | `src/JNotes.Core/Data/JNotesDbContext.cs` | Modify — collation, two new `DbSet`s + config |
| 6 | `src/JNotes.Core/Data/NoteIndexTag.cs` | **New** |
| 7 | `src/JNotes.Core/Data/JNotesDbContextFactory.cs` | Modify — design-time `UseNpgsql` |
| 8 | `src/JNotes.Core/Indexing/PostgresNoteIndexRepository.cs` | **New** |
| 9 | `src/JNotes.Core/Indexing/RedisNoteIndexRepository.cs` | **Delete** |
| 10 | `src/JNotes.Core/Indexing/SqlLikePattern.cs` | **New** — pure static, unit-tested |
| 11 | `src/JNotes.Core/Indexing/IndexTimestamp.cs` | **New** — pure static, unit-tested |
| 12 | `src/JNotes.Core/Indexing/NoteIndexReconciler.cs` | Modify — apply `IndexTimestamp` (D6) |
| 13 | `src/JNotes.Core/Users/EfUserRepository.cs` | Modify — order in SQL (D8), comment |
| 14 | `src/JNotes.Core/Data/Migrations/*` (3 files) | **Delete**, then regenerate |
| 15 | `src/JNotes.Web/Program.cs` | Modify — Data Protection keys path, comment |
| 16 | `src/JNotes.Web/BannedSymbols.txt` | Modify |
| 17 | `src/JNotes.Web/appsettings.json` | Modify |
| 18 | `src/JNotes.Web/Properties/launchSettings.json` | Modify |
| 19 | `Dockerfile` | Modify |
| 20 | `docker-compose.yml` | Modify — drop `redis`, add `postgres` + volume |
| 21 | `.gitignore` | Modify — one comment |
| 22 | `README.md` | Modify |
| 23 | Comment/doc sweep (13 files, §4.16) | Modify |
| 24 | `tests/JNotes.Tests/JNotes.Tests.csproj` | Modify |
| 25 | `tests/JNotes.Tests/Infrastructure/TestPostgresDatabase.cs` | **New** |
| 26 | `tests/JNotes.Tests/Integration/CoreServicesFixture.cs` | Modify |
| 27 | `tests/JNotes.Tests/Users/EfUserRepositoryTests.cs` | Modify — setup only |
| 28 | `tests/JNotes.Tests/Users/UserManagementServiceTests.cs` | Modify — setup only |
| 29 | `tests/JNotes.Tests/Indexing/SqlLikePatternTests.cs` | **New** |
| 30 | `tests/JNotes.Tests/Indexing/IndexTimestampTests.cs` | **New** |
| 31 | `tests/JNotes.Tests/Integration/NoteIndexRepositoryTests.cs` | **New** |
| 32 | `CLAUDE.md` | **HELD — only with explicit user approval** (§9) |

**Explicitly unchanged:** every file under `src/JNotes.Core/Storage/`, `Serialization/`, `Models/`, `Images/`, `Time/`; `INoteIndexRepository.cs` (signatures — only its doc comment changes); `NoteIndexEntry.cs` (shape — only its doc comment changes); `NoteService.cs`; `IAllUsersIndexReconciler`/`AllUsersIndexReconciler` (logic); every `.razor`, `.css` and `.js` file; `AutoIndexBackgroundService`/`ImageCleanupBackgroundService` (logic); `.config/dotnet-tools.json`; `Directory.Build.props`; `global.json`; `JNotes.slnx`.

---

## 4. Exact changes, per file

### 4.1 `src/JNotes.Core/JNotes.Core.csproj`

Remove:
```xml
<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.12" />
<PackageReference Include="StackExchange.Redis" Version="2.8.24" />
```
Add (keeping the alphabetical ordering of the `ItemGroup`):
```xml
<PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.3" />
```
`Microsoft.EntityFrameworkCore.Design` 10.0.12 stays as-is.

### 4.2 `src/JNotes.Core/JNotesOptions.cs`

Delete `DbPath` and `RedisConnectionString`. Add, in their place:

```csharp
/// <summary>
/// An Npgsql connection string (e.g. "Host=localhost;Port=5432;Database=jnotes;Username=jnotes;Password=…")
/// for the single PostgreSQL database holding user accounts, app/user settings, and the note index.
/// There is no fallback: a blank value fails startup loudly (see
/// <see cref="ServiceCollectionExtensions.AddJNotesCore"/>) rather than starting with nowhere to
/// write. Note the notes themselves never live here — they're markdown files under
/// <see cref="NotesFolderPath"/>, and the NoteIndex* tables are a derived, rebuildable cache over
/// them (see <see cref="Indexing.NoteIndexReconciler"/>).
/// </summary>
public string? PostgresConnectionString { get; set; }
```

Update the class-level `<summary>` to say `"NOTES_PATH"/"POSTGRES_CONNECTION_STRING"`.

### 4.3 `src/JNotes.Core/ServiceCollectionExtensions.cs`

* Remove `using StackExchange.Redis;`.
* In the `Configure<JNotesOptions>` delegate: replace the `DbPath` and `RedisConnectionString` lines with
  ```csharp
  options.PostgresConnectionString = configuration["POSTGRES_CONNECTION_STRING"] ?? options.PostgresConnectionString;
  ```
  and update the leading comment's key list (`"NOTES_PATH"/"POSTGRES_CONNECTION_STRING"/"TZ"`).
* Replace the whole `AddDbContextFactory` body:
  ```csharp
  services.AddDbContextFactory<JNotesDbContext>((sp, options) =>
  {
      var jnotesOptions = sp.GetRequiredService<IOptions<JNotesOptions>>().Value;

      // Thrown from here (rather than validated at registration) so it fires the first time a
      // context is created — which is HostExtensions.MigrateJNotesDatabaseAsync, before app.Run().
      // Same "fail loudly at startup, never limp along" property the old eager Redis connect had.
      if (string.IsNullOrWhiteSpace(jnotesOptions.PostgresConnectionString))
      {
          throw new InvalidOperationException(
              "POSTGRES_CONNECTION_STRING is required — JNotes keeps user accounts, settings and the note index in PostgreSQL.");
      }

      options.UseNpgsql(jnotesOptions.PostgresConnectionString);
  });
  ```
  The `Directory.CreateDirectory(dbDirectory)` block goes with it (there is no local database file any more).
* **Delete** the entire `services.AddSingleton<IConnectionMultiplexer>(…)` registration and its comment block.
* `services.AddSingleton<INoteIndexRepository, RedisNoteIndexRepository>();` → `services.AddSingleton<INoteIndexRepository, PostgresNoteIndexRepository>();` with a short comment: *singleton like the Redis one it replaces — it creates a `JNotesDbContext` per call through the singleton `IDbContextFactory`, exactly as `SettingsService`/`EfUserRepository` do.*
* Update the class doc comment: "it never registers a `DbContext` or file-store implementation itself" still holds; no wording change strictly needed beyond any stray "Redis" mention.

### 4.4 `src/JNotes.Core/HostExtensions.cs`

Rewrite `MigrateJNotesDatabaseAsync`:

```csharp
private const int MaxDatabaseConnectAttempts = 10;
private static readonly TimeSpan DatabaseConnectRetryDelay = TimeSpan.FromSeconds(2);

/// <summary>
/// Waits (briefly, boundedly) for PostgreSQL to accept connections, then applies pending EF Core
/// migrations. Uses Database.MigrateAsync() rather than EnsureCreated() so the schema can evolve
/// against users' existing databases — safe here because this runs as a single-instance container
/// with no multi-replica migration race.
/// The wait exists because EF Core connects lazily: a well-formed but unreachable
/// POSTGRES_CONNECTION_STRING would otherwise surface much later, and a cold `docker compose up`
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
                $"Could not reach PostgreSQL after {MaxDatabaseConnectAttempts} attempts — check POSTGRES_CONNECTION_STRING.");
        }

        logger?.LogInformation(
            "PostgreSQL isn't accepting connections yet (attempt {Attempt}/{MaxAttempts}); retrying in {DelaySeconds}s.",
            attempt, MaxDatabaseConnectAttempts, DatabaseConnectRetryDelay.TotalSeconds);
        await Task.Delay(DatabaseConnectRetryDelay);
    }
}
```

Add `using Microsoft.Extensions.Logging;`. The WAL `ExecuteSqlRawAsync` call and its comment are deleted entirely.

### 4.5 `src/JNotes.Core/Data/JNotesDbContext.cs`

Add `using JNotes.Core.Indexing;`. Add the two `DbSet`s:

```csharp
/// <summary>
/// The note index — a derived, rebuildable cache over the markdown files, not source data. Safe to
/// TRUNCATE: NoteIndexReconciler rebuilds it from disk on the next pass (startup, the auto-index
/// timer, or Settings -> "Rescan Now"). Named NoteIndex* so that's obvious in psql.
/// </summary>
public DbSet<NoteIndexEntry> NoteIndex => Set<NoteIndexEntry>();

public DbSet<NoteIndexTag> NoteIndexTags => Set<NoteIndexTag>();
```

In `OnModelCreating`, before the entity blocks:

```csharp
// PostgreSQL's equivalent of SQLite's NOCASE: a non-deterministic ICU collation makes both the
// unique index and every "Username == x" comparison case-insensitive, so EfUserRepository needs no
// ToLower() anywhere. NOTE: this four-argument HasCollation is Npgsql's
// (NpgsqlModelBuilderExtensions); EF Core's relational HasCollation takes one argument and sets the
// *database* default collation instead. Both live in the Microsoft.EntityFrameworkCore namespace,
// so the wrong overload binds silently.
modelBuilder.HasCollation(CaseInsensitiveCollation, locale: "und-u-ks-level2", provider: "icu", deterministic: false);
```

with `private const string CaseInsensitiveCollation = "jnotes_case_insensitive";` on the class.

Change `entity.Property(e => e.Username).UseCollation("NOCASE");` → `.UseCollation(CaseInsensitiveCollation);` and replace the NOCASE comment accordingly.

Keep both `HasFilter("\"Email\" IS NOT NULL")` / `HasFilter("\"OidcSubject\" IS NOT NULL")` unchanged — the double-quoted identifiers are already valid Postgres. Amend the comment: Postgres's plain unique index already tolerates multiple NULLs, so the filter is now belt-and-braces plus a smaller index, not a requirement.

Append:

```csharp
modelBuilder.Entity<NoteIndexEntry>(entity =>
{
    entity.ToTable("NoteIndexEntries");
    entity.HasKey(e => e.Id);
    // Exactly the default note-list query's shape (one user's notes, newest first).
    entity.HasIndex(e => new { e.UserId, e.UpdatedAt }).IsDescending(false, true);
    // Deliberately no FK to Users: the index is a derived cache and must never be able to fail to
    // write because of account-row ordering. (Same as Redis — a deleted user's rows are left
    // behind either way.)
});

modelBuilder.Entity<NoteIndexTag>(entity =>
{
    entity.HasKey(e => new { e.NoteIndexEntryId, e.Ordinal });
    entity.HasIndex(e => e.Tag);
    // FK with no navigation on either side, same as UserSetting -> UserAccount above:
    // NoteIndexEntry must stay the plain DTO INoteIndexRepository/NoteService/NoteIndexReconciler
    // already pass around, and the repository always writes an entry and its tags together.
    entity.HasOne<NoteIndexEntry>()
        .WithMany()
        .HasForeignKey(e => e.NoteIndexEntryId)
        .OnDelete(DeleteBehavior.Cascade);
});
```

### 4.6 `src/JNotes.Core/Data/NoteIndexTag.cs` (new)

```csharp
namespace JNotes.Core.Data;

/// <summary>
/// One tag on one indexed note. A child table rather than a jsonb/text[] column because search has
/// to match title OR body OR *any tag* with ILIKE in a single query, which needs an EXISTS
/// subquery — jsonb_array_elements_text isn't expressible in LINQ, and Npgsql's text[] mapping has
/// no "any element ILIKE" translation. It also turns the exact-tag filter and the distinct-tag
/// listing into indexed SQL instead of loading every note for the user into memory. The pre-Redis
/// SQLite index had a join table for the same reason.
/// Ordinal preserves the note's own front-matter tag order (what the note list renders), and — like
/// the JSON array it replaces — allows duplicates.
/// Rows are owned entirely by INoteIndexRepository.UpsertAsync/DeleteAsync and cascade with their
/// NoteIndexEntry; like everything else in the index, they're rebuildable from the files on disk.
/// </summary>
public sealed class NoteIndexTag
{
    public required Guid NoteIndexEntryId { get; set; }

    public required int Ordinal { get; set; }

    public required string Tag { get; set; }
}
```

### 4.7 `src/JNotes.Core/Data/JNotesDbContextFactory.cs`

```csharp
public JNotesDbContext CreateDbContext(string[] args)
{
    // `dotnet ef migrations add` never connects, so any well-formed string works; reading the env
    // var first means `dotnet ef database update` can still reach a real database if you want it
    // to (the app migrates itself at startup, so you normally don't need to).
    var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
        ?? "Host=localhost;Port=5432;Database=jnotes;Username=jnotes;Password=jnotes";

    var optionsBuilder = new DbContextOptionsBuilder<JNotesDbContext>()
        .UseNpgsql(connectionString);

    return new JNotesDbContext(optionsBuilder.Options);
}
```

### 4.8 `src/JNotes.Core/Indexing/SqlLikePattern.cs` (new)

Pure static, no DI, unit-testable — same tier as `AutoIndexInterval`/`UserValidation`.

```csharp
namespace JNotes.Core.Indexing;

/// <summary>
/// Turns a user's free-text search term into a safe ILIKE pattern. Exists because %, _ and \ are
/// wildcards in SQL LIKE but were ordinary characters under the Redis index's
/// string.Contains(..., OrdinalIgnoreCase) — searching for "100%" or "snake_case" must keep
/// meaning what it says.
/// </summary>
public static class SqlLikePattern
{
    /// <summary>The ESCAPE character used with every pattern this class produces.</summary>
    public const char EscapeCharacter = '\\';

    /// <summary>The same character as a string, for EF.Functions.ILike's escapeCharacter parameter.</summary>
    public const string EscapeString = "\\";

    /// <summary>Escapes <paramref name="text"/>'s LIKE wildcards and wraps it in %…% — a substring match.</summary>
    public static string ContainsPattern(string text) => $"%{Escape(text)}%";

    /// <summary>Escapes \, % and _ with <see cref="EscapeCharacter"/>. The backslash must be escaped first.</summary>
    public static string Escape(string text);
}
```

Implementation note: replace `\` → `\\` **first**, then `%` → `\%`, then `_` → `\_`. Doing it in any other order double-escapes.

### 4.9 `src/JNotes.Core/Indexing/IndexTimestamp.cs` (new)

```csharp
namespace JNotes.Core.Indexing;

/// <summary>
/// The note index stores timestamps in PostgreSQL `timestamptz`, which has **microsecond**
/// resolution, while File.GetLastWriteTimeUtc gives 100-nanosecond ticks. The reconciler's
/// "unchanged since last indexed" test is `lastIndexed >= currentMtime`, so without rounding both
/// sides to the store's own resolution, a truncated round trip makes every file look modified on
/// every single rescan — a full re-parse of the whole collection at startup and on every
/// auto-index tick. (Redis stored the full "O" round-trip string, so this didn't arise there.)
/// </summary>
public static class IndexTimestamp
{
    /// <summary>100-nanosecond ticks per microsecond.</summary>
    public const long TicksPerMicrosecond = 10;

    /// <summary>Rounds <paramref name="value"/> down to whole microseconds.</summary>
    public static DateTimeOffset ToIndexResolution(DateTimeOffset value) =>
        value.AddTicks(-(value.Ticks % TicksPerMicrosecond));
}
```

### 4.10 `src/JNotes.Core/Indexing/PostgresNoteIndexRepository.cs` (new)

Replaces `RedisNoteIndexRepository.cs` (delete that file). Same interface, same explicit-`userId` contract.

```csharp
using JNotes.Core.Data;
using JNotes.Core.Notes;
using Microsoft.EntityFrameworkCore;

namespace JNotes.Core.Indexing;

/// <inheritdoc cref="INoteIndexRepository"/>
/// <remarks>
/// Schema: one NoteIndexEntries row per note (see <see cref="NoteIndexEntry"/>) plus one
/// NoteIndexTags row per tag (see <see cref="NoteIndexTag"/>), in the same PostgreSQL database as
/// users/settings — one connection string, one migration history, one MigrateAsync at startup. The
/// index is still a rebuildable cache (see <see cref="NoteIndexReconciler"/>), so truncating both
/// tables is never a correctness problem, only a one-time rescan.
/// Text search is ILIKE '%term%', not to_tsvector/plainto_tsquery: it has to keep matching exactly
/// what the Redis implementation's case-insensitive Contains matched, partial words included.
/// Singleton, like the Redis repository it replaces: it creates a context per call through the
/// singleton IDbContextFactory, the same way SettingsService and EfUserRepository do.
/// </remarks>
public sealed class PostgresNoteIndexRepository : INoteIndexRepository
{
    private readonly IDbContextFactory<JNotesDbContext> _dbContextFactory;

    public PostgresNoteIndexRepository(IDbContextFactory<JNotesDbContext> dbContextFactory) =>
        _dbContextFactory = dbContextFactory;
    ...
}
```

**`SearchAsync`** — build the query incrementally, exactly mirroring the old filter order:

```csharp
await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

var entries = db.NoteIndex.AsNoTracking().Where(e => e.UserId == userId);

if (query.Type is { } type)
{
    var typeName = type.ToString();
    entries = entries.Where(e => e.NoteType == typeName);
}

if (!string.IsNullOrWhiteSpace(query.SearchText))
{
    var pattern = SqlLikePattern.ContainsPattern(query.SearchText);
    entries = entries.Where(e =>
        EF.Functions.ILike(e.Title, pattern, SqlLikePattern.EscapeString) ||
        EF.Functions.ILike(e.Body, pattern, SqlLikePattern.EscapeString) ||
        db.NoteIndexTags.Any(t => t.NoteIndexEntryId == e.Id
            && EF.Functions.ILike(t.Tag, pattern, SqlLikePattern.EscapeString)));
}

if (!string.IsNullOrWhiteSpace(query.Tag))
{
    var tag = query.Tag;
    // Exact and case-sensitive, matching the old List<string>.Contains(tag).
    entries = entries.Where(e => db.NoteIndexTags.Any(t => t.NoteIndexEntryId == e.Id && t.Tag == tag));
}

// Projected to an anonymous type and mapped afterwards: Enum.Parse has no SQL translation.
var rows = await entries
    .OrderByDescending(e => e.UpdatedAt)
    .Select(e => new
    {
        e.Id,
        e.Title,
        e.NoteType,
        e.CreatedAt,
        e.UpdatedAt,
        Tags = db.NoteIndexTags
            .Where(t => t.NoteIndexEntryId == e.Id)
            .OrderBy(t => t.Ordinal)
            .Select(t => t.Tag)
            .ToList(),
    })
    .ToListAsync(cancellationToken);

return rows
    .Select(r => new NoteSummary(
        r.Id,
        r.Title,
        Enum.Parse<Models.NoteType>(r.NoteType, ignoreCase: true),
        r.Tags,
        r.CreatedAt,
        r.UpdatedAt))
    .ToList();
```

**`GetDistinctTagsAsync`**:

```csharp
var tags = await (from t in db.NoteIndexTags.AsNoTracking()
                  join e in db.NoteIndex.AsNoTracking() on t.NoteIndexEntryId equals e.Id
                  where e.UserId == userId
                  select t.Tag)
    .Distinct()
    .ToListAsync(cancellationToken);

// Sorted in memory, exactly as the Redis version did: ORDER BY in SQL would use the database's
// collation, which orders mixed case and punctuation differently from StringComparer.Ordinal.
// The list is one user's distinct tags — tiny.
return tags.OrderBy(t => t, StringComparer.Ordinal).ToList();
```

**`UpsertAsync`** — "insert or fully replace", including tags:

```csharp
await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
// Explicit transaction because ExecuteDeleteAsync runs immediately, outside the change tracker: a
// failure between clearing the old tags and saving the new ones would otherwise leave a note with
// none.
await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

await db.NoteIndexTags
    .Where(t => t.NoteIndexEntryId == entry.Id)
    .ExecuteDeleteAsync(cancellationToken);

var existing = await db.NoteIndex.FirstOrDefaultAsync(e => e.Id == entry.Id, cancellationToken);
// Rounded to the store's own resolution so what comes back out of GetIndexedFileWriteTimesAsync
// equals what went in — see IndexTimestamp.
var indexedWriteTime = IndexTimestamp.ToIndexResolution(entry.IndexedFileWriteTimeUtc);

if (existing is null)
{
    db.NoteIndex.Add(new NoteIndexEntry { /* copy all 9 fields; IndexedFileWriteTimeUtc = indexedWriteTime */ });
}
else
{
    // Assign every field, not just the changed ones: this method's contract is a full replace.
    existing.UserId = entry.UserId;
    existing.Title = entry.Title;
    existing.Body = entry.Body;
    existing.NoteType = entry.NoteType;
    existing.CreatedAt = entry.CreatedAt;
    existing.UpdatedAt = entry.UpdatedAt;
    existing.IndexedFileWriteTimeUtc = indexedWriteTime;
    existing.FilePath = entry.FilePath;
}

var ordinal = 0;
foreach (var tag in tags)
{
    db.NoteIndexTags.Add(new NoteIndexTag { NoteIndexEntryId = entry.Id, Ordinal = ordinal++, Tag = tag });
}

await db.SaveChangesAsync(cancellationToken);
await transaction.CommitAsync(cancellationToken);
```

Note for the executor: construct a **new** `NoteIndexEntry` for the insert rather than `Add(entry)` — never attach the caller's instance to a context this method is about to dispose.

**`DeleteAsync`**:

```csharp
// Scoped by userId as well as id, so one user's id can never delete another's row (the Redis
// version deleted the hash by note id alone and only the set membership was user-scoped).
// No-ops when nothing matches. The tags cascade.
await db.NoteIndex
    .Where(e => e.Id == noteId && e.UserId == userId)
    .ExecuteDeleteAsync(cancellationToken);
```

**`GetIndexedFileWriteTimesAsync`**:

```csharp
return await db.NoteIndex.AsNoTracking()
    .Where(e => e.UserId == userId)
    .Select(e => new { e.Id, e.IndexedFileWriteTimeUtc })
    .ToDictionaryAsync(x => x.Id, x => x.IndexedFileWriteTimeUtc, cancellationToken);
```

### 4.11 `src/JNotes.Core/Indexing/NoteIndexReconciler.cs`

One change, in `RescanAsync`. After the `GetLastWriteTimeUtcAsync` try/catch:

```csharp
// Rounded to the index's own resolution before comparing or storing: PostgreSQL timestamptz keeps
// microseconds while the filesystem mtime has 100-nanosecond ticks, so an un-rounded comparison
// would make every unchanged file look modified on every rescan. See IndexTimestamp.
currentWriteTime = IndexTimestamp.ToIndexResolution(currentWriteTime);
```

The existing `if (!isNew && lastIndexedWriteTime >= currentWriteTime)` and the `IndexedFileWriteTimeUtc = currentWriteTime` assignment then both use the rounded value; no other edit.

### 4.12 `src/JNotes.Core/Users/EfUserRepository.cs`

`GetAllUsersAsync` becomes:

```csharp
await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
// Ordered in SQL: PostgreSQL orders timestamptz natively (the SQLite provider couldn't translate
// an ORDER BY over a DateTimeOffset column, which is why this used to materialize first).
return await db.Users.AsNoTracking().OrderBy(u => u.CreatedAt).ToListAsync(cancellationToken);
```

In `GetUserByUsernameAsync`, update the comment: *"Username's case-insensitive ICU collation (see `JNotesDbContext`) makes this comparison case-insensitive already"*. **No logic change anywhere else in this file.**

### 4.13 `src/JNotes.Core/Data/Migrations/` — regenerate

Delete:
* `20260917204544_InitialCreate.cs`
* `20260917204544_InitialCreate.Designer.cs`
* `JNotesDbContextModelSnapshot.cs`

Then, **after every model change in 4.5/4.6 compiles**:

```bash
dotnet tool restore
dotnet ef migrations add InitialCreate --project src/JNotes.Core --startup-project src/JNotes.Core -o Data/Migrations
```

Inspect the generated `Up()` and confirm it contains:
* `CREATE COLLATION` for `jnotes_case_insensitive` (`migrationBuilder.Sql` or an `AlterDatabase` annotation),
* `Users.Username` with `collation: "jnotes_case_insensitive"`,
* `uuid` for `Guid` columns, `text` for strings, `timestamp with time zone` for `DateTimeOffset`,
* `NoteIndexEntries` and `NoteIndexTags` with the FK, the `(UserId, UpdatedAt DESC)` index and the `Tag` index,
* the two filtered unique indexes on `Users`,
* **no** `TEXT` / `NOCASE` anywhere.

EF Core 9+ throws at `MigrateAsync` if the model has changes not captured in a migration, so this must be the **last** model-touching step before building.

### 4.14 `src/JNotes.Web/Program.cs`

Replace the Data Protection block (currently lines 31-38):

```csharp
// Persisted alongside the notes (the same bind-mounted volume in Docker) rather than the
// container's ephemeral filesystem — without this, the keys used to sign the auth cookie are
// regenerated on every restart, silently signing everyone out (ASP.NET Core logs a DataProtection
// warning about exactly this if left at its default).
// This used to derive from DB_PATH's directory; there is no local database file any more (Postgres
// may be on another host entirely), so it's its own key. When unset it falls back to a "keys"
// folder beside NOTES_PATH, which reproduces the old location for both defaults
// (App_Data/notes -> App_Data/keys, /data/notes -> /data/keys). Read directly off IConfiguration,
// like the OIDC values below, since this must be decided before the DI container exists.
var notesPath = builder.Configuration["NOTES_PATH"] ?? "notes";
var keysDirectory = builder.Configuration["DATA_PROTECTION_KEYS_PATH"]
    ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(notesPath))!, "keys");
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));
```

Also update the comment at ~line 96: *"reconcile every user's **PostgreSQL** note index against their notes folder"*.

### 4.15 `src/JNotes.Web/BannedSymbols.txt`

Delete lines 4-6 (`Microsoft.Data.Sqlite.SqliteConnection`, `StackExchange.Redis.IConnectionMultiplexer`, `StackExchange.Redis.IDatabase`) — those packages no longer exist in the graph. Keep the `File`/`Directory`/`DbContext` lines. Add:

```
T:Npgsql.NpgsqlConnection;Do not talk to PostgreSQL directly in JNotes.Web — go through INoteService/ISettingsService/INoteIndexReconciler in JNotes.Core.
T:Npgsql.NpgsqlDataSource;Do not talk to PostgreSQL directly in JNotes.Web — go through INoteService/ISettingsService/INoteIndexReconciler in JNotes.Core.
```

(`Npgsql` is visible to Web transitively through the `JNotes.Core` project reference, exactly as `StackExchange.Redis` and `Microsoft.Data.Sqlite` were, so the symbols resolve.)

### 4.16 Config and Docker files

**`src/JNotes.Web/appsettings.json`** — remove `DB_PATH` and `REDIS_CONNECTION_STRING`, add:
```json
"POSTGRES_CONNECTION_STRING": "Host=localhost;Port=5432;Database=jnotes;Username=jnotes;Password=jnotes",
"DATA_PROTECTION_KEYS_PATH": "App_Data/keys",
```
(A committed local-dev credential, same as the committed `localhost:6379` before it. The README must say: for anything real, set the env var instead.)

**`src/JNotes.Web/Properties/launchSettings.json`** — delete the `"REDIS_CONNECTION_STRING": "192.168.1.8:6329"` line. Do **not** substitute a LAN Postgres address; `appsettings.json`'s localhost default then applies. (§11 Q4 if the user wants a personal override back.)

**`Dockerfile`** — replace `ENV DB_PATH=/data/jnotes.db` with `ENV DATA_PROTECTION_KEYS_PATH=/data/keys`. Add no `POSTGRES_CONNECTION_STRING` default: there is no sensible one, and a wrong default that half-works is worse than a loud failure — `docker-compose.yml` supplies it. Change the mkdir line to `RUN mkdir -p /data/notes /data/keys && chown -R $APP_UID /data`.

**`docker-compose.yml`** — replace in full:

```yaml
services:
  jnotes:
    build: .
    ports:
      - "8080:8080"
    volumes:
      # Bind mount (not a named volume): the whole point of markdown-file storage is
      # host-visible, copyable, backupable notes. Swap for a named volume (e.g.
      # `jnotes-data:/data`) if you don't need host-side access to the files.
      - ./data:/data
    environment:
      - NOTES_PATH=/data/notes
      # Signs the auth cookie. On the bind mount on purpose: on the container filesystem these are
      # regenerated on every restart, silently signing everyone out.
      - DATA_PROTECTION_KEYS_PATH=/data/keys
      - POSTGRES_CONNECTION_STRING=Host=postgres;Port=5432;Database=jnotes;Username=jnotes;Password=${POSTGRES_PASSWORD:-jnotes}
      # Override by exporting TZ before running compose, or adding TZ=... to a .env file.
      - TZ=${TZ:-UTC}
      # Set to 0 to disable background index reconciliation.
      - AUTO_INDEX_INTERVAL_MINUTES=${AUTO_INDEX_INTERVAL_MINUTES:-5}
      # Optional OIDC login — uncomment and set via a .env file (or export before running compose)
      # to enable. All three must be set, or OIDC stays disabled. See README.md's Authentication section.
      # - OIDC_AUTHORITY=${OIDC_AUTHORITY}
      # - OIDC_CLIENT_ID=${OIDC_CLIENT_ID}
      # - OIDC_CLIENT_SECRET=${OIDC_CLIENT_SECRET}
      # - OIDC_DISPLAY_NAME=${OIDC_DISPLAY_NAME:-OIDC}
    depends_on:
      postgres:
        # Not just "started": the app migrates the schema before serving traffic, so it needs a
        # database that's finished initdb. (It also retries for ~20s on its own — see
        # HostExtensions.MigrateJNotesDatabaseAsync — but waiting properly is cleaner.)
        condition: service_healthy
    restart: unless-stopped

  postgres:
    image: postgres:18-alpine
    environment:
      - POSTGRES_DB=jnotes
      - POSTGRES_USER=jnotes
      # CHANGE THIS for anything reachable beyond your machine: export POSTGRES_PASSWORD=... or put
      # it in a .env file (gitignored). It has to match the app's POSTGRES_CONNECTION_STRING above.
      - POSTGRES_PASSWORD=${POSTGRES_PASSWORD:-jnotes}
    volumes:
      # Unlike the Redis service this replaces, the volume is NOT optional. Redis held only the note
      # index — a derived cache the startup reconciliation pass rebuilds from ./data/notes, so an
      # empty Redis was a one-time rescan and never data loss. PostgreSQL now also holds the user
      # accounts and settings, which exist nowhere else: lose this volume and you re-run /setup and
      # re-create every user. (The NoteIndex* tables in the same database are still throwaway.)
      #
      # Mounted at /var/lib/postgresql, NOT /var/lib/postgresql/data. The postgres:18+ images moved
      # PGDATA into a version-specific subdirectory (/var/lib/postgresql/18/docker) and declare the
      # VOLUME one level up. A /var/lib/postgresql/data mount is silently ignored on 18 — the data
      # goes to an anonymous volume and initdb re-runs on every recreate, with no error at all.
      #
      # A named volume rather than a bind mount: a Postgres data directory isn't human-readable
      # (unlike the notes, which is exactly why those are bind-mounted) and bind-mounted PGDATA has
      # well-known ownership problems on macOS/Windows. Back it up with pg_dump, not `cp`.
      - jnotes-pgdata:/var/lib/postgresql
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U jnotes -d jnotes"]
      interval: 5s
      timeout: 5s
      retries: 10
    restart: unless-stopped

volumes:
  jnotes-pgdata:
```

**`.gitignore`** — line 10's comment becomes: `# JNotes local dev data (notes files + Data Protection keys; see src/JNotes.Web/appsettings.json) and the`.

### 4.17 Comment / doc-comment sweep

Mechanical, no logic. Each says SQLite or Redis and must say PostgreSQL:

| File | What |
| --- | --- |
| `src/JNotes.Core/Indexing/INoteIndexRepository.cs:6` | "(Redis — see `RedisNoteIndexRepository`)" → "(PostgreSQL — see `PostgresNoteIndexRepository`)" |
| `src/JNotes.Core/Indexing/NoteIndexEntry.cs:3-8` | add that this is also the EF entity behind `NoteIndexEntries`, tags in the `NoteIndexTags` child table |
| `src/JNotes.Core/Indexing/NoteIndexReconciler.cs` / `INoteIndexReconciler.cs:4` | "one user's SQLite index rows" → "one user's index rows" |
| `src/JNotes.Core/Indexing/AutoIndexInterval.cs:9` | "without a host, a timer, or Redis" → "…or a database" |
| `src/JNotes.Core/Indexing/AllUsersIndexReconciler.cs:33, 53` | "file store/Redis" → "file store/index"; "One user's Redis/IO failure" → "One user's database/IO failure" |
| `src/JNotes.Core/Notes/NoteSummary.cs:5` | "projected from the SQLite index" → "from the note index" |
| `src/JNotes.Core/Notes/TrashedNoteSummary.cs:5` | "trash has no SQLite index" → "trash has no index" |
| `src/JNotes.Core/Notes/INoteService.cs:61` | "there's no SQLite index for it" → "there's no index for it" |
| `src/JNotes.Core/Notes/NoteGrouping.cs:22` | `RedisNoteIndexRepository.SearchAsync` → `PostgresNoteIndexRepository.SearchAsync` |
| `src/JNotes.Core/Notes/NoteService.cs:98-102` | the `ContentOnly` comment is still accurate; no change needed beyond checking it says nothing Redis-specific |
| `src/JNotes.Core/Users/OidcOptions.cs:6` | `"NOTES_PATH"/"DB_PATH"/"TZ"` → `"NOTES_PATH"/"POSTGRES_CONNECTION_STRING"/"TZ"` |
| `src/JNotes.Web/Indexing/AutoIndexBackgroundService.cs:8` | "every user's Redis index" → "every user's note index" |
| `src/JNotes.Web/Images/ImageCleanupBackgroundService.cs:18` | "one small SQLite read" → "one small database read" |
| `src/JNotes.Web/Components/Layout/NavMenu.razor:104` | `RedisNoteIndexRepository.SearchAsync` → `PostgresNoteIndexRepository.SearchAsync` |

`src/JNotes.Web/Components/Pages/Settings.razor` needs **no** change — it never names the store.

### 4.18 `README.md`

* **Intro (line 5-6):** "A Redis-backed index makes the notes fast to search/list, and a SQLite database stores app settings and user accounts" → a single PostgreSQL database holding user accounts, settings **and** the note index; the index is still a derived cache, never the source of truth, and the markdown files are.
* **Features → "Self-healing index" (line 20):** drop "Redis".
* **Project layout (line 30):** "file storage, Redis note indexing, EF Core/SQLite settings" → "file storage, note indexing, EF Core/PostgreSQL".
* **Line 35:** "never touches the filesystem, EF Core/SQLite, or Redis directly" → "…EF Core/PostgreSQL directly".
* **Running locally (lines 68-80):** rewrite. Notes still go under `src/JNotes.Web/App_Data/`; **PostgreSQL is now a requirement for running at all**, with the one-liner:
  ```bash
  docker run --rm -p 5432:5432 -e POSTGRES_USER=jnotes -e POSTGRES_PASSWORD=jnotes -e POSTGRES_DB=jnotes postgres:18-alpine
  ```
  after which `appsettings.json`'s default `POSTGRES_CONNECTION_STRING` just works. Keep the "the index is a rebuildable cache — an empty database is rebuilt by the startup reconciliation pass, and reconciliation also runs on a timer" paragraph, retargeted at the `NoteIndex*` tables, and add that **the accounts/settings tables beside them are not rebuildable and do need a backup**.
* **Upgrade callout (lines 85-92):** replace with the clean-break notice — > **Upgrading from a SQLite/Redis version of JNotes?** There is no automated migration. Your notes are untouched (they're files), but user accounts and settings do not carry over: point `POSTGRES_CONNECTION_STRING` at an empty database and you'll go through the `/setup` screen again. The re-created user gets a **new** user id, so rename `notes/<old-user-id>/` to `notes/<new-user-id>/` (find the new id in Settings → Administration settings) to get the old notes back, then Settings → "Rescan Now". You can delete the old `jnotes.db`/`-wal`/`-shm` files and stop your Redis container.
* **Configuration table:** delete the `DB_PATH` and `REDIS_CONNECTION_STRING` rows; add
  * `| POSTGRES_CONNECTION_STRING | Host=localhost;Port=5432;Database=jnotes;Username=jnotes;Password=jnotes (local) / the compose postgres service (Docker) | Npgsql connection string for the one database holding user accounts, settings and the note index. Required — there's no fallback, and a missing or unreachable value fails startup rather than starting degraded. |`
  * `| DATA_PROTECTION_KEYS_PATH | App_Data/keys (local) / /data/keys (Docker) | Where the keys that sign the auth cookie are stored. Must be on persistent storage — on a container's ephemeral filesystem every restart regenerates them and silently signs everyone out. Defaults to a keys folder beside NOTES_PATH. |`
* **Backup callout (lines 110-115):** replace the SQLite/WAL note with a Postgres one — `pg_dump` (or `docker compose exec postgres pg_dump -U jnotes jnotes > jnotes.sql`); copying the volume's files while the server runs is not a backup. Keep the closing reassurance: the notes are plain `.md` files safe to copy anytime, and the `NoteIndex*` tables in that dump are rebuildable anyway.
* **Tests section:** note the integration and `Users` suites need a **real PostgreSQL** (same `docker run` line; override with `JNOTES_TEST_POSTGRES_CONNECTION_STRING`), and that each test class creates and drops its own `jnotes_test_<guid>` database, so the configured user needs `CREATEDB`.
* **Running with Docker (lines 143-147):** notes and the Data Protection keys are bind-mounted to `./data`; the **database is a named Docker volume** (`jnotes-pgdata`), not a file under `./data` — deleting `./data` no longer resets accounts, and `docker compose down -v` does.

---

## 5. Ordered phases

Build after every phase. `dotnet build JNotes.slnx` must be clean at each checkpoint marked ✅.

**Phase 1 — packages and configuration surface**
1. `JNotes.Core.csproj` (§4.1), `JNotes.Tests.csproj` (§6.1).
2. `JNotesOptions.cs` (§4.2).
3. `ServiceCollectionExtensions.cs` (§4.3) — expect a build error on `PostgresNoteIndexRepository` until phase 3; that's fine.
4. `HostExtensions.cs` (§4.4).
5. `JNotesDbContextFactory.cs` (§4.7).

**Phase 2 — the EF model**
6. `Data/NoteIndexTag.cs` (§4.6).
7. `Data/JNotesDbContext.cs` (§4.5).
8. `Users/EfUserRepository.cs` (§4.12).

**Phase 3 — the index repository**
9. `Indexing/SqlLikePattern.cs` (§4.8), `Indexing/IndexTimestamp.cs` (§4.9).
10. `Indexing/PostgresNoteIndexRepository.cs` (§4.10).
11. Delete `Indexing/RedisNoteIndexRepository.cs`.
12. `Indexing/NoteIndexReconciler.cs` (§4.11).
✅ `dotnet build JNotes.slnx` — Core must compile with no `StackExchange.Redis`/`Sqlite` references left. Grep to confirm: `rg -n 'StackExchange|UseSqlite|Microsoft\.Data\.Sqlite' src/`.

**Phase 4 — migration baseline** (must come after all of phase 2/3, per §4.13)
13. Delete the three files in `Data/Migrations/`.
14. `dotnet tool restore`, then `dotnet ef migrations add InitialCreate --project src/JNotes.Core --startup-project src/JNotes.Core -o Data/Migrations`.
15. Inspect the generated `Up()` against the checklist in §4.13.
✅ Build.

**Phase 5 — Web**
16. `Program.cs` (§4.14).
17. `BannedSymbols.txt` (§4.15).
✅ Build — the banned-API analyzer runs here, so this is where a leftover Web-side Redis/Sqlite reference would surface.

**Phase 6 — config, Docker, ignore file**
18. `appsettings.json`, `launchSettings.json`, `Dockerfile`, `docker-compose.yml`, `.gitignore` (§4.16).

**Phase 7 — tests**
19. `tests/JNotes.Tests/Infrastructure/TestPostgresDatabase.cs` (§6.2).
20. `CoreServicesFixture.cs` (§6.3).
21. `EfUserRepositoryTests.cs`, `UserManagementServiceTests.cs` (§6.4).
22. New tests (§6.5, §6.6).
✅ `dotnet test JNotes.slnx` against a running Postgres — **all green**.

**Phase 8 — comments and docs**
23. The sweep (§4.17).
24. `README.md` (§4.18).
25. **Skip `CLAUDE.md`** unless the user has explicitly approved §9.
✅ `dotnet build JNotes.slnx`, `dotnet format JNotes.slnx --verify-no-changes`, `dotnet test JNotes.slnx`.

**Phase 9 — verification** (§7) and manual acceptance (§8).

---

## 6. Tests

### 6.1 `tests/JNotes.Tests/JNotes.Tests.csproj`

Remove `<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.12" />`. Add:
```xml
<PackageReference Include="Npgsql" Version="10.0.3" />
```
(The fixture uses `NpgsqlConnection`/`NpgsqlCommand`/`NpgsqlConnectionStringBuilder` directly. It is also available transitively through the Core project reference, but an explicitly-used package gets an explicit reference — and the Sqlite line it replaces was there for the same reason.)

### 6.2 `tests/JNotes.Tests/Infrastructure/TestPostgresDatabase.cs` (new)

```csharp
namespace JNotes.Tests.Infrastructure;

/// <summary>
/// A throwaway PostgreSQL database for one test class: CREATE DATABASE in setup, DROP in teardown.
/// The replacement for "a temp SQLite file per test class", and deliberately a whole database
/// rather than a schema so EF Core migrations run against a virgin database exactly as they do in
/// production — the same fidelity-over-convenience reasoning that keeps these tests off EF Core's
/// in-memory provider.
/// Point the tests at a different server with JNOTES_TEST_POSTGRES_CONNECTION_STRING, mirroring how
/// POSTGRES_CONNECTION_STRING works for the app. The configured user needs CREATEDB (the official
/// postgres image's POSTGRES_USER is a superuser, so the documented `docker run` just works).
/// </summary>
internal sealed class TestPostgresDatabase : IAsyncDisposable
{
    private const string ConnectionStringEnvVar = "JNOTES_TEST_POSTGRES_CONNECTION_STRING";
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=jnotes;Username=jnotes;Password=jnotes";
    private const int MaxCreateAttempts = 3;

    /// <summary>The connection string for this test's own database — what JNotesOptions gets.</summary>
    public string ConnectionString { get; }

    public static Task<TestPostgresDatabase> CreateAsync();

    public ValueTask DisposeAsync();
}
```

`CreateAsync`:
1. `var baseConnectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvVar) ?? DefaultConnectionString;`
2. `var maintenance = new NpgsqlConnectionStringBuilder(baseConnectionString);` — if `maintenance.Database` is null/empty, set it to `"postgres"` (CREATE/DROP need *some* existing database to connect to).
3. `var databaseName = "jnotes_test_" + Guid.NewGuid().ToString("N");`
4. `var target = new NpgsqlConnectionStringBuilder(maintenance.ConnectionString) { Database = databaseName };`
5. `CREATE DATABASE "<databaseName>"` over an `NpgsqlConnection` to the maintenance string, retried up to `MaxCreateAttempts` with a 500 ms gap — xUnit runs test classes in parallel and concurrent `CREATE DATABASE` statements contend on the template database. The name is interpolated because PostgreSQL can't parameterize identifiers; it's a locally-generated GUID, never user input.

`DisposeAsync`:
```csharp
// Npgsql pools per connection string, and disposing EF's contexts only returns connections to the
// pool rather than closing them — DROP DATABASE would fail with "database is being accessed by
// other users". Clearing the pools handles the common case; WITH (FORCE) (PostgreSQL 13+)
// terminates anything still attached.
NpgsqlConnection.ClearAllPools();
// ... DROP DATABASE IF EXISTS "<name>" WITH (FORCE) over the maintenance connection
```

Callers must dispose their `ServiceProvider` **before** disposing this.

### 6.3 `tests/JNotes.Tests/Integration/CoreServicesFixture.cs`

* Remove `using StackExchange.Redis;`, add `using JNotes.Tests.Infrastructure;`.
* Drop the `_rootDir`'s `dbPath`, the `RedisConnectionStringEnvVar` const and the `redisConnectionString` local; hold a `TestPostgresDatabase _database` field instead.
* `CreateAsync`: `var database = await TestPostgresDatabase.CreateAsync();` then
  ```csharp
  services.Configure<JNotesOptions>(o =>
  {
      o.NotesFolderPath = notesFolder;
      o.PostgresConnectionString = database.ConnectionString;
  });
  ```
  Keep `services.AddLogging()` and the `ICurrentUserContext` stub exactly as they are, and keep the `MigrateJNotesDatabaseAsync()` call and the two seeded users unchanged.
* `DisposeAsync`: **delete `CleanUpRedisAsync()` and the whole `CleanUpRedisAsync` method** (a dropped database needs no key housekeeping — call that out in the commit message as a simplification). New order: `_scope.Dispose()` → `await _provider.DisposeAsync()` → `await _database.DisposeAsync()` → delete `_rootDir`.
* Update the class `<summary>`: a temp folder, a **real PostgreSQL database created and dropped per test class** (see `TestPostgresDatabase`), two seeded users. Keep the "not EF Core's in-memory provider" justification — it now reads "so real PostgreSQL behaviour, the ICU collation, the filtered unique indexes and the migration itself are exercised".
* Keep every public accessor (`NoteService`, `Reconciler`, `AllUsersReconciler`, `FileStore`, `Serializer`, `UserRepository`, `Scope`, `NotesFolder`, `UserAId`, `UserBId`, `ActAsUserA/B`) and **add**:
  ```csharp
  public INoteIndexRepository IndexRepository => _scope.ServiceProvider.GetRequiredService<INoteIndexRepository>();
  ```
  for the new `NoteIndexRepositoryTests`.

### 6.4 `EfUserRepositoryTests.cs` / `UserManagementServiceTests.cs`

Identical minimal change in both, setup only — **no test body changes at all**:
* add `using JNotes.Tests.Infrastructure;` and a `private TestPostgresDatabase _database = null!;` field;
* in `InitializeAsync`, create it first and set `o.PostgresConnectionString = _database.ConnectionString;` in place of `o.DbPath = …`;
* in `DisposeAsync`, `await _provider.DisposeAsync();` then `await _database.DisposeAsync();` then the temp-dir delete.

`services.AddLogging()` is deliberately **not** added — `MigrateJNotesDatabaseAsync` resolves `ILoggerFactory` with `GetService`, not `GetRequiredService`, precisely so these two providers keep working unchanged (§4.4).

Two existing tests are now the guards for D7 and D8 and must pass untouched: `GetUserByUsernameAsync_returns_the_matching_user_case_insensitively` (the ICU collation) and `GetAllUsersAsync_returns_every_user_ordered_by_CreatedAt_ascending` (SQL ordering).

### 6.5 New pure unit tests (no database, no fixture)

**`tests/JNotes.Tests/Indexing/SqlLikePatternTests.cs`** — `[Theory]` over `ContainsPattern`:

| Input | Expected |
| --- | --- |
| `"needle"` | `"%needle%"` |
| `"100%"` | `"%100\\%%"` |
| `"snake_case"` | `"%snake\\_case%"` |
| `"back\\slash"` | `"%back\\\\slash%"` |
| `"a\\%b"` | `"%a\\\\\\%b%"` (backslash escaped first — the ordering guard) |
| `""` | `"%%"` |
| `"  spaced  "` | `"%  spaced  %"` (no trimming — `Contains` never trimmed) |

**`tests/JNotes.Tests/Indexing/IndexTimestampTests.cs`**:
* a value with sub-microsecond ticks rounds **down** to the microsecond (`…1234567` → `…123456`);
* a value already on a microsecond boundary is returned unchanged;
* `ToIndexResolution(x) <= x` always;
* the offset is preserved.

### 6.6 New integration tests

**`tests/JNotes.Tests/Integration/NoteIndexRepositoryTests.cs`** (uses `CoreServicesFixture`) — this is the file that pins the new implementation against §1's "Search matching" row:

1. `SearchAsync_matches_the_title_the_body_and_a_tag_case_insensitively` — upsert one note per field, search a differently-cased substring of each, assert each is found.
2. `SearchAsync_treats_a_percent_or_underscore_in_the_term_as_a_literal` — **the escaping regression guard; do not drop it.** Index titles `"100% done"` and `"1000 done"`; searching `"100%"` returns only the first. Index `"snake_case"` and `"snakeXcase"`; searching `"snake_case"` returns only the first.
3. `UpsertAsync_replaces_the_previous_tags_rather_than_adding_to_them` — upsert with `["a","b"]`, re-upsert with `["c"]`, assert the summary's tags are exactly `["c"]` and `GetDistinctTagsAsync` no longer contains `a`/`b`.
4. `SearchAsync_preserves_the_tag_order_it_was_given` — `["zebra","apple"]` comes back in that order, not sorted.
5. `SearchAsync_filters_by_an_exact_case_sensitive_tag` — `query.Tag = "Work"` does not match a note tagged `"work"` (matching the old ordinal `List.Contains`).
6. `GetDistinctTagsAsync_is_distinct_ordinal_sorted_and_scoped_to_the_user` — user B's tags never appear for user A.
7. `DeleteAsync_ignores_a_note_id_that_belongs_to_another_user` — call with user B's id and user A's note id; user A's row survives.
8. `DeleteAsync_is_a_noop_for_an_unknown_id`.
9. `GetIndexedFileWriteTimesAsync_round_trips_the_mtime_at_index_resolution` — upsert an entry whose `IndexedFileWriteTimeUtc` has sub-microsecond ticks, read it back, assert it equals `IndexTimestamp.ToIndexResolution(original)`. **This is the D6 unit-level guard.**
10. `SearchAsync_filters_by_note_type`.

**Existing integration tests that must pass with no edits** (they are the real safety net; name them in the commit message):
* `NoteIndexReconcilerTests` — all 8, especially `RescanAsync_is_a_noop_when_nothing_changed_since_the_last_index` (D6's system-level guard) and `RescanAsync_records_an_error_and_preserves_the_existing_row_for_corrupted_front_matter` (confirms the "error recorded in the result, row kept, **no parse-error column**" behaviour is preserved).
* `AllUsersIndexReconcilerTests` — all 4, especially `ReconcileAllAsync_is_a_noop_with_no_changes`.
* `NoteServiceTests`, `SettingsServiceTests`, `ImageServiceTests`, `ImageCleanupServiceTests`.
* `Storage/FileSystemNoteFileStoreTests` and every pure unit suite — untouched by this change.

**Deliberately not tested:** the `MigrateJNotesDatabaseAsync` retry loop (a real-clock test means sleeping 20 s; the logic is a bounded `for` over `CanConnectAsync`), and the compose healthcheck (verified manually, §8.1).

---

## 7. Verification commands (from repo root)

```bash
# Start the database the tests and the local app both use (leave it running):
docker run --rm -p 5432:5432 \
  -e POSTGRES_USER=jnotes -e POSTGRES_PASSWORD=jnotes -e POSTGRES_DB=jnotes \
  postgres:18-alpine

dotnet build JNotes.slnx
dotnet format JNotes.slnx --verify-no-changes

# Fast loop, no database needed:
dotnet test tests/JNotes.Tests --filter "FullyQualifiedName~SqlLikePatternTests"
dotnet test tests/JNotes.Tests --filter "FullyQualifiedName~IndexTimestampTests"

# Full suite — needs the Postgres above (or JNOTES_TEST_POSTGRES_CONNECTION_STRING):
dotnet test JNotes.slnx

# Grep guards — all four must return nothing:
rg -n 'StackExchange|IConnectionMultiplexer|UseSqlite|Microsoft\.Data\.Sqlite' src/ tests/
rg -n 'REDIS_CONNECTION_STRING|DB_PATH' src/ tests/ Dockerfile docker-compose.yml README.md
rg -n 'RedisNoteIndexRepository' src/ tests/
rg -n 'JNOTES_TEST_REDIS_CONNECTION_STRING' src/ tests/ README.md

dotnet run --project src/JNotes.Web     # for §8
docker compose up --build               # for §8.1
```

`dotnet format` does not touch `.yml`/`.json`, but `.editorconfig`'s `[*]` rules (UTF-8, trailing-whitespace trim, final newline) apply to them.

---

## 8. Acceptance checklist (mechanical — for the tester)

Prerequisite for 8.2-8.10: a running local Postgres per §7 and `dotnet run --project src/JNotes.Web`. Start from an **empty** database and a **deleted** `src/JNotes.Web/App_Data/` so the first-run path is genuinely exercised.

1. **Docker path.** `docker compose up --build`. Expect: the `postgres` service goes healthy before `jnotes` starts; `jnotes` logs no connection retries; `http://localhost:8080` loads **in a real browser** (per CLAUDE.md's Docker gotcha — a `curl` check cannot detect a missing static asset). Then `docker compose down` (no `-v`) and `docker compose up` again: **you are still signed in** (Data Protection keys persisted to `./data/keys`) and your account still exists (the named volume persisted). Confirm `docker volume ls` shows `jnotes-pgdata`, and `docker compose exec postgres ls /var/lib/postgresql/18/docker` lists a real `PGDATA` (not empty — the §4.16 mount-path trap).
2. **Setup flow.** `/` redirects to `/setup`. Create an admin with a username, an email and a password. You land signed in.
3. **Schema.** `psql "postgresql://jnotes:jnotes@localhost:5432/jnotes" -c '\dt'` lists `Settings`, `Users`, `UserSettings`, `NoteIndexEntries`, `NoteIndexTags`, `__EFMigrationsHistory` — and nothing else. `\d "Users"` shows `Username` with collation `jnotes_case_insensitive`.
4. **Case-insensitive login.** Sign out, sign in with the username in UPPERCASE. It works.
5. **Notes CRUD + list.** Create a text note with a title, two tags and a body; create a checklist note. Both appear on `/` under "Today", in `UpdatedAt`-descending order. The Tag filter dropdown offers both tags; picking one filters correctly.
6. **Search (the behaviour-preservation check).** Search a substring of a title → found. A substring of the **body** → found. A **tag** → found. A differently-cased substring → found. A **partial word** (e.g. `eedl` for "needle") → **found** (this is what would break under `tsvector`). Now title a note `Budget 100% done` and another `Budget 1000 done`, and search `100%` → **only the first** (the escaping check).
7. **Checklist toggle.** Open the checklist note, tick an item: it sinks below the unchecked ones and stays ticked after a reload. Add an item; add a category.
8. **Trash.** Delete a note → it leaves `/` immediately and appears in `/trash`. Restore it → back on `/`. Delete and purge → gone from both, and `select count(*) from "NoteIndexEntries"` drops by one.
9. **Index rebuild from truncation (the derived-cache guarantee).**
   ```bash
   psql "postgresql://jnotes:jnotes@localhost:5432/jnotes" \
     -c 'TRUNCATE "NoteIndexEntries" CASCADE;'
   ```
   `/` is now empty (the index is the only list source). Settings → Administration → Index → **Rescan Now**. Every note comes back with its correct title, tags and timestamps, and search finds their bodies again. `select count(*) from "NoteIndexTags"` is non-zero. **Zero note data lost.** Then restart the app with the tables truncated again and confirm the *startup* pass rebuilds them with no user action.
10. **No spurious re-indexing (D6).** With nothing edited, click **Rescan Now** twice. The result must report **0 added, 0 updated, 0 removed** both times. (Before the `IndexTimestamp` fix this reports the full note count as "updated", every time.)
11. **`ContentOnly` staleness still behaves as documented.** Edit only a note's body, wait for "All changes saved", and confirm it has *not* moved in the list; click Rescan Now and confirm it now has.
12. **User CRUD.** Settings → Administration: add a Member (with an email), edit their first/last name, reset their password, sign in as them (only "App settings" visible), sign back in as admin, delete them. Confirm `notes/deleted/<their-id>/` exists on disk and their `Users` row is gone.
13. **Fail-fast.** Stop the app. Run it with `POSTGRES_CONNECTION_STRING=` (empty): it must fail at startup with a message naming `POSTGRES_CONNECTION_STRING`, not start and 500 later. Run it with a valid-looking but unreachable host (e.g. port 5433): it must log ~10 retries and then fail with the "Could not reach PostgreSQL" message.
14. **Data Protection keys.** Confirm `src/JNotes.Web/App_Data/keys/` contains a `key-*.xml`, and that no DataProtection "not persisted" warning appears in the startup log.

---

## 9. Documentation updates

### `CLAUDE.md` — **HELD: do not apply without explicit user approval**

Proposed edits, for the user to approve or decline. The executor must **not** touch `CLAUDE.md` unless the user explicitly confirms.

1. **"What this is" (lines 7-11).** "a Redis index over them for fast search — the index is a derived cache, never the source of truth — and a SQLite database for everything else (settings, user accounts)" → **a single PostgreSQL database holding user accounts and settings *and* the note index; the index tables are still a derived cache, never the source of truth, and the markdown files still are.**
2. **"Commands" (line 18).** "integration tests need a real Redis (see Tests)" → **"need a real PostgreSQL (see Tests)"**. The `dotnet ef` lines stay verbatim — `dotnet-ef` is provider-agnostic and the startup-project rule is unchanged.
3. **"Architecture" (lines 37-46).** "`JNotes.Core` (domain, storage, indexing, EF Core, Redis)" → drop Redis. "Web never touches the filesystem, EF Core/SQLite, or Redis directly" → **"EF Core/PostgreSQL"**; the banned-symbol list becomes `System.IO.File`/`Directory`, `DbContext`, and **`Npgsql`'s `NpgsqlConnection`/`NpgsqlDataSource`**.
4. **New paragraph, "Data Protection keys" (lines 113-115).** Keys now live at `DATA_PROTECTION_KEYS_PATH`, defaulting to a `keys` folder **beside `NOTES_PATH`** (which reproduces the old `{DB_PATH's directory}/keys` for both defaults), because with a possibly-remote Postgres there is no "next to the database" any more. Same silent failure mode if it lands on an ephemeral filesystem.
5. **"Storage and indexing" (lines 202-231).** Retitle the framing to "Files are the source of truth; the `NoteIndex*` tables are a derived index". Line 212 "`ListAsync`/search read only the Redis index" → "the index tables". Line 223 "There's deliberately no Redis index for the trash" → "no index for the trash". Add the three new invariants:
   * the index lives in the **same** database and schema as users/settings (D1) — one connection string, one migration, and the `NoteIndex*` prefix is the "these are the throwaway tables" marker;
   * **tags are a child table, not `jsonb`/`text[]`** (D4) — because "title OR body OR any tag `ILIKE`" has to be one translatable LINQ query, and because the pre-Redis SQLite index already had a join table;
   * **`IndexedFileWriteTimeUtc` is rounded to microseconds on both sides** (D6) — `timestamptz` is microsecond-resolution while the filesystem mtime is 100 ns ticks, and without the rounding the reconciler's `lastIndexed >= currentMtime` test makes every file look modified on every rescan. This is exactly the kind of non-obvious trap CLAUDE.md exists to record.
6. **Line 254 / lines 262-266.** `RedisNoteIndexRepository.UpsertAsync` "replaces the whole hash, tags included" → `PostgresNoteIndexRepository.UpsertAsync` replaces the row **and its tag rows, in one transaction**. The search paragraph: `SearchAsync` now filters in SQL with `ILIKE '%term%'` over `Title`/`Body` and an `EXISTS` over `NoteIndexTags` — deliberately `ILIKE` and **not** `tsvector`/`plainto_tsquery`, which would stem and stop matching partial words. `query.Tag` stays exact and case-sensitive. `GetDistinctTagsAsync` still sorts `Ordinal` **in memory**, because the DB collation orders mixed case differently.
7. **Authentication section (line 126).** `OidcOptions` reads flat keys "like `NOTES_PATH`/`DB_PATH`/`TZ`" → `NOTES_PATH`/`POSTGRES_CONNECTION_STRING`/`TZ`.
8. **Authentication section (lines 131-140).** "the NOCASE column already makes lookups case-insensitive" → a **non-deterministic ICU collation** (`jnotes_case_insensitive`, `und-u-ks-level2`) does. Worth recording the trap: Npgsql's 4-argument `HasCollation` and EF Core's 1-argument relational `HasCollation` (which sets the *database* default) are both in the `Microsoft.EntityFrameworkCore` namespace, so the wrong one binds silently.
9. **"Tests" (lines 473-485).** "a temp-file SQLite database, a **real Redis** (`localhost:6379`, or `JNOTES_TEST_REDIS_CONNECTION_STRING`; e.g. `docker run --rm -p 6379:6379 valkey/valkey`)" → **a real PostgreSQL** (`JNOTES_TEST_POSTGRES_CONNECTION_STRING`, default `Host=localhost;Port=5432;Database=jnotes;Username=jnotes;Password=jnotes`; e.g. `docker run --rm -p 5432:5432 -e POSTGRES_USER=jnotes -e POSTGRES_PASSWORD=jnotes -e POSTGRES_DB=jnotes postgres:18-alpine`), with a **fresh `jnotes_test_<guid>` database created and dropped per test class** (`TestPostgresDatabase`) — a whole database rather than a schema so migrations run against a virgin database exactly as in production. Replace the "SQLite-specific behavior (e.g. no `ORDER BY DateTimeOffset`)" example, which no longer exists, with: the real provider is used so the ICU collation, the filtered unique indexes, the `ILIKE` escaping and the migration itself are exercised. Add the two Npgsql pooling gotchas (`ClearAllPools` + `DROP … WITH (FORCE)`) and that `CleanUpRedisAsync` is gone.
10. **New "Docker" note** beside the existing build gotcha: the `postgres:18`+ `PGDATA` move (`/var/lib/postgresql/18/docker`, `VOLUME /var/lib/postgresql`) and that a `/var/lib/postgresql/data` mount is **silently ignored**; and that unlike the Redis service it replaces, the Postgres volume is **mandatory**, since accounts and settings exist nowhere else.

### `README.md` (in scope) — see §4.18.

---

## 10. Risks and gotchas

**R1 — `postgres:18` silently ignores a `/var/lib/postgresql/data` volume.** The highest-severity item in this plan, because the failure is silent *and* it now destroys non-derived data: the container starts clean every recreate and the user loses every account. Mitigated by mounting `/var/lib/postgresql` (§4.16) with a comment saying why, and by acceptance step 8.1 which explicitly checks `PGDATA` is non-empty after a `down`/`up`.

**R2 — The microsecond truncation (D6).** Symptom if missed: "Rescan Now" always reports every note as "updated", every auto-index tick re-parses the whole collection, and two existing tests fail. Caught by `RescanAsync_is_a_noop_when_nothing_changed_since_the_last_index` and by acceptance step 8.10.

**R3 — Data Protection keys (D11).** Symptom if missed: everything works, and then every restart signs everyone out. Nothing fails, nothing logs an error at the point of the mistake. Acceptance step 8.1 (sign-in survives a `down`/`up`) and 8.14 (the key file exists) are the checks.

**R4 — `ILIKE` escaping.** Without `SqlLikePattern`, a search for `%` matches everything and `_` matches any character. Pinned by `SearchAsync_treats_a_percent_or_underscore_in_the_term_as_a_literal` and acceptance step 8.6.

**R5 — `ILIKE` case-folding vs `OrdinalIgnoreCase`.** `ILIKE` folds case using the database's `lc_ctype`; `OrdinalIgnoreCase` is invariant. For ASCII they agree exactly. For non-ASCII edge cases (Turkish dotless i, `İ`) a search could match slightly differently than before. Accepted: this only ever *widens or narrows a fuzzy search by one character class*, and the alternative — pulling every note into memory to preserve bit-exact `OrdinalIgnoreCase` — is what we are deliberately moving away from. Worth a sentence in the CLAUDE.md search paragraph if §9 is approved.

**R6 — Non-deterministic collation availability (D7).** If the server was built without ICU, `CREATE COLLATION` fails during `MigrateAsync` and the app will not start. The official images have ICU. The documented fallback (lowercase the lookup input, plain unique index) is in D7; it is a two-method change in `EfUserRepository` plus dropping two lines from `JNotesDbContext` and regenerating the migration.

**R7 — `CREATEDB` in the test environment.** The fixture creates and drops databases, so `JNOTES_TEST_POSTGRES_CONNECTION_STRING`'s user needs `CREATEDB`. The documented `docker run` uses a superuser, so a local run just works; a shared/managed CI Postgres may not. Failure is loud and obvious (`permission denied to create database`).

**R8 — Test-suite runtime.** Each DB-touching test class now pays one `CREATE DATABASE` + full migration (~0.3-1 s) instead of creating a SQLite file (~0 ms). Across the ~8 affected classes that is a few seconds, parallelised. Acceptable; noted so nobody thinks something hung. The pure unit suites are unaffected and remain the fast loop.

**R9 — Concurrent `CREATE DATABASE`.** xUnit's parallel class execution can have several fire at once and contend on the template database. Mitigated by the 3-attempt retry in `TestPostgresDatabase.CreateAsync`. If it still proves flaky, the fallback is a single `[Collection("Postgres")]` across the DB-touching classes, trading parallelism for determinism.

**R10 — `Users`/`Settings` are no longer backed up by copying a folder.** `./data` no longer contains the accounts database. Anyone whose backup script copies `./data` now silently backs up notes and keys only. This is a genuine operational regression from SQLite and must be stated plainly in the README's backup callout (`pg_dump`), not buried.

**R11 — Two new moving parts at runtime.** A remote Postgres means network latency on every settings read and every note-list load, where SQLite was a local file read. At this app's scale (one user's notes, a handful of accounts) this is not a concern, and the *index* path is strictly faster than before (SQL filtering versus loading every note into memory). No connection pooler proposed.

**R12 — `docker compose down -v` is now destructive.** It deletes `jnotes-pgdata`, i.e. every account and setting — whereas with Redis it only dropped a rebuildable cache. README + CLAUDE.md wording (§9.10) should say so.

**R13 — The committed local-dev password.** `appsettings.json` ships `Password=jnotes`, exactly as it shipped `localhost:6379` before. Fine for a local dev default, and the README says to use the env var for anything real; but it is a credential in git for the first time, so it deserves the explicit sentence rather than being quietly introduced.

**R14 — Migration baseline is destructive to any existing dev database.** Regenerating `InitialCreate` (D3) means an existing `jnotes.db` is orphaned and an existing Postgres database with the old history table can't be migrated. Intended (clean break), but the executor should delete `src/JNotes.Web/App_Data/` and start from an empty Postgres rather than wonder why `MigrateAsync` complains.

**R15 — `EF.Functions.ILike`'s three-argument overload.** If the executor uses the two-argument overload, the escaping in `SqlLikePattern` produces `\%` which Postgres still interprets with the *default* escape character `\` — so it happens to work. It is specified with the explicit escape character anyway so the behaviour is not dependent on `standard_conforming_strings` or a server-side default.

---

## 11. Open questions (recommended default given for each — none blocking)

**Q1 — Table/entity naming.** `NoteIndexEntries`/`NoteIndexTags` + `PostgresNoteIndexRepository`. *Default: as written* — the table prefix makes "these are the rebuildable ones" visible in `psql`, and `PostgresNoteIndexRepository` mirrors `RedisNoteIndexRepository`'s "name the store" convention (the closer precedent for this file than `EfUserRepository`). Alternative: `EfNoteIndexRepository`, for symmetry with `EfUserRepository`.

**Q2 — `pg_trgm` + GIN index for `ILIKE`.** *Default: skip it* (D5). `CREATE EXTENSION` in the startup migration adds a privilege requirement that can hard-fail startup on a managed Postgres, and an unindexed `ILIKE` over one user's notes is already faster than today's full client-side scan. If you want it later it is a standalone migration and nothing else changes:
```sql
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE INDEX "IX_NoteIndexEntries_Title_trgm" ON "NoteIndexEntries" USING gin ("Title" gin_trgm_ops);
CREATE INDEX "IX_NoteIndexEntries_Body_trgm"  ON "NoteIndexEntries" USING gin ("Body"  gin_trgm_ops);
```

**Q3 — Orphaned index rows after a user is deleted.** Today (Redis) and after this change (no FK, D9), deleting a user leaves their index rows behind forever — the reconciler only loops over *existing* users. An FK to `Users` with `OnDelete(Cascade)` would fix it for free. *Default: leave it* — it is a pre-existing wart, fixing it is a behaviour change outside this migration's remit, and the coupling costs the derived-cache tables their referential independence. Say the word and it is two lines in `JNotesDbContext` plus a regenerated migration.

**Q4 — `launchSettings.json`.** The `http` profile currently pins `REDIS_CONNECTION_STRING=192.168.1.8:6329` — a personal LAN address. *Default: delete the line* and let `appsettings.json`'s localhost default apply. Tell me if you want a `POSTGRES_CONNECTION_STRING` override for that same host put back.

**Q5 — PostgreSQL major version.** *Default: `postgres:18-alpine`* — 18 is the current stable line (18.6, Aug 2026); 19 was still in beta as of August 2026 and may have gone GA this month. The mature line is the right call for a self-hosted app, and 18 is also where LIKE-on-non-deterministic-collations support landed. Bumping later is a one-line image change plus a `pg_upgrade`/dump-restore.

**Q6 — Full-text search as a follow-up.** Genuine `tsvector` search (with a GIN index, ranking, and prefix matching via `to_tsquery('needl:*')`) is a real feature worth having, but it *changes what matches* — so it belongs behind its own decision and its own plan, not smuggled in as a side effect of an infrastructure move. *Default: out of scope, recorded here.*

---

## 12. Facts verified while planning

So the executor does not have to re-check: `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3 is the latest stable for EF Core 10 and requires EF Core ≥ 10.0.4 (repo pins 10.0.12); PostgreSQL 18 is the current stable major and 19 was in beta as of Aug 2026; `postgres:18`+ moved `PGDATA` to `/var/lib/postgresql/18/docker` with `VOLUME /var/lib/postgresql`, silently ignoring a `.../data` mount; `EF.Functions.ILike` has 2- and 3-argument overloads; Npgsql's `HasCollation(name, locale, provider, deterministic)` exists and collides by namespace with EF Core's 1-argument relational `HasCollation`; non-deterministic collations rejected `LIKE` before PostgreSQL 18; `dotnet-ef` is provider-agnostic so `.config/dotnet-tools.json` needs no change; the existing `HasFilter("\"Email\" IS NOT NULL")` strings are already valid PostgreSQL; `NoteIndexReconciler` records parse errors in `ReconciliationResult.Errors` and keeps the existing row, so **no parse-error column is needed**; `RedisNoteIndexRepository`'s own remarks confirm the pre-Redis SQLite index had a tag join table; every `DateTimeOffset` in the app is offset-zero, so Npgsql's `timestamptz` offset restriction is already satisfied; `EfUserRepositoryTests`/`UserManagementServiceTests` build providers without `AddLogging()` and never resolve `IConnectionMultiplexer`, which is why they pass today with no Redis running; `Settings.razor` never names the datastore; `IDbContextFactory<T>` from `AddDbContextFactory` is a singleton, so a singleton index repository over it is correct; `docs/plans/` contains five prior plans whose format this one follows.
