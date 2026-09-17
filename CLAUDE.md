# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

JNotes is a self-hosted, multi-user note-taking app: Blazor Server (.NET 10) UI, notes stored as
portable markdown files on disk (each a readable `.md` with YAML front matter, one subfolder per
user), a Redis index over them for fast search — the index is a **derived cache**, never the
source of truth — and a SQLite database holding everything else (settings, user accounts). See
README.md for features, the note file format, and full configuration reference.

**Real authentication.** Every `UserAccount` has a `Role` (`Admin`/`Member`), an optional password
(`PasswordHash`, hashed with `PasswordHasher<UserAccount>`), an optional `Email`, and an optional
`OidcSubject` — an account needs at least a password or an email (for OIDC) to ever sign in. The
very first admin is created through a first-run `/setup` screen (gated by `ISetupStateProvider`,
seeded at startup by `HostExtensions.InitializeSetupStateAsync` from "does any `UserAccount` exist
yet") — there's no more auto-created default user. Admins manage everyone else from `/settings`'s
Administration settings section (markup-gated via `<AuthorizeView Roles="Admin">`, plus a
server-side role check before the page ever loads every account's data into a Member's circuit
state — see "Settings and user management share one page" below): create, reset a password, or
delete (see "Files are the source of truth" below for what deleting does). OIDC is supported
alongside password login and can
be configured via `OIDC_AUTHORITY`/`OIDC_CLIENT_ID`/`OIDC_CLIENT_SECRET`/`OIDC_DISPLAY_NAME` env
vars (`JNotes.Core.Users.OidcOptions`, read the same flat-env-var way as `NOTES_PATH`/`DB_PATH`/`TZ`);
password login itself can be turned off from Settings → Security once OIDC is configured
(`ISettingsService.SetPasswordAuthEnabledAsync` refuses to disable it otherwise — that would lock
everyone out). OIDC provisioning is admin-gated, not self-service: an admin pre-creates the account
with an `Email` set, and the first successful OIDC login for that email links it
(`OidcAccountLinker`, matches an **unlinked** account only — never relinks or auto-creates).

**Username/email format rules live in one place: `JNotes.Core.Users.UserValidation`**, a pure
static class (`IsLowercase`/`IsValidEmail`, the latter backed by a `[GeneratedRegex]` — the same
reasoning `ChecklistMarkdownParser`/`UserInitials` are plain static classes for). Enforced in two
layers, not one: `EfUserRepository.CreateUserAsync` throws `InvalidUsernameException`/
`InvalidEmailException` so *every* creation path gets the guarantee even if a future caller skips
the UI, while `/setup` and `/settings`'s "Add user" form also check `UserValidation` up front so
the error shows next to the form instead of round-tripping through an exception. Usernames must be
lowercase everywhere (the `Username` column's NOCASE collation already makes lookups
case-insensitive, but mixed-case *input* would still render inconsistently across the UI). Email
is mandatory at `/setup` (checked in `Setup.razor`'s `HandleSetupAsync`, not a repository-level
rule — `CreateUserAsync`'s `email` parameter stays nullable since the admin "Add user" form still
allows a password-only or email-only account) but only format-validated, not required, when adding
a user. The `/setup` email `<InputText>` deliberately has no `type="email"` — that would trigger
the browser's own native constraint validation, which intercepts form submission before
`HandleSetupAsync` (and thus `UserValidation`) ever runs, hiding JNotes's own error message behind
a native tooltip.

**Profile editing (`/profile`, `Settings.razor`'s Administration settings "Edit profile") has one
permission rule: Email is Admin-only, enforced in Core, not just the UI.**
`IUserManagementService.UpdateProfileAsync(targetUserId, requestingUserId, ...)` is the single
entry point both `Profile.razor` (self-edit — `targetUserId == requestingUserId`) and the
admin-only section of `Settings.razor` (editing someone else) call. FirstName/LastName
are always allowed for your own profile, or anyone's if you're an Admin; Email is refused
server-side (`InvalidOperationException`) for a Member changing their own — the UI disables the
input too, but that alone wouldn't stop a client that skipped it, so the same rule is checked again
in `UserManagementService` itself, the same belt-and-suspenders `SetPasswordAuthEnabledAsync`
already established. `NavMenu`'s "Signed in as" shows `FirstName ?? Username`, re-queried fresh on
every circuit init (not baked into the auth cookie's claims, which would go stale until next login)
— both `Profile.razor` and the admin edit modal force a full reload after saving so `NavMenu` (a
sibling component, not a descendant of either) picks up the change immediately instead of waiting
for the user's next navigation.

**Settings and user management share one page, split into two sections by role, not two routes.**
`Settings.razor` (`/settings`, `[Authorize]` — any signed-in user) has an "App settings" section
(Appearance/Account — just dark mode and your own password) everyone sees, and an "Administration
settings" section (Security, Storage, Index, the user table, add/edit/delete user) wrapped in
`<AuthorizeView Roles="Admin">` — the former standalone `/users` page
(`[Authorize(Policy = "AdminOnly")]`) folded in here rather than staying a separate route, so
there's one settings destination instead of two nav entries. Storage/Index (notes folder path,
note count, Rescan Now) moved into Administration settings too, even though they technically only
ever act on *your own* notes (`Reconciler.RescanAsync(CurrentUser.UserId)`) — a deliberate choice
to keep "App settings" down to the two things every Member actually needs. Markup gating alone
isn't enough for `OnInitializedAsync`, though: it only stops the Administration section from
*rendering*, not from being fetched into a Member's component state, so `Settings.razor` checks
`AuthenticationStateProvider`'s `IsInRole(nameof(UserRole.Admin))` before ever calling
`UserRepository.GetAllUsersAsync()`, `SettingsService.GetNotesFolderPathAsync()`, or
`RefreshCountAsync()` — otherwise that data would sit in a Member's circuit state even though it
never renders for them. The user-management mutations themselves (create/delete/reset-password)
still have no server-side role check in `IUserRepository`/`UserManagementService` beyond what
`UpdateProfileAsync`/`DeleteUserAsync` already do — same as the pre-existing `Security` section's
`TogglePasswordAuthAsync` — relying on Blazor Server's render-tree-scoped event dispatch (a
Member's circuit never receives an event handler id for a button `AuthorizeView` didn't render, so
there's nothing to invoke) rather than a route guard, now that the admin-only page-level
`[Authorize(Policy = "AdminOnly")]` guard is gone.

**`NavMenu`'s account widget is bottom-anchored and self-contained, not a separate "signed in as"
block.** `NavMenu.razor` renders as a single `d-flex flex-column h-100` wrapper (not two loose
sibling elements like before) specifically so `mt-auto` on the account section pushes it to the
bottom of whichever container hosts it — `MainLayout.razor`'s desktop `<aside>` (already
`d-flex flex-column`, height stretched from its own flex-row parent) and the mobile
`.offcanvas-body` (height comes from `.offcanvas`'s `flex-grow: 1`) both give the wrapper a
definite height to fill, so the same markup works in both places with no separate mobile styling.
The avatar's initials come from `JNotes.Core.Users.UserInitials.Compute` — a pure static class
(same reasoning as `NoteGrouping`/`ChecklistMarkdownParser`) that prefers
`FirstName[0] + LastName[0]` when both are set, else the first two letters of whichever single
name is available (first name, then username), always capped at 2 characters. Clicking the
avatar/name toggles a Bootstrap `collapse` (`#accountMenu`, the same `data-bs-toggle`/`.collapsed`
chevron-rotation pattern as `NoteList.razor`'s group headers) revealing "My Profile" and "Sign
out" — `/profile` no longer has its own top-level nav entry, since it lives here now alongside
sign-out instead of being a permanently-visible link.

**The sidebar's top section is ordered Search, New note, Notes, Settings, Trash, and its icons are
plain Unicode glyphs, not a vendored icon font.** No icon library is vendored (same reasoning as
Bootstrap being the only vendored UI dependency, under `wwwroot/lib/`) — instead, each label except
the search box is preceded by a `<span class="nav-icon">` wrapping a single monochrome dingbat/
symbol character (✎ new note, ▤ notes, ⚙ settings, ⌫ trash), the same "plain text glyph, no new
dependency" precedent `NoteList.razor`'s `▸` group-header chevron and `NavMenu`'s own
`account-menu-chevron` already established. The gear and pencil glyphs append the `︎` text-
presentation variation selector to force monochrome/text rendering instead of color emoji — the
other two symbols (▤, ⌫) live in Unicode blocks with no emoji presentation at all, so they render
monochrome by default with no selector needed. `.nav-icon` (`wwwroot/css/nav-menu.css`) is a fixed-
width inline-block purely so labels stay left-aligned regardless of each glyph's natural width. The
search `<input>` is deliberately excluded from icon treatment (the placeholder text "Search" already
signals its purpose) and clears itself via `@onclick` whenever it's clicked, whether or not it
currently holds a previous query — so starting a new search never requires manually clearing the
box first.

**"New note" lives in `NavMenu.razor`, not `NoteList.razor` — it needs to work from any page, not
just `/`.** The create-and-navigate logic (`INoteService.CreateAsync` then
`NavigationManager.NavigateTo($"/notes/{note.Id}")`) moved there verbatim; `NoteList.razor` no
longer injects `NavigationManager` at all, since nothing else in that page needed it. The button
is wrapped in the same unqualified `<AuthorizeView>` as the account widget below it (not a manual
`IsAuthenticated` check) — that's deliberate here, unlike the avatar/initials lookup in
`OnInitializedAsync`: `NavMenu` renders for `NotFound`/`Error` too (both `[AllowAnonymous]`, both
using `MainLayout`), and Blazor Server never hands an anonymous circuit a valid event-handler id
for markup `AuthorizeView` didn't render, so there's nothing for a signed-out visitor to click —
the same render-tree-scoped dispatch reasoning already relied on for the Administration settings
mutations.

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
dotnet watch --project src/JNotes.Web run       # live-reloads C#, Razor, and wwwroot/css/*.css

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
EF Core, Redis) ← `JNotes.Tests` (xUnit, tests Core only).

**Web never touches the filesystem, EF Core/SQLite, or Redis directly.** It only consumes
interfaces from Core: `INoteService`, `ISettingsService`, `INoteIndexReconciler`, `IUserRepository`,
`IUserManagementService`. This is enforced at *build time*, not just by convention:
`Microsoft.CodeAnalysis.BannedApiAnalyzers` bans `System.IO.File`/`Directory`, `DbContext`,
`SqliteConnection`, and `StackExchange.Redis`'s `IConnectionMultiplexer`/`IDatabase` in `JNotes.Web`
(`src/JNotes.Web/BannedSymbols.txt`) — a real build error, not a warning, if violated. All of
Core's DI wiring lives in one place:
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

**Dark is the app-wide default theme; `/login` and `/setup` are always dark, never per-user.**
`SettingsService.GetThemeAsync` returns `AppTheme.Dark` when a user has no `UserSetting` row yet
(rather than `AppTheme.Light`) — a user who's never touched the Appearance toggle gets dark until
they explicitly pick light, not the other way around. `App.razor`'s `OnInitializedAsync` sets the
`<html data-bs-theme>` prerendered into the initial response (see "Render-mode split" above for why
this runs during the HTTP response rather than after the circuit connects): `AppTheme.Dark` for any
unauthenticated request (`/login`, `/setup`, or anything before `ICurrentUserContext` is usable) —
this is a fixed choice, not that visitor's eventual account's saved preference, since there's no
signed-in user yet to have one. A user who's explicitly set *light* still sees a dark login screen
right up until the moment they sign in, then sees their own light preference immediately after —
confirmed by toggling light as an existing user, signing out (still dark), and back in (light again).

**Data Protection keys are persisted next to the database** (`{DB_PATH's directory}/keys/`,
`Program.cs`), not left at the default (the container's ephemeral filesystem). Those keys are what
sign/decrypt the auth cookie — left at the default, every restart regenerates them and silently
signs everyone out, which ASP.NET Core would otherwise only warn about in the logs rather than fail
loudly on. Verified by restarting the Docker container mid-session and confirming the browser
stayed signed in.

**Files are the source of truth; Redis is a derived index.** Notes live at
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
  file that was never written. Trashing is file-first too, for the symmetric reason.
- `NoteIndexReconciler.RescanAsync()` diffs the notes folder against the index (added / updated —
  by comparing on-disk mtime vs. the last-indexed mtime, so unchanged files aren't re-parsed /
  removed / corrupted-front-matter-errors-recorded-without-deleting-the-row) and runs once at
  startup plus on demand from Settings → "Rescan Now". This is what makes hand-edited or
  externally-added/removed `.md` files converge correctly. There's deliberately no
  `FileSystemWatcher` — unreliable across Docker bind-mount backends.
- `ListAsync`/search reads only the Redis index (fast, never touches disk); only `GetAsync`
  (opening one note) reads the actual file.

**"Delete" moves a note to the trash, never deletes it outright — permanent deletion only happens
from the Trash page.** `INoteFileStore.TrashAsync` moves a note's file to
`{NotesFolder}/{userId}/trash/{noteId}.md`, sibling files `RestoreAsync`/`PurgeAsync` move it back
out / delete it for good, and `ListTrashedIdsAsync` lists what's there — all nested *inside* each
user's own folder, not a sibling of it like the whole-account `deleted/{userId}/` archive above.
That nesting is what makes the trash invisible to everything that doesn't know about it for free:
`ListIdsAsync`'s `Directory.EnumerateFiles(folder, "*.md")` is non-recursive, so a trashed note
never appears to `NoteIndexReconciler` or a live listing, with zero special-casing needed there.
`NoteService.TrashAsync` deletes the index row immediately (same as the old hard-delete did) rather
than waiting on reconciliation, since a trashed note disappearing from `ListIdsAsync` would
otherwise look identical to "removed externally" and only get caught on the next rescan.
**Deliberately no Redis index for the trash itself** — `NoteService.ListTrashAsync` reads
straight off the trash folder (parses each file's front matter, tolerating a `NoteNotFoundException`
race if something's purged mid-listing or a `NoteParseException` on a corrupted one by leaving it
out rather than failing the whole page) — this app's usual "not worth a schema for a rarely-large,
rarely-viewed list" trade-off. `TrashAsync` doesn't touch mtime, but a same-volume file move alone
wouldn't either, so it's stamped explicitly with `File.SetLastWriteTimeUtc` right after the move —
that stamp is what `GetTrashedAtUtcAsync` (and the Trash page's "Deleted …" timestamps) actually
reports as "when this was deleted". `RestoreAsync` re-adds the index row by reading the note back
from its new (live) location and reindexing it exactly like any other note — no special "was
trashed" state carried anywhere once it's back.

**Autosave has two persistence scopes, and the cheaper one is deliberately incomplete.**
`NoteService.AutosaveAsync(note, NoteSaveScope scope)` (`JNotes.Core/Notes/`) is what
`NoteEditor.razor`'s debounced autosave, checklist toggling, and `UpdateAsync` all funnel through —
there's no explicit Save button in the editor at all; every edit reaches disk on its own debounce
timer (see below), the same "trust autosave" reasoning the "New note" sidebar move already leans
on. `NoteSaveScope.ContentOnly` (body edits, checklist toggles) writes only the file — never the
index, since body isn't indexed and title/tags haven't changed. This is intentionally the same
"file changed, index doesn't know yet" state a hand-edited file is in, so `NoteIndexReconciler`
(already described above) catches it up for free the next time it runs — no separate sync path was
built for autosave. The visible trade-off: a note's position in the `UpdatedAt`-sorted list doesn't
move while you're only editing its body, until a `Metadata`-scope save (title/tag edit, or leaving
the page — see below) or a reconciliation pass. `NoteSaveScope.Metadata` (title or tag edits)
writes the file *and* reindexes immediately — nothing narrower was needed since
`RedisNoteIndexRepository.UpsertAsync` already replaces the note's whole hash, tags included, in
one call.

`NoteEditor.razor`'s autosave: two independent debounces (`AutosaveDebouncer`,
`JNotes.Web/Components/Notes/`) — content and metadata edits shouldn't wait on each other — but
since both ultimately write the *same* file, the actual save (not the debounce delay) is serialized
through one `SemaphoreSlim(1, 1)` per open editor, so a title save can never race a content
autosave's file write. `DeleteAsync` cancels pending debounces and waits on that same gate before
deleting, so a delete can't race an in-flight autosave either. `IAsyncDisposable.DisposeAsync`
does one best-effort `Metadata`-scope flush if anything's still unsaved when the component tears
down (navigating away right after typing), so the last stretch of edits and the index both catch
up together rather than waiting for the next reconciliation.

**Search lives in the sidebar (`NavMenu.razor`), not on the notes list page — it's a single
free-text box with no type/tag options, matching title, body, and tags all at once.** Pressing
Enter navigates to `/?q=<text>` (URL-encoded, empty text just navigates to `/`); `NoteList.razor`
reads that back via `[SupplyParameterFromQuery(Name = "q")]` and refreshes from
`OnParametersSetAsync` (not `OnInitializedAsync` — the same page instance is reused across repeat
searches, since only the query string changes, so it has to react to parameter updates, not just
run once on first load; `NoteEditor.razor`'s `Id` route parameter is the same pattern for the same
reason). Routing search through the URL rather than component-local state is what makes it work
from *any* page — the search box itself lives in the persistent sidebar layout, so typing while on
`/settings` or inside a note still lands you back on `/` with results, without the search box
needing to know anything about the page it's currently rendered alongside. `NoteList.razor` keeps
its own Type/Tag filter dropdowns (unrelated to free-text search, still page-local) and shows a
"Search results for … · Clear" line above them when `Query` is set.

Search covers **body content**, not just title: `NoteIndexEntry.Body` is a full copy of the note's
body, kept in sync by `NoteService`'s existing file-first-then-index write path and
`NoteIndexReconciler` exactly like `Title` already is — no new sync mechanism, just one more field
on entries those two already build. It inherits the same `ContentOnly`-autosave staleness
trade-off documented above: a body edit alone doesn't update the indexed `Body` until a
`Metadata`-scope save or a reconciliation pass, so search-by-content can lag a content-only
autosave the same way list ordering already does. `RedisNoteIndexRepository.SearchAsync` loads a
user's notes (a Redis hash per note, tags inline as a JSON field — no normalized tag table the way
SQLite had one, since nothing benefits from an indexed join once matching happens in-memory) and
does one case-insensitive `Contains` check per candidate field (title, body, each tag) rather than
a SQL `LIKE` — same matching semantics, just evaluated client-side.

**The default notes view groups by relative date, not just sorts.** `NoteGrouping.GroupByRelativeDate`
(`JNotes.Core/Notes/`) buckets an already-`UpdatedAt`-descending list (`RedisNoteIndexRepository.SearchAsync`
already sorts; grouping never re-sorts) into Today/Yesterday/This week/Last week/This month/Previous,
skipping empty buckets, keeping each bucket's notes in whatever order they arrived. It's a plain
static class with no DI, like `ChecklistMarkdownParser` — pure bucketing logic with no clock/timezone
reads of its own, so `NoteList.razor` passes in `DateTimeOffset.UtcNow` and
`IAppTimeZoneProvider.TimeZone` explicitly, keeping it deterministic and unit-testable
(`tests/JNotes.Tests/Notes/NoteGroupingTests.cs`). Both the "now" and each note's `UpdatedAt` are
converted to the same time zone before comparing dates — comparing raw UTC dates would misbucket a
note whose local calendar day differs from its UTC one. Bucket boundaries are checked in strict
priority order (today → yesterday → this week → last week → this month → previous) rather than as
independent ranges, since `monthStart` can chronologically fall *after* `lastWeekStart` near the start
of a calendar month. `NoteList.razor`'s sections are collapsible via Bootstrap's native
`data-bs-toggle="collapse"` — no new JS or C# toggle state, just CSS (`wwwroot/css/note-list.css`)
that rotates the chevron off Bootstrap's own `.collapsed` class.

**`ChecklistNoteEditor` and `TextNoteEditor` share the same toggleable-side-panel layout.**
(`JNotes.Web/Components/Notes/`) Both use a plain `bool` field (`_showRawMarkdown` /
`_showPreview`) and a `row g-3` / `col-12 col-lg-6` split — the always-visible editable view
(checklist checkboxes / the body `<textarea>`) on the left, the togglable secondary view (raw
markdown `<textarea>` / rendered `MarkdownPreview`) on the right, `col-12` full-width on the left
when the right side is hidden. `ChecklistNoteEditor` used to hide its raw-markdown `<textarea>` in
a full-width Bootstrap `.collapse` below the checklist instead — replaced with this plain-C#
toggle specifically to match `TextNoteEditor`'s side-by-side shape, since a Bootstrap `.collapse`
can't drive the left column's Blazor-rendered `col-12`/`col-lg-6` class switch on its own (that
state lives in the DOM via Bootstrap's JS, not in the component). `MarkdownToolbar` (bound to the
raw-markdown `<textarea>`'s `ElementReference`) only renders while that panel is open — there's
nothing for it to act on while it's hidden.

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
a real temp-file SQLite database, a real Redis instance (see "Tests need a real Redis" below), and
a temp folder — not the EF Core in-memory provider, specifically so SQLite-specific behavior (like
the `ORDER BY DateTimeOffset` limitation, worked around client-side wherever it comes up —
`EfUserRepository.GetAllUsersAsync`) is actually exercised. `CoreServicesFixture`
(`Integration/`) seeds two users (each with a password, so it stays usable for any future
auth-aware test) and registers a mutable `TestCurrentUserContext` (`ActAsUserA()`/`ActAsUserB()`)
in place of the real authenticated one, so cross-user isolation can be asserted through
`NoteService` itself, not just by calling `INoteFileStore`/`INoteIndexRepository` with different
explicit ids. `Users/EfUserRepositoryTests.cs` and `Users/UserManagementServiceTests.cs` build
their own `AddJNotesCore` provider (not `CoreServicesFixture`) since they need direct access to
things it doesn't expose (`ISetupStateProvider`, `IUserManagementService`) — the latter covers the
file-move-before-DB-delete soft-delete design, including idempotency on a retried delete.

**CSS is plain, hand-authored, and lives directly under `wwwroot/css/` — no Sass, no build step.**
(This app briefly used `AspNetCore.SassCompiler`/`.scss` under `Styles/`; that's been reverted.)
`variables.css` holds app-wide custom properties (`:root { --note-editor-height: 60vh; ... }`) in
place of Sass `$variables`; `note-editor.css`/`note-list.css`/`nav-menu.css` use native CSS nesting
(`&`) in place of Sass nesting; `app.css` is the entry point (linked from `App.razor`) and pulls the
rest together with native `@import` in place of Sass `@use` — same one-file-per-concern shape as
before, just with nothing to compile. Since these are ordinary static files under `wwwroot`, `dotnet
watch` and `app.MapStaticAssets()` (`Program.cs`) handle them exactly like any other asset — no
special-cased build step or DI registration needed, unlike the Sass compiler this replaced. Add a
new concern as its own `.css` file and `@import` it from `app.css`, the same way `note-list.css`
and `nav-menu.css` were added.

**Docker build gotcha:** the Dockerfile's multi-stage build restores against just the `.csproj`
files first (for layer caching) then copies full source — the publish step must **not** pass
`--no-restore`, or the static web assets manifest gets computed from that incomplete early
restore and silently ships without `_framework/blazor.web.js`, breaking all client-side
interactivity in the built image while every `curl`-based check still looks fine (no
`Accept-Encoding` header, so compressed-asset issues don't show either). If touching the
Dockerfile, verify with a real browser (or at minimum `curl -H "Accept-Encoding: gzip"`) against
the built image, not just a plain `curl` health check.
