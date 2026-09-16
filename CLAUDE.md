# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

JNotes is a self-hosted, multi-user note-taking app: Blazor Server (.NET 10) UI, notes stored as
portable markdown files on disk (each a readable `.md` with YAML front matter, one subfolder per
user), and a SQLite database that indexes them for fast search — the index is a **derived cache**,
never the source of truth. See README.md for features, the note file format, and full
configuration reference.

**Real authentication.** Every `UserAccount` has a `Role` (`Admin`/`Member`), an optional password
(`PasswordHash`, hashed with `PasswordHasher<UserAccount>`), an optional `Email`, and an optional
`OidcSubject` — an account needs at least a password or an email (for OIDC) to ever sign in. The
very first admin is created through a first-run `/setup` screen (gated by `ISetupStateProvider`,
seeded at startup by `HostExtensions.InitializeSetupStateAsync` from "does any `UserAccount` exist
yet") — there's no more auto-created default user. Admins manage everyone else from `/users`
(`[Authorize(Policy = "AdminOnly")]`): create, reset a password, or delete (see "Files are the
source of truth" below for what deleting does). OIDC is supported alongside password login and can
be configured via `OIDC_AUTHORITY`/`OIDC_CLIENT_ID`/`OIDC_CLIENT_SECRET`/`OIDC_DISPLAY_NAME` env
vars (`JNotes.Core.Users.OidcOptions`, read the same flat-env-var way as `NOTES_PATH`/`DB_PATH`/`TZ`);
password login itself can be turned off from Settings → Security once OIDC is configured
(`ISettingsService.SetPasswordAuthEnabledAsync` refuses to disable it otherwise — that would lock
everyone out). OIDC provisioning is admin-gated, not self-service: an admin pre-creates the account
with an `Email` set, and the first successful OIDC login for that email links it
(`OidcAccountLinker`, matches an **unlinked** account only — never relinks or auto-creates).

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

**Web never touches the filesystem or EF Core/SQLite directly.** It only consumes interfaces from
Core: `INoteService`, `ISettingsService`, `INoteIndexReconciler`, `IUserRepository`,
`IUserManagementService`. This is enforced at *build time*, not just by convention:
`Microsoft.CodeAnalysis.BannedApiAnalyzers` bans `System.IO.File`/`Directory`, `DbContext`, and
`SqliteConnection` in `JNotes.Web` (`src/JNotes.Web/BannedSymbols.txt`) — a real build error, not a
warning, if violated. All of Core's DI wiring lives in one place:
`JNotes.Core/ServiceCollectionExtensions.AddJNotesCore` — **with one deliberate exception**:
`ICurrentUserContext` is declared in Core (`JNotes.Core/Users/`) but registered by `JNotes.Web`'s
`Program.cs` itself, because its only implementation needs `IHttpContextAccessor`, an ASP.NET Core
hosting type Core has never depended on. Web's own auth wiring — cookie/OIDC handler registration,
`[Authorize]`/`AuthorizeRouteView`, sign-in/out — lives entirely in `JNotes.Web/` for the same
reason (see `JNotes.Web/Auth/`). If you add a service that genuinely needs a hosting-layer
dependency, this is the pattern: define the interface in Core, implement and register it in Web.

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

**`ICurrentUserContext` gotcha**: `AuthenticatedCurrentUserContext`'s constructor reads
`IHttpContextAccessor.HttpContext.User` — Blazor Server only guarantees `HttpContext` is non-null
while the DI scope backing a request/circuit-connect is still being built, not later during
interactive rendering, and scoped services are constructed lazily on first resolution. Every routed
page must resolve `NoteService` (or `ICurrentUserContext` directly) from its *first* lifecycle
method (`OnInitializedAsync`/`OnParametersSetAsync`) — every page in this app already does, since
they all load data on init, but a page that deferred its first `NoteService` call behind a later
interaction would silently get a wrong/default user (or, now that it throws on an unauthenticated
principal instead of falling back, an exception).

**Two revalidation mechanisms, not one — both needed.** A signed-in user can be deleted or have
their role changed while they still hold a valid session cookie or an already-open circuit.
`CookiePrincipalValidator` (`JNotes.Web/Auth/`, wired as `CookieAuthenticationEvents.OnValidatePrincipal`)
only fires when the cookie middleware actually runs on an HTTP request — circuit connect, full page
reload, SignalR reconnect — never again for the rest of an already-open circuit sitting on a live
SignalR connection with no further requests. `JNotesRevalidatingAuthenticationStateProvider`
(`RevalidatingServerAuthenticationStateProvider`, from `Microsoft.AspNetCore.Components.Server` —
not `.Authorization`, easy to get wrong) covers that gap: it runs on its own timer *inside* the
circuit. Both are registered; either one invalidating forces `Routes.razor`'s
`AuthorizeRouteView.NotAuthorized` to redirect to `/login`.

**Render-mode split for `/login` and `/setup`.** `HttpContext.SignInAsync`/`SignOutAsync` write a
`Set-Cookie` header, which only works while the response is still being built — true for a
statically-rendered page handling a form POST, not from inside an already-connected
`InteractiveServer` circuit (headers long sent). `App.razor` computes render mode per-request from
`HttpContext` (available there as a cascading parameter, since `App.razor` renders as part of the
initial HTTP response before any circuit exists): `null` (static SSR) for `/login`/`/setup`,
`InteractiveServer` for everything else, passed to `<Routes @rendermode="RenderModeForPage" />`.
Login/logout/the OIDC challenge are plain form posts / minimal API endpoints in `Program.cs` for
the same reason — never an `@onclick` inside an interactive component.

**Data Protection keys are persisted next to the database** (`{DB_PATH's directory}/keys/`,
`Program.cs`), not left at the default (the container's ephemeral filesystem). Those keys are what
sign/decrypt the auth cookie — left at the default, every restart regenerates them and silently
signs everyone out, which ASP.NET Core would otherwise only warn about in the logs rather than fail
loudly on. Verified by restarting the Docker container mid-session and confirming the browser
stayed signed in.

**Files are the source of truth; SQLite is a derived index.** Notes live at
`{NotesFolder}/{userId}/{noteId}.md` — one subfolder per user (`FileSystemNoteFileStore`); a
brand-new install has no users at all until the `/setup` screen creates the first admin (see "Real
authentication" above). Deleting a user (`IUserManagementService.DeleteUserAsync`) never deletes
their notes: `INoteFileStore.ArchiveUserFolderAsync` moves (not deletes) their folder to
`{NotesFolder}/deleted/{userId}/` *before* the account row is hard-deleted — file-move first, DB
delete second, so a failure at either step leaves the account either fully intact or fully deleted,
never an index-less orphaned file tree. This shapes `NoteService`
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

**Autosave has two persistence scopes, and the cheaper one is deliberately incomplete.**
`NoteService.AutosaveAsync(note, NoteSaveScope scope)` (`JNotes.Core/Notes/`) is what
`NoteEditor.razor`'s debounced autosave, the explicit Save button, checklist toggling, and
`UpdateAsync` all funnel through. `NoteSaveScope.ContentOnly` (body edits, checklist toggles) writes
only the file — never the index, since body isn't indexed and title/tags haven't changed. This is
intentionally the same "file changed, index doesn't know yet" state a hand-edited file is in, so
`NoteIndexReconciler` (already described above) catches it up for free the next time it runs — no
separate sync path was built for autosave. The visible trade-off: a note's position in the
`UpdatedAt`-sorted list doesn't move while you're only editing its body, until a `Metadata`-scope
save (title/tag edit, explicit Save, or leaving the page — see below) or a reconciliation pass.
`NoteSaveScope.Metadata` (title or tag edits) writes the file *and* reindexes immediately — nothing
narrower was needed since `EfNoteIndexRepository.UpsertAsync` already replaces both the
`NoteIndexEntry` row and the note's `NoteTag` rows in one call.

`NoteEditor.razor`'s autosave: two independent debounces (`AutosaveDebouncer`,
`JNotes.Web/Components/Notes/`) — content and metadata edits shouldn't wait on each other — but
since both ultimately write the *same* file, the actual save (not the debounce delay) is serialized
through one `SemaphoreSlim(1, 1)` per open editor, so a title save can never race a content
autosave's file write. `DeleteAsync` cancels pending debounces and waits on that same gate before
deleting, so a delete can't race an in-flight autosave either. `IAsyncDisposable.DisposeAsync`
does one best-effort `Metadata`-scope flush if anything's still unsaved when the component tears
down (navigating away right after typing), so the last stretch of edits and the index both catch
up together rather than waiting for the next reconciliation.

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
integration tests (`Integration/`, `Users/`) that spin up the real `AddJNotesCore` DI graph against
a real temp-file SQLite database and temp folder — not the EF Core in-memory provider, specifically
so SQLite-specific behavior (like the `ORDER BY DateTimeOffset` limitation, worked around
client-side wherever it comes up — `EfNoteIndexRepository.SearchAsync`,
`EfUserRepository.GetAllUsersAsync`) is actually exercised. `CoreServicesFixture`
(`Integration/`) seeds two users (each with a password, so it stays usable for any future
auth-aware test) and registers a mutable `TestCurrentUserContext` (`ActAsUserA()`/`ActAsUserB()`)
in place of the real authenticated one, so cross-user isolation can be asserted through
`NoteService` itself, not just by calling `INoteFileStore`/`INoteIndexRepository` with different
explicit ids. `Users/EfUserRepositoryTests.cs` and `Users/UserManagementServiceTests.cs` build
their own `AddJNotesCore` provider (not `CoreServicesFixture`) since they need direct access to
things it doesn't expose (`ISetupStateProvider`, `IUserManagementService`) — the latter covers the
file-move-before-DB-delete soft-delete design, including idempotency on a retried delete.

**Docker build gotcha:** the Dockerfile's multi-stage build restores against just the `.csproj`
files first (for layer caching) then copies full source — the publish step must **not** pass
`--no-restore`, or the static web assets manifest gets computed from that incomplete early
restore and silently ships without `_framework/blazor.web.js`, breaking all client-side
interactivity in the built image while every `curl`-based check still looks fine (no
`Accept-Encoding` header, so compressed-asset issues don't show either). If touching the
Dockerfile, verify with a real browser (or at minimum `curl -H "Accept-Encoding: gzip"`) against
the built image, not just a plain `curl` health check.
