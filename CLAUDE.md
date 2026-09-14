# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

JNotes is a self-hosted, multi-user note-taking app: Blazor Server (.NET 10) UI, notes stored as
portable markdown files on disk (each a readable `.md` with YAML front matter, one subfolder per
user), and a SQLite database that indexes them for fast search — the index is a **derived cache**,
never the source of truth. See README.md for features, the note file format, and full
configuration reference.

**Login doesn't exist yet.** "Multi-user" so far means data scoping only — every note, its search
index, and per-user preferences (theme) belong to a specific `UserAccount`. Real authentication is
a separate, not-yet-built feature (an earlier cookie-auth attempt was built, then reverted). In the
meantime, "current user" is resolved by `CookieCurrentUserContext` (`JNotes.Web/`) from a plain,
unsigned, non-`HttpOnly` `actAsUserId` cookie set by an "act as" dropdown in the nav — trivially
spoofable, explicitly not a security boundary, purely so scoping can be exercised end to end. It's
meant to be deleted outright, not extended, once real login lands.

## Commands

```bash
# Build / test / format (from repo root)
dotnet build JNotes.slnx
dotnet test JNotes.slnx
dotnet test tests/JNotes.Tests --filter "FullyQualifiedName~ChecklistMarkdownParserTests"   # single test/class
dotnet format JNotes.slnx
dotnet format JNotes.slnx --verify-no-changes   # CI-style check, no changes written

# Run locally (needs .NET 10 SDK; stores data under src/JNotes.Web/App_Data/, gitignored)
dotnet run --project src/JNotes.Web
dotnet watch --project src/JNotes.Web run       # live-reloads C#, Razor, and Styles/*.scss

# EF Core migrations — startup-project is Core, NOT Web: JNotes.Web deliberately has no
# EF Core Design package reference (see Architecture below), so Web can't be the startup project.
dotnet ef migrations add <Name> --project src/JNotes.Core --startup-project src/JNotes.Core -o Data/Migrations
dotnet ef database update --project src/JNotes.Core --startup-project src/JNotes.Core
# `dotnet ef` is a local tool (.config/dotnet-tools.json) — run `dotnet tool restore` once after cloning.

# Docker (production-shaped: this is what Docker actually runs, not `dotnet run`)
docker compose up --build
```

## Architecture

**Three projects**, enforced one-directional dependency:
`JNotes.Web` (Blazor Server UI + composition root) → `JNotes.Core` (domain, storage, indexing,
EF Core) ← `JNotes.Tests` (xUnit, tests Core only).

**Web never touches the filesystem or EF Core/SQLite directly.** It only consumes four
interfaces from Core: `INoteService`, `ISettingsService`, `INoteIndexReconciler`, `IUserRepository`.
This is enforced at *build time*, not just by convention: `Microsoft.CodeAnalysis.BannedApiAnalyzers`
bans `System.IO.File`/`Directory`, `DbContext`, and `SqliteConnection` in `JNotes.Web`
(`src/JNotes.Web/BannedSymbols.txt`) — a real build error, not a warning, if violated. All of
Core's DI wiring lives in one place: `JNotes.Core/ServiceCollectionExtensions.AddJNotesCore` —
**with one deliberate exception**: `ICurrentUserContext` is declared in Core
(`JNotes.Core/Users/`) but registered by `JNotes.Web`'s `Program.cs` itself, because its only
implementation needs `IHttpContextAccessor`, an ASP.NET Core hosting type Core has never depended
on. If you add a service that genuinely needs a hosting-layer dependency, this is the pattern:
define the interface in Core, implement and register it in Web.

**Per-user scoping uses two different patterns, deliberately.** `INoteFileStore`,
`INoteIndexRepository`, and `INoteIndexReconciler` all take an **explicit** `Guid userId`
parameter. `NoteService`'s public methods (what Blazor pages call) take **no** `userId` parameter
at all — it injects `ICurrentUserContext` and threads `.UserId` through internally. The split is
deliberate, not inconsistent: `NoteIndexReconciler` has to operate on an *arbitrary* user (looped
over everyone at startup, since there's no "current user" before any request exists) as well as
"whoever clicked Rescan Now", so its collaborators need an explicit parameter either way — while
`NoteService` only ever means "the current user", so forcing every Blazor page to pass a `userId`
on every call would just be repetition (and a spoofing risk) with no benefit. If you add a new
Core service that only `NoteService`-like callers will use, prefer implicit `ICurrentUserContext`;
if a system-level/reconciliation-style caller might also need it, take an explicit parameter.

**`ICurrentUserContext` gotcha**: `CookieCurrentUserContext`'s constructor reads
`IHttpContextAccessor.HttpContext` — Blazor Server only guarantees that's non-null while the DI
scope backing a request/circuit-connect is still being built, not later during interactive
rendering, and scoped services are constructed lazily on first resolution. Every routed page must
resolve `NoteService` (or `ICurrentUserContext` directly) from its *first* lifecycle method
(`OnInitializedAsync`/`OnParametersSetAsync`) — every page in this app already does, since they
all load data on init, but a page that deferred its first `NoteService` call behind a later
interaction would silently get a wrong/default user.

`CookieCurrentUserContext` also never trusts the cookie's user id blindly — it checks
`IDefaultUserProvider.IsKnownUser(id)` and falls back to `DefaultUserId` if that user doesn't
exist. Without this, a stale cookie (e.g. left over from before a fresh-start database reset, or
just referencing a user someone deleted) would crash the circuit with an unhandled FK-constraint
`DbUpdateException` on the first write instead of degrading gracefully. `DefaultUserProvider`'s
known-ids cache is seeded from every existing user at startup (`HostExtensions.EnsureDefaultUserAsync`)
and kept in sync by `EfUserRepository.CreateUserAsync` — both writes stay in Core so Web never needs
its internal setters.

**Files are the source of truth; SQLite is a derived index.** Notes live at
`{NotesFolder}/{userId}/{noteId}.md` — one subfolder per user (`FileSystemNoteFileStore`); a
brand-new install bootstraps a default "admin" `UserAccount` if none exist yet
(`HostExtensions.EnsureDefaultUserAsync`) so the app is usable out of the box. This shapes `NoteService`
(`JNotes.Core/Notes/NoteService.cs`) and `NoteIndexReconciler`
(`JNotes.Core/Indexing/NoteIndexReconciler.cs`):
- Every write goes **file first, then index** (`NoteService`) — a crash mid-operation leaves an
  orphaned file the next reconciliation will discover and index, never an index row pointing at a
  file that was never written. Delete is file-first too, for the symmetric reason.
- `NoteIndexReconciler.RescanAsync()` diffs the notes folder against the index (added / updated —
  by comparing on-disk mtime vs. the last-indexed mtime, so unchanged files aren't re-parsed /
  removed / corrupted-front-matter-errors-recorded-without-deleting-the-row) and runs once at
  startup plus on demand from Settings → "Rescan Now". This is what makes hand-edited or
  externally-added/removed `.md` files converge correctly. There's deliberately no
  `FileSystemWatcher` — unreliable across Docker bind-mount backends.
- `ListAsync`/search reads only the SQLite index (fast, never touches disk); only `GetAsync`
  (opening one note) reads the actual file.

**Checklist toggling is line-based regex, not a Markdig AST round-trip**
(`JNotes.Core/Serialization/ChecklistMarkdownParser.cs`). Markdig is used only for read-only
preview rendering; re-emitting markdown from its parsed tree risks reformatting content outside
the edited line, so toggling a checkbox mutates only that line's bracket character in place,
leaving the rest of the file byte-identical. Don't route checklist edits through Markdig's AST.

**Front matter is hand-split, not Markdig-parsed**
(`JNotes.Core/Serialization/FrontMatterSplitter.cs`): a "does the file start with a `---`-delimited
block" string search, then YamlDotNet deserializes the YAML. `NoteMarkdownSerializer` maps
between that and the domain `Note` model (`Models/Note.cs`); timestamps are UTC everywhere in
storage (`JNotesOptions.TimeZoneId`/`AppTimeZoneProvider` in `Core/Time/` only affects *display*
in the Web UI, via the `TZ` env var).

**Tests** (`tests/JNotes.Tests/`) split into pure unit tests (`Serialization/`, `Time/`) and
integration tests (`Integration/`) that spin up the real `AddJNotesCore` DI graph against a real
temp-file SQLite database and temp folder (`CoreServicesFixture`) — not the EF Core in-memory
provider, specifically so SQLite-specific behavior (like the `ORDER BY DateTimeOffset` limitation,
worked around client-side wherever it comes up — `EfNoteIndexRepository.SearchAsync`,
`EfUserRepository.GetAllUsersAsync`) is actually exercised. `CoreServicesFixture` seeds two users
and registers a mutable `TestCurrentUserContext` (`ActAsUserA()`/`ActAsUserB()`) in place of the
real cookie-based one, so cross-user isolation can be asserted through `NoteService` itself, not
just by calling `INoteFileStore`/`INoteIndexRepository` with different explicit ids.

**Docker build gotcha:** the Dockerfile's multi-stage build restores against just the `.csproj`
files first (for layer caching) then copies full source — the publish step must **not** pass
`--no-restore`, or the static web assets manifest gets computed from that incomplete early
restore and silently ships without `_framework/blazor.web.js`, breaking all client-side
interactivity in the built image while every `curl`-based check still looks fine (no
`Accept-Encoding` header, so compressed-asset issues don't show either). If touching the
Dockerfile, verify with a real browser (or at minimum `curl -H "Accept-Encoding: gzip"`) against
the built image, not just a plain `curl` health check.
