# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

JNotes is a self-hosted, multi-user note-taking app: Blazor Server (.NET 10) UI built on MudBlazor, notes stored as
portable markdown files on disk (each a readable `.md` with YAML front matter, one subfolder per
user), and a single PostgreSQL database holding user accounts and settings plus a note index over
the files for fast search — the index tables are a **derived cache**, never the source of truth;
the markdown files are. See README.md for features, the note file format, and the configuration
reference.

## Commands

```bash
# Build / test / format (from repo root)
dotnet build JNotes.slnx
dotnet test JNotes.slnx                          # integration tests need a real PostgreSQL (see Tests)
dotnet test tests/JNotes.Tests --filter "FullyQualifiedName~ChecklistMarkdownParserTests"   # single class
dotnet format JNotes.slnx
dotnet format JNotes.slnx --verify-no-changes    # CI-style check

# Run locally (needs .NET 10 SDK; data under src/JNotes.Web/App_Data/, gitignored)
dotnet run --project src/JNotes.Web
dotnet watch --project src/JNotes.Web run       # live-reloads C#, Razor, and wwwroot/css/*.css
# Both use the FIRST launch profile (`http`, Properties/launchSettings.json), which points at the real
# `jnotes` database and App_Data/notes — see "Tests" for throwaway-database rules.

# EF Core migrations — startup project is Core, NOT Web: Web deliberately has no EF Core Design reference.
# The schema is a single `InitialCreate` baseline (nothing has shipped yet); a model change made after it
# gets a new migration like normal, and the baseline is regenerated only by deliberately resetting them.
dotnet ef migrations add <Name> --project src/JNotes.Core --startup-project src/JNotes.Core -o Data/Migrations
dotnet ef database update --project src/JNotes.Core --startup-project src/JNotes.Core
# `dotnet ef` is a local tool (.config/dotnet-tools.json) — run `dotnet tool restore` once after cloning.

docker compose up --build   # production-shaped: what Docker actually runs, not `dotnet run`
```

## Architecture

**Three projects**, one-directional dependency: `JNotes.Web` (Blazor Server UI + composition root)
→ `JNotes.Core` (domain, storage, indexing, EF Core) ← `JNotes.Tests` (xUnit, tests Core only).

**Web never touches the filesystem or EF Core/PostgreSQL directly** — only Core's interfaces
(`INoteService`, `ISettingsService`, `INoteIndexReconciler`, `IUserRepository`,
`IUserManagementService`, `IImageService`, `IImageCleanupService`, `INoteExportService`, `INoteAutoTagger`, `INoteSharingService`). Enforced at build time: `Microsoft.CodeAnalysis.BannedApiAnalyzers`
(`src/JNotes.Web/BannedSymbols.txt`) bans `System.IO.File`/`Directory`, `DbContext`, and Npgsql's
`NpgsqlConnection`/`NpgsqlDataSource` in Web. Pure static Core
helpers (`ChecklistMarkdownParser`, `NoteGrouping`, `UserInitials`, `UserValidation`) are fine to
call from Web.

All Core DI wiring is in `JNotes.Core/ServiceCollectionExtensions.AddJNotesCore`, **except**
`ICurrentUserContext` and `IPdfExportAssetSource`: declared in Core, registered in Web's `Program.cs`, because
`ICurrentUserContext`'s only implementation needs `IHttpContextAccessor` (an ASP.NET Core hosting type Core
doesn't depend on) and the latter's files (mermaid and Roboto) are Web's `wwwroot`, read through
`IWebHostEnvironment.WebRootFileProvider`.
Web's auth wiring (cookie/OIDC handlers, `[Authorize]`/`AuthorizeRouteView`, sign-in/out) lives in
`JNotes.Web/Auth/` for the same reason. Any service needing a hosting-layer dependency follows
this pattern: interface in Core, implementation + registration in Web.

**Configuration is environment variables only.** There is no `appsettings.json`.
`JNotes.Web/Hosting/EnvironmentOnlyConfiguration.UseEnvironmentVariablesOnly` (called right after
`WebApplication.CreateBuilder()`, which is deliberately given no `args`) removes every
`FileConfigurationSource` (JSON files, user secrets) and `CommandLineConfigurationSource`, so a stray or
re-added `appsettings.json` (or a stale copy in `bin/`) is ignored. It removes those sources rather than
calling `Sources.Clear()`: `CreateBuilder` also registers the `DOTNET_`/`ASPNETCORE_`-prefixed environment
providers that supply `urls`/`http_ports`, and clearing them would break Kestrel binding in Docker. The
Logging defaults (`Default=Information`, `Microsoft.AspNetCore=Warning`) live there too, as the
lowest-priority in-memory source (not `AddFilter`, which would beat an env override). Local-dev values live
in `src/JNotes.Web/Properties/launchSettings.json` (committed; the debug DB password is already public; no
secrets — `OIDC_*` is absent so it comes from the developer's own environment; `OLLAMA_URL` is present and
empty on purpose). The `http` and `https` profiles' env blocks are duplicated (JSON can't share them) and
must be edited together. `POSTGRES_HOST`/`POSTGRES_DB`/`POSTGRES_USER` are required
(`JNotesOptions.GetMissingRequiredPostgresSettings`; the fail-fast error names them); blank counts as unset
for the string settings. `JNotesDbContextFactory` (design-time, `dotnet ef`) reads `POSTGRES_*` straight
from the environment and is unaffected.

**Per-user scoping uses two patterns, deliberately.** `INoteFileStore`, `INoteIndexRepository`, and
`INoteIndexReconciler` take an **explicit** `Guid userId` — the reconciler must run for arbitrary
users (looped at startup, before any request exists) as well as "whoever clicked Rescan Now".
`NoteService`'s public methods take **no** `userId`; it injects `ICurrentUserContext` internally, so
pages don't repeat (or spoof) it. New Core services: implicit `ICurrentUserContext` if only
`NoteService`-style callers use them; explicit parameter if a system-level caller might too.

**`ICurrentUserContext` gotcha:** `AuthenticatedCurrentUserContext` reads
`IHttpContextAccessor.HttpContext.User` in its constructor, but Blazor Server only guarantees a
non-null `HttpContext` while the DI scope is being built, and scoped services construct lazily.
So every routed page must resolve `NoteService` (or `ICurrentUserContext`) from its *first*
lifecycle method (`OnInitializedAsync`/`OnParametersSetAsync`); deferring it behind a later
interaction gets a wrong user or, since it throws on an unauthenticated principal, an exception.
`MainLayout` also injects `INoteService` (constructed at circuit start alongside `ICurrentUserContext`)
for the sidebar's Recent list, and only *calls* it after an `IsAuthenticated` check.

**Two revalidation mechanisms — both needed.** A user can be deleted or change role while holding
a valid cookie or open circuit. `CookiePrincipalValidator` (`OnValidatePrincipal`) only fires when
the cookie middleware runs on an HTTP request (circuit connect, reload, SignalR reconnect), never
for an already-open circuit. `JNotesRevalidatingAuthenticationStateProvider`
(`RevalidatingServerAuthenticationStateProvider` from `Microsoft.AspNetCore.Components.Server`, not
`.Authorization`) covers that gap on a timer inside the circuit. Either invalidating sends
`Routes.razor`'s `AuthorizeRouteView.NotAuthorized` to `/login`.

**Render-mode split for `/login` and `/setup`.** `SignInAsync`/`SignOutAsync` write `Set-Cookie`,
which only works while the response is still being built — a static-SSR form POST, not an
already-connected `InteractiveServer` circuit. `App.razor` picks render mode per request from
`HttpContext`: `null` (static SSR) for `/login`/`/setup`, `InteractiveServer` otherwise. Login,
logout, and the OIDC challenge are therefore plain form posts / minimal-API endpoints in
`Program.cs`, never an `@onclick` in an interactive component.

**Theme.** The live app is dark-only: `MainLayout` and `AuthLayout` render `MudThemeProvider
IsDarkMode="true"`, and `App.razor` emits a literal `<html data-theme="dark">` (read by
`mermaid-preview.js`). There is no per-user theme setting. The `UserSettings` table is kept, unused,
for future per-user preferences. PDF export is light via its own `note-pdf.css`, independent of this.

**UI is MudBlazor** (NuGet `MudBlazor`, `AddMudServices()` in `Program.cs`; Roboto is vendored under
`wwwroot/lib/roboto` and `fonts.css`, so there are no external requests). Things that aren't obvious:
- **One theme, one owner.** `Theme/JNotesTheme.cs` is the shared `MudTheme` (palette, Roboto stack).
  `MainLayout.razor` hosts `MudThemeProvider`/`MudPopoverProvider`/`MudDialogProvider`/
  `MudSnackbarProvider`.
- **Palette variables exist only at runtime.** `MudBlazor.min.css` uses `var(--mud-palette-*)` but never
  defines them; `MudThemeProvider` emits them. Our own CSS reads them (`--mud-palette-lines-default`,
  `--mud-palette-text-secondary`, …). `--jnotes-font-mono` (in `variables.css`) is the one mono stack.
- **Static-SSR pages can't contain interactive MudBlazor components.** `/login` and `/setup` (and
  `AuthLayout`) are static so `SignInAsync` can write `Set-Cookie`; a `MudTextField`/`MudButton` there
  would be dead. They use plain `<form>`/`<input>` styled with `mud-*` classes and `auth.css`.
  `MudThemeProvider` is fine there (it only emits the CSS variables). Rendering-only components
  (`MudText`) work anywhere.
- **`MudBlazor` 9's API differs from 7/8 in places** (e.g. `MudFileUpload` has `CustomContent`, not
  `ActivatorContent`). Check parameter names against `~/.nuget/packages/mudblazor/<ver>/lib/net10.0/MudBlazor.xml`,
  not memory.
- **MudBlazor inputs keep their own state.** After a server-side refusal, a `MudSwitch`/`MudCheckBox`
  stays where the user clicked unless it's rebuilt — bump an `@key` (`_securityVersion` in `Settings.razor`,
  `_listVersion` in `ChecklistPanel`).

**Data Protection keys are persisted at `DATA_PROTECTION_KEYS_PATH`** (`Program.cs`), defaulting to
a `keys` folder beside `NOTES_PATH` (a possibly-remote PostgreSQL gives no local directory to derive
it from). They sign the auth cookie; at the default (ephemeral container filesystem) every restart
regenerates them and silently signs everyone out.

## Authentication and users

- Every `UserAccount` has a `Role` (`Admin`/`Member`), optional `PasswordHash`
  (`PasswordHasher<UserAccount>`), optional `Email`, optional `OidcSubject`; it needs a password or
  an email (for OIDC) to ever sign in.
- The first admin is created at the first-run `/setup` screen (gated by `ISetupStateProvider`,
  seeded at startup by `HostExtensions.InitializeSetupStateAsync` from "does any `UserAccount`
  exist"). There is no auto-created default user.
- OIDC (`OIDC_AUTHORITY`/`OIDC_CLIENT_ID`/`OIDC_CLIENT_SECRET`/`OIDC_DISPLAY_NAME`, read into
  `OidcOptions` like `NOTES_PATH`/`POSTGRES_HOST`/`TZ`) works alongside password login. Provisioning is
  admin-gated: an admin pre-creates the account with an `Email`, and the first OIDC login for that
  email links it (`OidcAccountLinker`, **unlinked** accounts only — never relinks or auto-creates).
  Password login can be disabled in Settings → Security once OIDC is configured;
  `ISettingsService.SetPasswordAuthEnabledAsync` refuses otherwise (lockout).
- **Username/email rules live in `JNotes.Core.Users.UserValidation`** (pure static; `IsLowercase`,
  `IsValidEmail` via `[GeneratedRegex]`). Enforced in two layers: `EfUserRepository.CreateUserAsync`
  throws `InvalidUsernameException`/`InvalidEmailException` so every creation path is covered, and
  `/setup` and the "Add user" form check up front so the error shows beside the form. Usernames
  must be lowercase (a non-deterministic ICU collation, `jnotes_case_insensitive`, declared in
  `JNotesDbContext` — already makes lookups case-insensitive, but mixed-case
  input would render inconsistently; note Npgsql's four-argument `HasCollation(name, locale, provider,
  deterministic)` and EF Core's one-argument relational `HasCollation`, which sets the *database*
  default instead, are both in the `Microsoft.EntityFrameworkCore` namespace, so the wrong overload
  binds silently). Email is mandatory at `/setup` (checked in `Setup.razor`, not the
  repository — `CreateUserAsync`'s `email` stays nullable for password-only accounts) but only
  format-validated when adding a user. The `/setup` email `<InputText>` deliberately has no
  `type="email"`: native constraint validation would intercept the submit before `UserValidation`
  runs and hide JNotes's own message.
- **Profile editing** goes through one entry point,
  `IUserManagementService.UpdateProfileAsync(targetUserId, requestingUserId, ...)`, used by both the
  `Account.razor` (`/account`, "My Account", self-edit) and the admin section of `Settings.razor`.
  FirstName/LastName: your own, or anyone's if Admin.
  **Email is Admin-only, enforced in Core** (`InvalidOperationException` for a Member) as well as by
  the disabled input — a client that skips the UI mustn't get through. The account widget
  (`MainLayout` → `AccountMenu`) shows `FirstName ?? Username`, re-queried on each circuit init (not
  cached in cookie claims, which would go stale), so a self-edit save forces a full reload of
  the current page (`/account`, or `/settings` for an admin editing their own row) to refresh that
  sibling component.
- **Self-service password change requires the current password; admin reset doesn't.**
  `ChangePassword.razor` (`/account/change-password`, its own page rather than an inline field in
  the My Account form, so a mistyped current password doesn't fail silently next to unrelated profile
  state) re-verifies via `IUserRepository.VerifyPasswordAsync(username, currentPassword)` before
  calling the same `SetPasswordAsync` the admin's reset-password dialog uses — the admin path
  deliberately skips this check, since knowing the current password isn't and shouldn't be a
  precondition for an admin resetting someone else's. An OIDC-only account (no `PasswordHash` yet)
  skips the current-password field entirely, since there's nothing to verify against — this is then
  "set a password" rather than "change" one. My Account's "Change password" button is just a
  link to this page.
- **Personal account settings and administration are separate pages, split by route and role.**
  `Account.razor` (`/account`, `[Authorize]`, "My Account") holds your own name, email, save-profile and
  a link to change your password; it is reached only from the account widget's "My Account" item
  (there is no sidebar entry), and resolves `ICurrentUserContext` in `OnInitializedAsync` like every
  routed page. `Settings.razor` (`/settings`, page title "Administration", `[Authorize(Roles = "Admin")]`)
  holds Security, Storage, Index, Images, Auto-tagging, the user table and add/edit/delete user; its
  "Administration" item in the account widget (there is no sidebar link) sits inside
  `<AuthorizeView Roles="Admin" Context="adminAuth">`, but that is cosmetic — the
  route attribute is what stops a Member (they get `Routes.razor`'s "not authorized" message). The inner
  `<AuthorizeView Roles="Admin">` and the `IsInRole(nameof(UserRole.Admin))` check stay as defense in
  depth, because an already-open circuit keeps its principal until revalidation: markup gating only
  stops *rendering*, not fetching into a circuit's state, so `OnInitializedAsync` checks the role before
  calling `GetAllUsersAsync()`, `GetNotesFolderPathAsync()`, or `RefreshCountAsync()`. The user
  mutations (create/delete/reset-password) have no server-side role check beyond what
  `UpdateProfileAsync`/`DeleteUserAsync` do; they rely on Blazor Server's render-tree-scoped event
  dispatch (a Member's circuit never receives a handler id for a button `AuthorizeView` didn't
  render). The three user dialogs (edit profile, reset password, delete — inline `MudDialog`s bound to
  the `*Target` fields) are therefore declared *inside* the Administration `AuthorizeView`. Storage/Index only act on *your own* notes but live in Administration because it is admin-only.

## Sidebar (`NavMenu.razor`)

- **Layout:** one `MudDrawer` (`Variant="Responsive"`, md breakpoint, `ClipMode.Always`) in
  `MainLayout.razor` hosts a single `NavMenu` — a persistent sidebar on desktop, an overlay opened by the
  app bar's menu button below md (so the menu, and its search box state, exists once). `MudDrawer`'s
  content is already a full-height flex column; `.jn-account { margin-top: auto }` (`nav-menu.css`)
  bottom-anchors the account section (mobile only — at md and up the widget lives in the app bar).
  `MainLayout` closes the overlay on navigation when the viewport is below md (`IBrowserViewportService`).
- **Account widget:** `AccountMenu.razor` (`Components/Layout/`, injects nothing) is rendered twice — in
  `MainLayout`'s `MudAppBar` at the right (`d-none d-md-flex`, a `MudMenu` whose `ChildContent` is a
  "My Account" `MudMenuItem Href="/account"` and, for admins only, an "Administration"
  `MudMenuItem Href="/settings"` above the `/logout` form) and at the bottom of `NavMenu`
  (`d-md-none`, the original `MudCollapse`, with the same two links as `MudButton Href`s — class
  `jn-account-link` — above the same form). The admin item is wrapped in a nested
  `<AuthorizeView Roles="Admin" Context="adminAuth">`; the context is renamed so it can't collide with the
  outer `auth` one or MudMenu's own `context`. CSS
  show/hide was chosen over `MudHidden` (needs JS, flashes). The display name and avatar initials
  (`UserInitials.Compute`, pure static: `FirstName[0]` + `LastName[0]` if both, else first two letters of
  first name, then username; max 2 chars) are looked up once per circuit in
  `MainLayout.OnInitializedAsync` and passed down as parameters. Sign-out is still a plain
  `<form method="post" action="/logout">` with a `MudButton ButtonType="Submit"`, never a
  `MudMenuItem`/`OnClick` (it must write `Set-Cookie`); "My Account" is plain navigation, so an `Href`
  item is fine. Profile editing (`/account`) and Administration (`/settings`) are reached only from this
  widget, so there is no top-level nav entry for either.
- **Order:** Search, New Note (the dropdown button below), All Notes, Shared with Me (a plain `MudNavLink` to
  `/shared`), divider, Trash, then (signed-in only, when there are notes) a **Recent** section (Search and New Note
  share one `<AuthorizeView Context="auth">` outside `MudNavMenu`; the divider is a plain `MudDivider`). Icons are MudBlazor's
  built-in `Icons.Material.*` (compiled SVG path strings — no icon font, no network). The vendored UI
  dependencies are Roboto and mermaid, under `wwwroot/lib/`; MudBlazor itself comes from NuGet. The
  search field has a leading search icon and clears itself on every `@onclick` (an unmatched attribute
  on `MudTextField`, which lands on its root element). Nav labels are title case throughout.
- **"New Note" lives here, not in `NoteList.razor`, so it works from any page.** It is one `MudMenu` whose
  `ActivatorContent` is a `Variant.Filled`/`Color.Primary` full-width `MudButton` ("New Note", with + and
  dropdown icons), with two `MudMenuItem Href`s, "Text" (`/notes/new/text`) and "Checklist"
  (`/notes/new/checklist`). It only navigates — **nothing is created until Save** (see `NoteEditor`'s
  new-note mode under "Notes UI"). New notes of every type start with an **empty title, tags, and body**.
  It sits **outside** `MudNavMenu`, in its own `div.jn-nav-new` between the search box and the nav list: a
  `MudMenu` nested *inside* `MudNavMenu` didn't work in practice, whereas as a sibling it behaves like
  `AccountMenu`'s app-bar menu (its popover renders in `MainLayout`'s `MudPopoverProvider`, so the drawer
  can't clip it, and `MainLayout`'s close-on-navigation closes the mobile drawer for free). It shares the
  search box's unqualified `<AuthorizeView Context="auth">` (not a manual `IsAuthenticated` check) because
  `NavMenu` also renders for `NotFound`/`Error` (`[AllowAnonymous]`, same `MainLayout`), and an anonymous
  circuit never gets a handler id for markup `AuthorizeView` didn't render; the context is renamed so it
  can't collide with `ActivatorContent`'s own `context`.
  **No active-route indication on the New Note button, deliberately:** neither the button nor its menu items
  reflect an open `/notes/new/{type}` draft (the editor page itself makes that obvious), so `NavMenu` needs no
  `LocationChanged` subscription (`MainLayout` owns the one that refreshes Recent). `.mud-menu` is `inline-flex`
  by default, so `nav-menu.css` stretches it (and its button) to the sidebar's full width explicitly.
- **Recent notes** (up to 10, below Trash). `MainLayout` loads
  `INoteService.ListAsync(new NoteSearchQuery(Limit: 10))` — scope All (own + shared with me), index
  `UpdatedAt` order, so the `ContentOnly` staleness applies (a body-only edit doesn't reorder it until a
  `Metadata` save or rescan) — in `OnInitializedAsync` and on **every `LocationChanged`** (query-string-only
  changes included). Each refresh cancels any in-flight one, re-checks authentication (a deleted user's
  index rows survive), and on error logs a warning and keeps the previous list. It passes the list down
  `MainLayout` → `NavMenu` → `RecentNotes.razor` (`Components/Layout/`, injects nothing, same "layout queries,
  children inject nothing" pattern as `AccountMenu`), rendered inside `<AuthorizeView Context="auth">` before
  `AccountMenu`, which must stay the last child. Entries are `MudNavLink Href="/notes/{id}"` with a
  type icon and "Untitled" for an empty title; the open note is highlighted by `MudNavLink`'s default
  `Prefix` match, so `/notes/{id}/edit` counts too (no URL parsing). The heading is an `h2` because
  `Routes.razor`'s `FocusOnNavigate` targets `h1`. There is deliberately no timer or push: the list does
  **not** refresh without a navigation (Restore/Purge on `/trash`, another user's edit, Rescan Now).
- **Search** is a single free-text box (title, body, tags) with no type/tag options. Enter navigates
  to `/?q=<url-encoded text>` (empty → `/`); `NoteList.razor` reads it via
  `[SupplyParameterFromQuery(Name = "q")]` and refreshes from `OnParametersSetAsync`, not
  `OnInitializedAsync` — the page instance is reused across searches, only the query string
  changes (same reason `NoteEditor.razor` reacts to its `Id` parameter). Routing through the URL is
  what makes search work from any page. `NoteList.razor` is a thin routed page: it holds `Query` and
  every `INoteService` call, and hands the result to `NoteListView` (`Components/Notes/`), which
  renders the "Search results for … · Clear" line shown when `Query` is set, and the list (see "The default notes view" under "Notes UI").

## Storage and indexing

**Files are the source of truth; the `NoteIndex*` tables are a derived index.** Notes live at
`{NotesFolder}/{userId}/{noteId}.md` (`FileSystemNoteFileStore`).

- **Writes go file first, then index** (`NoteService`): a crash leaves an orphaned file the next
  reconciliation indexes, never an index row for a file that was never written. Trashing likewise.
- **`NoteIndexReconciler.RescanAsync()`** diffs the folder against the index — added, updated (disk
  mtime vs. last-indexed mtime), removed, and corrupted front matter (error recorded, row kept) —
  at startup, from Settings → "Rescan Now", and via the auto-index background service. This is what
  makes hand-edited or externally added/removed `.md` files converge. Deliberately no
  `FileSystemWatcher` (unreliable across Docker bind-mount backends).
- **`ListAsync`/search read only the index tables** (never disk); only `GetAsync` reads the file.
- **Deleting a user** never deletes their notes: `INoteFileStore.ArchiveUserFolderAsync` *moves*
  the folder to `{NotesFolder}/deleted/{userId}/` **before** the account row is hard-deleted, so a
  failure at either step leaves the account fully intact or fully deleted, never an index-less
  orphan tree.
- **"Delete" on a note moves it to `{NotesFolder}/{userId}/trash/{noteId}.md`; only the Trash page
  purges.** `TrashAsync`/`RestoreAsync`/`PurgeAsync`/`ListTrashedIdsAsync` on `INoteFileStore`.
  Nesting the trash *inside* the user folder hides it from everything else for free
  (`ListIdsAsync`'s `EnumerateFiles(folder, "*.md")` is non-recursive), unlike the sibling
  `deleted/{userId}/` archive. `NoteService.TrashAsync` deletes the index row immediately —
  otherwise a trashed note is indistinguishable from "removed externally" until the next rescan.
  There's deliberately **no index for the trash**: `ListTrashAsync` reads the trash folder
  directly, skipping a note on `NoteNotFoundException` (purged mid-listing) or `NoteParseException`
  (corrupted). `TrashAsync` stamps mtime explicitly with `File.SetLastWriteTimeUtc` (a same-volume
  move doesn't change it); that stamp is "deleted at" (`GetTrashedAtUtcAsync`). `RestoreAsync`
  reindexes like any other note.
- **Front matter is hand-split, not Markdig-parsed** (`FrontMatterSplitter`: does the file start
  with a `---` block, then YamlDotNet). `NoteMarkdownSerializer` maps it to the `Note` model.
  Timestamps are UTC in storage; `JNotesOptions.TimeZoneId`/`AppTimeZoneProvider` (`TZ` env var)
  affect only *display*.
- **The index lives in the same PostgreSQL database and schema as users/settings** — one connection
  string (assembled from `POSTGRES_HOST`/`POSTGRES_PORT`/`POSTGRES_DB`/`POSTGRES_USER`/
  `POSTGRES_PASSWORD` — split out, rather than one `POSTGRES_CONNECTION_STRING`, so a docker-compose
  setup can share plain host/port/db/user/password values with the `postgres` service instead of
  hand-building a connection string), one migration history, one `MigrateAsync()` at startup. The
  `NoteIndex*` table prefix (`NoteIndexEntries`, `NoteIndexTags`) is the "these are the throwaway,
  rebuildable ones" marker in `psql` — a schema boundary would add no guarantee `NoteIndexReconciler`
  doesn't already provide, since the tables can be `TRUNCATE`d and rebuilt from disk at any time.
  `NoteAutoTagAttempts` (the background tagger's memory, see "Auto-tagging") and `NoteShares` (per-note
  sharing grants, see "Note sharing") are the only **non-`NoteIndex*`** note-related tables, precisely because
  they are *not* derived: neither can be rebuilt from the files.
- **Tags are a child table (`NoteIndexTags`), not a `jsonb`/`text[]` column**, because search has to
  match title OR body OR *any tag* with `ILIKE` in one translatable LINQ query —
  `jsonb_array_elements_text` isn't expressible in LINQ, and Npgsql's `text[]` mapping has no "any
  element ILIKE" translation. This also makes the exact-tag filter and `GetDistinctTagsAsync` indexed
  SQL instead of "load every note into memory and filter".
- **`NoteIndexUserTags` is a per-user distinct-tag projection of `NoteIndexTags`** (`UserId`, `Tag`,
  `NoteCount` = distinct indexed notes carrying it, `LastUsedAt` = max `UpdatedAt` of them), backing tag
  auto-suggest (`INoteService.SuggestTagsAsync`, the `TagInput` component) and `GetDistinctTagsAsync`. It
  is a derived cache like the rest of `NoteIndex*` and safe to `TRUNCATE`.
  `PostgresNoteIndexRepository.UpsertAsync`/`DeleteAsync` recompute only the affected tags (old ∪ new) in
  the same transaction as the `NoteIndexTags` write, using `INSERT … ON CONFLICT DO UPDATE` so two circuits
  sharing a tag can't hit a PK violation. `RescanAsync` ends with `RebuildUserTagsAsync(userId)`, so a
  truncated or drifted table heals even when every file is unchanged (the mtime skip would otherwise never
  touch it). The key `(UserId, Tag)` uses the default deterministic collation, so "Work" and "work" stay two
  rows (matching `NoteIndexTags` and the exact tag filter). Matching is case-insensitive `ILIKE`, prefix
  matches first, then `NoteCount`, then `LastUsedAt`. No FK to Users (like `NoteIndexEntries`); trashed
  notes contribute no tags because they have no index row; a `ContentOnly` save never touches it (the same
  staleness trade-off as `Body`). Suggestions are scoped by `ICurrentUserContext` inside `NoteService`, so
  a page can't ask for another user's tags. In the editor, `TagInput` (`Components/Notes/`, injects
  nothing — `NoteEditor` passes a `SuggestTags` delegate) shows tags as chips plus a
  `MudAutocomplete<string>`; typed text that matches no suggestion is offered last as "Add new tag …",
  so every add goes through `ValueChanged`, and a comma-separated entry adds several tags (`TagList`, pure
  static). The list page has no Type/Tag filters (removed from the UI); `NoteSearchQuery.Tag`/`Type`
  still exist in Core, and tags are still searchable through the free-text box.
- **`IndexedFileWriteTimeUtc` is rounded to whole microseconds on both write and compare**
  (`IndexTimestamp.ToIndexResolution`), because PostgreSQL `timestamptz` has microsecond precision
  while `File.GetLastWriteTimeUtc` has 100-nanosecond ticks. Without the rounding,
  `NoteIndexReconciler`'s "unchanged since last indexed" test (`lastIndexed >= currentMtime`) is
  false for almost every file on almost every rescan — a full re-parse of the whole collection, every
  time, forever.

## Notes UI

**Opening a note lands on a read-only view; editing is a separate route.** `NoteView.razor`
(`/notes/{id}`) renders title, tags, and body (`MarkdownPreview` for text notes; the shared
`ChecklistPanel` for checklists) with Edit, Export PDF when enabled, and Delete (the same soft-delete as the editor, behind the same "Move to Trash" confirmation `MudMessageBox`). The view
never edits free-form body text, but for checklists it can **toggle items** via
`INoteService.SetChecklistItemCheckedAsync` (adding items/categories is done in the editor). A single `_isMutating` flag (passed to the panel as `Disabled`, which
disables every control while a call is in flight) stands in for the editor's save gate. `NoteEditor.razor` is at `/notes/{id}/edit` with a "Close" link back (autosave has already written everything; the button just returns to the view) and a "Delete" button — an outlined-error icon+text button, behind a "Move to Trash" confirmation `MudMessageBox` (shared wording with the view's Delete).
**The same page also serves `/notes/new/{TypeName}` as an unsaved draft** (`IsNew` = `TypeName is not null`): an in-memory `Note` with `Id = Guid.Empty`, built in `OnParametersSetAsync` (the component is reused when switching text ↔ checklist). **No autosave** — `OnContentEdited`/`OnMetadataEdited` skip the debouncers and `DisposeAsync` skips its flush, since either would create the note. **Save** (`SaveNewAsync`) refuses an empty/whitespace title with an inline error, else calls `INoteService.CreateAsync` and navigates to the view; Delete becomes **Discard** (a link to `/`, nothing on disk to trash). A `NavigationLock` confirms before dropping a draft that has content (`_draftSaved` is set before Save's own navigation so it doesn't block itself). An image pasted into a draft is uploaded immediately, so a discarded draft can leave an orphan for the scheduled cleanup.
The view's whole content (header, meta, body) sits in a centered `.note-view` column capped by
`--note-view-max-width` (56rem, `note-view.css`); the editor's two-pane split deliberately stays full width.
**Shared notes (see "Note sharing"):** `NoteView` and `NoteEditor` load through `INoteService.GetWithAccessAsync` and gate by `NoteAccess`. Buttons by level: **Owner** — Edit, Share (shows "Shared with N"), Export PDF, Delete; **Edit** recipient — Edit, Export PDF, **Leave**; **Read** recipient — Export PDF, **Leave** (`ChecklistPanel ReadOnly`, no Edit). Recipients also see a "Shared by X · Can edit/View only" chip in `.jn-meta`. Leave confirms in a `MudMessageBox`, calls `INoteSharingService.LeaveAsync`, then goes to `/shared`. The **share dialog** is an inline `MudDialog` (the `Settings.razor` pattern) rendered **only inside `@if (_access.IsOwner)`**, so a recipient's circuit never gets its handler ids; it hosts `NoteSharePanel` (`Components/Notes/`, injects nothing — the page passes a `SearchRecipients` delegate and the current `Shares`, and receives `OnShare`/`OnPermissionChanged`/`OnRemove`). `ShareAsync` is an upsert, so adding and changing permission are one handler. The page bumps `_sharesVersion` after every mutation and the panel puts its `Version` in each row's `@key` — a `MudSelect` keeps its own state after a refused change (same gotcha as `_listVersion`). `NoteEditor` for an **Edit** recipient renders the title as text and the tags as read-only chips (`TagInput` has no `Disabled` parameter, so it isn't rendered and `OnMetadataEdited` can never fire), hides Delete, and passes `ImagesNoteId`/`SaveImage` to `TextNoteEditor`; a **Read** recipient is redirected (`replace: true`) to the view. `SaveNowAsync` catches `NoteNotFoundException`/`NoteAccessDeniedException`, sets **`_accessLost`**, cancels both debouncers and shows "no longer available … NOT saved"; `OnContentEdited`/`OnMetadataEdited` and `DisposeAsync`'s flush then do nothing. A recipient's save returns the owner's merged note, so the editor adopts only its Title/Tags/UpdatedAt and keeps the body being typed. `SharedWithMe.razor` (`/shared`) is a thin routed page like `NoteList` (`NoteListScope.SharedWithMe`, `NoteGroupList`). `NoteListItem` shows a "Shared by X · …" chip (recipient) or "Shared · N" (owner).

**Autosave (existing notes only) has two scopes, and the cheaper one is deliberately incomplete.**
`NoteService.AutosaveAsync(note, NoteSaveScope)` is what the editor's debounced autosave,
checklist changes, and `UpdateAsync` funnel through — there's no Save button.
- `ContentOnly` (body edits, checklist toggles/adds) writes only the file, never the index. It's the
  same "file changed, index doesn't know yet" state as a hand edit, so the reconciler catches it up
  for free. Trade-off: the note doesn't move in the `UpdatedAt`-sorted list, and its indexed
  `Body` (used by search) stays stale, until a `Metadata` save or a reconciliation pass.
- `Metadata` (title/tag edits) writes the file **and** reindexes immediately
  (`PostgresNoteIndexRepository.UpsertAsync` replaces the row and its tag rows, in one transaction).

**`NoteEditor.razor` autosave:** two independent debounces (`AutosaveDebouncer`,
`Components/Notes/`) for content and metadata, but the actual save is serialized through one
`SemaphoreSlim(1, 1)` per open editor since both write the same file. `DeleteAsync` cancels pending
debounces and takes the same gate. `DisposeAsync` does one best-effort `Metadata`-scope flush of
anything unsaved (navigating away right after typing) so file and index catch up together.

**Search matches body content**, not just title: `NoteIndexEntry.Body` is a full copy, kept in sync
by the same file-first-then-index path and reconciler as `Title`, with the `ContentOnly` staleness
above. `PostgresNoteIndexRepository.SearchAsync` filters in SQL with `EF.Functions.ILike('%term%')`
over `Title`/`Body` and an `EXISTS` over `NoteIndexTags` — deliberately `ILIKE`, not
`tsvector`/`plainto_tsquery`, which would stem words and stop matching partial words (a silent
search-behavior change this migration was explicit about avoiding). The term is escaped
(`SqlLikePattern`) so a literal `%`/`_`/`\` in a search doesn't act as a wildcard. `query.Tag` stays
an exact, case-sensitive match; `GetDistinctTagsAsync` still sorts `Ordinal` in memory, since the
database's collation would order mixed case differently from `StringComparer.Ordinal`.

**The default notes view groups by relative date.** `NoteGrouping.GroupByRelativeDate` buckets an
already-`UpdatedAt`-descending list into Today/Yesterday/This week/Last week/This month/Previous,
skipping empty buckets and never re-sorting. It's pure static with no clock/timezone reads —
`NoteList.razor` calls it from `RefreshAsync`, passing `DateTimeOffset.UtcNow` and
`IAppTimeZoneProvider.TimeZone`, and both "now" and each `UpdatedAt` are converted to that zone first
(comparing raw UTC dates would misbucket). Boundaries are checked in strict priority order
(today → … → previous), not as independent ranges, because `monthStart` can fall *after*
`lastWeekStart` early in a month.

**The notes screen is split across three files, and the split is deliberate.**
- `Pages/NoteList.razor` (`/`) owns the URL, the `Query` and all `INoteService`
  calls. `_groups` is `null` until the first load finishes, an empty list when nothing matched (safe as
  the "empty" signal only because `GroupByRelativeDate` drops empty buckets). The components below inject
  nothing — `INoteService` has to stay resolved from a routed page's first lifecycle method (see the
  `ICurrentUserContext` gotcha), and `[SupplyParameterFromQuery]` only works on a routable component.
- `Components/Notes/NoteListView.razor` renders the search header and the list.
- `Components/Notes/NoteGroupList.razor` renders the sections with `MudExpansionPanels` (multi-expand)
  and its own Loading…/empty placeholders, and owns `_collapsedGroups`. That set is kept here, not left
  to the panel, because the panel's `Expanded` parameter would snap back to open on every re-render
  (e.g. when a search changes). `NoteListView` renders it **unconditionally** — never wrap it in an
  `@if`: the placeholders live inside it precisely so a search that matches nothing doesn't destroy the
  instance (a component survives its own internal branches, not a parent's) and take the set with it.

**`ChecklistNoteEditor` and `TextNoteEditor` deliberately share one layout.** A hide/show button on
top, then a `.jn-editor-split` CSS grid (one column, two from 960px when the right pane is shown — a
grid rather than `MudGrid`, whose negative-margin spacing risks horizontal overflow): the always-visible markdown `<textarea>` on the
left, and a togglable, **visible-by-default** right pane (`MarkdownPreview` / a read-only
`ChecklistPanel`) driven by a plain `bool` (`_showPreview`/`_showChecklist`), the left going full-width when
it's hidden. Both right panes share `.note-editor-preview`'s fixed height (see `note-editor.css`);
the checklist pane is a flex column so its list scrolls. The one
difference: `TextNoteEditor` has the `MarkdownToolbar` (bold etc.); `ChecklistNoteEditor`
deliberately doesn't, and has just an "Item" and a "Category" button instead (its own `_textareaRef`, no
`MarkdownToolbar`). They call `markdown-toolbar.js`'s `insertLine`, which puts `- [ ] Item` / `## Category`
on its own line below the cursor's line (a blank line is kept above a category) and selects the placeholder. **The editor's checklist pane is a pure preview** — inert checkboxes, no
add controls (`ChecklistPanel ReadOnly="true"`): once the user is editing the raw
markdown, a second way to edit it just duplicates the UI. Checking happens in `NoteView` only; adding
items and categories happens through the editor's toolbar buttons. The body input stays a **native `<textarea>`** (styled as
`.note-editor-textarea`), not a `MudTextField`: `markdown-toolbar.js` and `image-paste.js` drive the real
DOM element and rely on a real `input` event.

**Images: text notes only, button + paste, no drag-and-drop.** `TextNoteEditor` owns the upload;
`MarkdownToolbar`'s Image button (a `MudButton` inside a `MudFileUpload<IBrowserFile>`'s `CustomContent`
that opens its picker; `ClearAsync` after each pick so the same file can be chosen twice) just raises `OnImageSelected`, and
`wwwroot/js/image-paste.js` calls `OnImagePastedAsync` on a paste. Both funnel into one
`UploadAndInsertAsync` → `IImageService.SaveAsync` → `insertAtCursor` with
`ImageLinks.ToMarkdown` (that fires a real `input` event, so Body/autosave need no plumbing).
- **Uploads ride the circuit, not an HTTP endpoint** (`InputFile`; a paste passes the clipboard file
  as a `DotNet.createJSStreamReference`, because a screenshot exceeds SignalR's 32 KB interop
  message limit) — so there is no upload route and no antiforgery/CSRF surface. `paste` only takes
  over an *image-only* clipboard (`text/plain` empty); text copied from Word/a web page often carries
  a rendered image too and must paste as text.
- **Files:** `{NotesFolder}/{userId}/images/{guid:N}.{png|jpg|gif|webp}` (`IImageStore`, explicit
  `userId`; `IImageService` is the implicit-current-user front, like `NoteService`). Nesting under
  the user folder means `ListIdsAsync` (non-recursive `*.md`) ignores it and
  `ArchiveUserFolderAsync` takes images along with a deleted user's notes.
- **The `.md` link is note-relative** — `![name](images/<file>)` — so the raw folder still renders in
  Obsidian/VS Code/GitHub. `ImageLinkMarkdownExtension` (a `DocumentProcessed` walk in the
  `MarkdownRenderer` pipeline) rewrites *only* an exact `images/<valid stored name>` image URL to
  `/images/<file>`; external URLs and anything odd pass through. (Non-owners get the note-scoped route —
  see the sharing bullet below.) `ImageLinks` holds the prefixes so
  inserter and renderer can't drift.
- **`GET /images/{fileName}`** (`Program.cs`, `RequireAuthorization`) serves from the *caller's own*
  folder, so another user's name is a 404 with no user id in the URL. Names are GUIDs, never reused,
  hence `Cache-Control: private, max-age=31536000, immutable`; `nosniff`.
- **Shared notes' images use a note-scoped route** — `GET /notes/{noteId:guid}/images/{fileName}`
  (`Program.cs`, `RequireAuthorization`), used only when the viewer is **not** the owner (`MarkdownPreview`'s
  `ImagesNoteId` → `IMarkdownRenderer.RenderToHtml(markdown, noteId)`, which rewrites `images/<file>` to
  `ImageLinks.NoteScopedPrefix(noteId)` + file through its own Markdig walk; owners keep `/images/…`
  unchanged). It calls `INoteService.OpenImageAsync`, which re-resolves access on every request and serves
  from the **owner's** folder. A **Read** viewer additionally needs the note's raw text to mention the
  file name; **Owner and Edit skip that check** (an Edit user can write any name into the body, and skipping
  it avoids a broken image between a paste and the debounced autosave). Because a revoked share must stop
  working it is `Cache-Control: private, no-cache` with an ETag (the file name) — a revalidation is a cheap
  304 after the access check — not the immutable cache of `/images`. **An Edit recipient's uploads go to the
  owner's folder** (`INoteService.SaveImageAsync`, via `TextNoteEditor`'s `SaveImage` delegate): a recipient's
  own folder would leave the owner unable to see the image and the recipient's cleanup would delete it.
  `IImageCleanupService` is therefore unchanged — the image sits in the owner's folder and the owner's note
  mentions it.
- **Validation is by magic bytes, not the client's name/MIME** (`ImageFormat.Detect`), and
  `ImageFormat.IsStoredFileName` (`\z`-anchored regex) is also the path-traversal guard for every
  name that reaches the disk. SVG is excluded on purpose: opened directly it can run script in the
  app's origin. 10 MB cap (`ImageFormat.MaxBytes`), enforced while streaming to disk, with the
  partial file deleted on failure; the editor also refuses an oversized file up front.
- **Orphans are swept on a schedule, never on delete.** Nothing tracks references (the same image may be
  pasted into several notes), so deleting/purging a note or editing a link out never touches the image.
  `IImageCleanupService` (Core, **explicit `userId`** like the reconciler — its caller is a background
  service with no current user) deletes a user's images that no `.md` file mentions;
  `CleanUpAllUsersAsync` loops every user with per-user failure isolation and records the run time.
  **The schedule is an admin, app-wide setting** (Settings → Administration → Images, a `Settings` row
  `ImageCleanupIntervalDays`, default **7**, `0` = off, max 365; `ImageCleanupSchedule` holds the
  rules, `IsDue` is pure and unit-tested). Unlike `AUTO_INDEX_INTERVAL_MINUTES` (env var, read once),
  it's editable at runtime, so `JNotes.Web/Images/ImageCleanupBackgroundService` **polls every 15 min**
  and re-reads it rather than sleeping a whole interval. The last run is persisted
  (`ImageCleanupLastRunUtc` row) so a restart neither skips a pass nor — for a container restarting
  more often than the interval — postpones it forever. **A never-run schedule starts its clock instead
  of running**, so upgrading (or a fresh install) deletes nothing on first start; the first pass is one
  interval later. "Clean up now" runs the same all-users pass and shares that clock. Like
  `SetPasswordAuthEnabledAsync`, `SetImageCleanupIntervalDaysAsync` has no Core role check; it relies
  on the Administration `AuthorizeView`. Safety rules, all deliberate: "mentions" is
  `ImageFormat.FindStoredFileNames` over the **raw text** of every `.md` in the user folder *and*
  `trash/` (`INoteFileStore.EnumerateRawContentsAsync` — not `ReadAsync`, which would skip the
  corrupted-front-matter notes that still need their images), matched loosely (any occurrence of the
  stored name, case-insensitive, code fences and front matter included) because a false positive only
  keeps a file; every note is read *before* any delete, so a read failure deletes nothing;
  `IImageStore.ListAsync` only returns names JNotes generated, so a stray file is never a candidate;
  and images newer than `ImageCleanupSchedule.MinImageAge` (10 min) are skipped, because an upload
  lands on disk before the debounced autosave writes the note containing its link. An image in a
  *trashed* note survives until that note is purged.

**Mermaid diagrams render in text-note previews only** (`MarkdownPreview`, so the editor's right
pane and `NoteView`; never checklists, the list, or the trash). A ```` ```mermaid ```` fence is an
ordinary fenced code block in the file, so the `.md` stays portable. Markdig's default would emit
`<pre><code class="language-mermaid">`; `MermaidCodeBlockRenderer` (installed by
`MermaidMarkdownExtension`, in the pipeline in `MarkdownRenderer`) instead emits
`<pre class="mermaid">` with the source **HTML-escaped** — that escaping is what keeps
`DisableHtml()` from being bypassed by a `</pre><script>` inside a fence (regression-tested in
`MarkdownRendererTests`). Mermaid's own `securityLevel: 'strict'` sanitizing is the second layer:
hostile HTML in a node label is stripped (`onerror` removed, `<script>` dropped), so such a label
renders **blank**, not as literal text — expected, not a bug. Gotcha: in that file's namespace,
`IMarkdownExtension.Setup`'s parameter must be written `Markdig.Renderers.IMarkdownRenderer`, since
an unqualified name binds to JNotes's own `IMarkdownRenderer` and the class silently fails to
implement the interface.
- **Mermaid is vendored** at `wwwroot/lib/mermaid/dist/mermaid.min.js` (11.17.2, the single-file UMD
  bundle, committed byte-for-byte with no `.map`; the ESM build needs ~100 chunk files). It's
  injected on first use by `wwwroot/js/mermaid-preview.js`, and `MarkdownPreview` only imports that
  module when its HTML contains a mermaid block, so notes without diagrams never download it.
  Diagrams render in the browser; the SVG never crosses the circuit. Upgrading is re-downloading the
  file, not a build step.
- **A `MarkupString` is opaque to Blazor's diff**, so any change to `_html` (every editor keystroke)
  tears down and rebuilds the whole preview subtree, destroying rendered SVGs. Hence, in
  `mermaid-preview.js`: an element stamp (`data-mermaid-key` = theme + source; skip if unchanged), a
  capped SVG cache (a rebuilt element is repainted synchronously, no flash), and a 250 ms debounce so
  only genuinely new source reaches mermaid. `MarkdownPreview` calls the module from
  `OnAfterRenderAsync` on every render, not just the first. A bad diagram falls back to its source
  with `.mermaid-error` and is stamped so it retries only when source or theme changes. For a parse
  failure its hover `title` is always the generic "Invalid mermaid syntax." (`parse` runs with
  `suppressErrors`, so mermaid's own message isn't available).
- **Theme is read from `data-theme` in JS**, not passed from C#, with a `MutationObserver` on
  `<html>`. The attribute is fixed to `dark` today (`App.razor`), but the observer and the theme cache
  key are kept so a future theme change needs no JS changes.
- **`MarkdownToolbar`'s "Flow →" / "Flow ↓" buttons** (a second "Diagrams" `MudButtonGroup`) insert
  `flowchart LR` / `flowchart TD` starters. The snippet text lives as C# raw-string consts in the
  component (non-interpolated: the `{Decision}` braces are literal) and goes through the existing
  `insertAtCursor`, which dispatches a real `input` event, so autosave needs no new plumbing.
- **The toolbar gets the textarea as a getter, not an `ElementReference`.** `TextNoteEditor` passes
  `GetTargetTextarea="() => _textareaRef"` (a `Func<ElementReference>`), and each handler calls it
  at click time. A parent's `@ref` is only assigned *after* the render that passes it down, so a plain
  `ElementReference` parameter stays empty until the parent happens to re-render (e.g. after the
  first keystroke); JS then receives an unresolved object, `insertAtCursor` throws `Cannot read
  properties of undefined (reading 'slice')`, and the circuit dies with "An unhandled error has
  occurred". Every toolbar button failed this way on a freshly opened editor until the getter.
  Same rule for any child that needs a parent's element ref.

## Checklists

**Line-based regex, not a Markdig AST round-trip**
(`JNotes.Core/Serialization/ChecklistMarkdownParser.cs`). Markdig is for read-only preview only;
re-emitting markdown from its tree risks reformatting content outside the edited line. Every
mutation preserves each line's own bytes (and CRLF/LF style) and changes only the target's bracket
character and, for checking, line *order*. Don't route checklist edits through Markdig's AST.
Only `- [ ]`/`- [x]` bullets are recognised.

**API:** `SetItemChecked`, `AppendItemsToCategory`, and `AddCategory` on the parser;
`SetChecklistItemCheckedAsync`, `AddChecklistItemAsync` (optional category), and
`AddChecklistCategoryAsync` on `INoteService` (read the file, guard `NoteType.Checklist`, mutate,
`ContentOnly` save). It's **set, not toggle**: reordering makes
`LineIndex` volatile, and a replayed "set checked" is idempotent while a replayed flip is not.

**Checked items sink to the bottom of the *file*, not just the screen.** A display-only sort would
make the app and any other editor (Obsidian, vim, GitHub) disagree about order, and would desync
`ChecklistNoteEditor`'s markdown pane from its preview pane.
- A **block** is a maximal run of consecutive task lines with the same indent; prose, headings,
  blank lines, and other indents end it. Items therefore never cross a heading, and a nested
  parent never moves away from its children.
- After setting the mark, the block is stably partitioned unchecked-then-checked, with the toggled
  line last in its destination group: checking sends it to the very bottom; **unchecking returns
  it to the end of the unchecked run, not its old slot** (remembering that would need state outside
  the file). Partitioning the whole block also repairs hand-edited files.
- Adding items (`AppendItemsToCategory`) puts one unchecked item per non-blank line of the text
  (trimmed) at the end of the target category's last block's unchecked run, above its checked items;
  with no task lines in the range it inserts before any trailing blank lines. No escaping is needed:
  `Text` is greedy to end of line and front matter is only detected at the file's start.

**Categories are markdown headings — no extra storage.** Any ATX heading (`#`–`######`, text after
a space) starts a category holding the task lines beneath it up to the next heading; items before
the first heading are the uncategorized section. `ChecklistSection`/`ParseSections` is a read-only
projection like `ChecklistItem`. This rides on the block rule above: headings already end blocks,
so a checked item sinks within its own category and never crosses one. On the parser,
`AddCategory(body, name)` appends `## name` after the last content (blank line before it; the name is
trimmed and line breaks flattened) and `AppendItemsToCategory(body, text, headingLineIndex)` inserts
into one section (`null` = uncategorized) using the same end-of-unchecked-run rule, or directly under
the heading if the category is empty.

**`ChecklistPanel` is the one checklist UI, used by both the view (interactive) and the editor
(`ReadOnly`, see above)** (`Components/Notes/`). `ReadOnly` renders just the grouped list with
inert checkboxes — deliberately not `Disabled`, which greys them out; instead `MudCheckBox`'s own `ReadOnly`
blocks toggling and `.checklist-readonly` sets `pointer-events: none` (`note-editor.css`); without `ReadOnly` (the view) the checkboxes toggle. It groups items by section and has no add-item or
add-category controls. It owns no persistence: it renders the `Body` it's given and reports a toggle
through the `OnItemChecked` callback, so the panel stays free of any save logic. Adding, renaming,
deleting, or reordering categories and items is done in the editor's markdown textarea and toolbar.

**`@key` + `_listVersion` is required on checklist rows** (`ChecklistPanel.razor`): each row's
`@key` includes `_listVersion`, bumped after every mutation. Without it, the `MudCheckBox` has already
toggled itself, the item moves, and the row taking its slot renders the same as the old one, so Blazor's
diff skips it and the box stays ticked while the model disagrees. (The same rebuild snaps a box back if
the save failed.)

**The editor edits raw markdown only; the view goes through the service.** `ChecklistNoteEditor`
has one input, the markdown textarea, whose changes raise `BodyChanged` and ride `NoteEditor`'s
ordinary content debounce and save gate. `NoteView` has no autosave, so its panel callbacks use the
`INoteService` checklist methods.
Known limitation: two tabs on one note are last-writer-wins (there's no cross-circuit locking).

## Note sharing

An owner shares one note with any other user as **Read** or **Edit**. **The file never moves or gets copied**
— it stays at `{NotesFolder}/{ownerId}/{noteId}.md` — and every access check is made in Core, on every call.

**Model.** `NoteShares` (`NoteShare`, `Data/`) is the source of truth for grants and is **not derived** (like
`NoteAutoTagAttempts`, so no `NoteIndex*` prefix): PK `(NoteId, SharedWithUserId)` (the resolver's lookup; one
owner per recipient), indexes on `(OwnerUserId, NoteId)` and `SharedWithUserId`, `Permission` stored as the enum
name, **both FKs to `Users` cascade** (PostgreSQL allows two cascade paths), a check constraint
`CK_NoteShares_NotSelf`, and deliberately no FK to the truncatable `NoteIndexEntries`. Grants are **not** in front
matter: that would put other users' ids in portable files, need a rescan to see a change, and let a hand edit grant
access. `INoteShareRepository`/`PostgresNoteShareRepository` (singleton, explicit ids) is plain storage;
`INoteService` does note access and `INoteSharingService`/`NoteSharingService` (scoped, implicit current user)
manages grants (`ListSharesAsync`, `SearchRecipientsAsync`, `ShareAsync` — an upsert, so it also changes a
permission — `UnshareAsync`, `LeaveAsync`).

**Access resolution, re-done on every call, nothing cached** (`NoteService.ResolveAccessAsync`): (a) the live file
exists in the caller's own folder → **Owner** (a stat, no DB query); (b) else a `NoteShares` row for
`(noteId, me)` → the row's owner with Edit/Read; (c) else `NoteNotFoundException`. So an unshared foreign id is
still a 404 and the own-folder guarantee holds, and revoking or downgrading takes effect on the recipient's next
action (permissions are not in cookie claims). `NoteSharingService.RequireOwnerAsync` gives a recipient
`NoteAccessDeniedException` and a stranger `NoteNotFoundException`.

**Permission matrix.** Read: view/search/export PDF, Leave. Edit: the same plus the **body only** (text body,
checklist add/toggle, images). Owner only: title, tags, type, share management, trash/restore/purge.
`INoteService.GetWithAccessAsync` returns the note with its `NoteAccess` (`Level`, `OwnerUserId`,
`OwnerDisplayName`, `IsOwner`, `CanEditBody`).

**Edit writes are a body-only compare-and-swap merge** (`NoteService.ReplaceBodyAsync`), never `WriteAsync`
(which creates a missing file): `ReadRawAsync(owner)` → deserialize → take **only `Body`** from the caller (Title,
Tags, Type, CreatedAt stay the file's, `UpdatedAt` = now) → `ReplaceIfUnchangedAsync`, up to 3 tries, then an
`IOException`. So a trashed/purged/archived note is never recreated, a recipient's metadata is never persisted
(differing Title/Tags are **ignored**; only a Type change throws `NoteAccessDeniedException`, since throwing on a
rename would break every recipient autosave after the owner renames), and the owner's concurrent rename or the
auto-tagger's tags survive a recipient's save. The `Metadata` scope also reindexes the owner's row
(`IndexAsync(ownerId, …)`); `ContentOnly` writes only the file, so the `ContentOnly` staleness (index `Body`/`UpdatedAt`,
list order, search) applies to recipients' edits too. Saves are last-writer-wins between owner and recipient
(the owner's `WriteAsync` sends its whole in-memory note); the CAS only closes the read-to-write window.
The auto-tagger runs per owner and a recipient's merge re-reads the file, so auto-added tags survive.

**Owners' autosave needs a live file:** `AutosaveAsync` requires the live file to exist (resolver step a), so an
autosave after the note was trashed in another tab throws `NoteNotFoundException` instead of silently recreating
a live copy next to the trash copy (which is also what stops a recipient whose share was revoked from forking
the note into their own folder).

**Trash, restore, purge, users.** `TrashAsync`/`RestoreAsync`/`PurgeAsync` stay own-folder only, so a recipient's
call is a natural no-op (`RestoreAsync` throws `NoteNotFoundException`). Trash keeps the share rows; recipients
lose the note because the index row is deleted and the live file is gone (reads, image requests and PDF export
404, and a recipient's write throws `NoteNotFoundException` — the CAS finds no file). `RestoreAsync` reindexes and
the shares reappear with the same permissions. `PurgeAsync` deletes the file, then the share rows (only if the note
isn't live). Deleting a user archives their folder, then the row delete cascades their shares as owner and as
recipient; the owner's orphaned index rows remain but are unreachable through any share. `LeaveAsync` has no
existence check, so it works while the note is trashed. A file removed outside the app leaves a harmless
invisible share row that applies again if the file returns. `FileSystemNoteFileStore.ResolveFolderAsync`
creates `{ownerId}/` as a side effect, so a read in the window between a folder archive and the row delete can
recreate an empty owner folder (harmless).

**Listing and search** stay one SQL query over `NoteIndexEntries` (`PostgresNoteIndexRepository.SearchAsync`):
`e.UserId == me OR EXISTS(NoteShares s WHERE s.NoteId == e.Id AND s.OwnerUserId == e.UserId AND
s.SharedWithUserId == me)` for `NoteListScope.All`, only the `EXISTS` for `NoteListScope.SharedWithMe`
(`NoteSearchQuery`'s 4th parameter; its 5th, `Limit`, is an SQL `LIMIT` applied after every filter, ordered
`UpdatedAt` then `Id` descending so ties are deterministic, and must be >= 1); requiring `s.OwnerUserId == e.UserId` means a stale or foreign share row
can never expose another folder's entry. `NoteSummary` keeps its positional constructor and gains init
properties `OwnerUserId`, `SharedWithMe` (`SharedNoteInfo`: owner, display name, permission — null for own
notes) and `OutgoingShareCount`. Text, tag and type filters cover shared notes. **Tag suggestions and
`GetDistinctTagsAsync` stay own-only**, because recipients can't edit tags. If the `OR EXISTS` ever hurts the
`(UserId, UpdatedAt)` index for large collections, rewrite it as a `Concat` of the own and shared queries.

**Recipient picker.** `SearchCandidatesAsync` lists every account except the owner and the note's current
recipients, `ILIKE` over username and first/last name (`Username` has a non-deterministic collation, which
`ILIKE` refuses, so it is compared under `EF.Functions.Collate(…, "default")`), escaped with `SqlLikePattern`,
ordered by username, limit clamped 1..20. **It never matches on or returns an email.** Display names come from
`UserDisplayName.Format` (pure static, like `UserInitials`).

## PDF export (optional)

**Off unless `GOTENBERG_URL` is set** (`PdfExportOptions`, read in `AddJNotesCore` like `OIDC_*`; `IsEnabled` needs an absolute http(s) URL). Both halves check it: `NoteView` renders the **Export PDF** button only when enabled, and `Program.cs` maps `GET /notes/{id:guid}/pdf` only when enabled. With it off, the route is simply unmapped (404), not a handler that refuses.

**A plain anchor to a minimal-API endpoint, not an `@onclick`.** Only a real HTTP response can hand the browser a file. The button is `Href` + `Target="_blank"`: Blazor's router ignores anchors with a non-`_self` target, so the browser does a real GET with the cookie. The endpoint (`Export/NotePdfEndpoints.cs`) returns `Content-Disposition: inline` with a title-derived name (`PdfFileName`), so the PDF opens in the new tab and "Save" proposes that name. It resolves `INoteExportService` in an ordinary request scope, where `HttpContext` always exists, so the `ICurrentUserContext` gotcha doesn't apply. The access check is `INoteService.GetAsync`, which resolves owner-or-shared, so an id the caller neither owns nor was given is a 404; **any access level, Read included, may export** (it is a read), and stored images come through `INoteService.OpenImageAsync` (the same access rules as the app, from the owner's folder). If Gotenberg is down or errors, `GotenbergPdfRenderer` throws `PdfRenderingException` and the endpoint returns **503 with its own small HTML page**. It needs a body, or `UseStatusCodePagesWithReExecute` would show "Not found".

**Gotenberg's HTML route with a self-contained document, never its URL route.** Gotenberg has no cookie, and our pages are interactive circuits in the user's (dark) theme. `NotePdfDocumentBuilder` (pure static) builds `index.html` with an inline light stylesheet (`Export/note-pdf.css`, an embedded resource with literal colours, no `--mud-*` variables). Everything the page loads is sent as a flat sibling file in the same multipart request: stored images (read through `INoteService.OpenImageAsync`, so access-checked), the vendored Roboto `.woff2` files (the Gotenberg image has no Roboto), and `mermaid.min.js` only when a diagram exists.

**Nothing user-controlled may make Chromium fetch.** `PdfMarkdownRenderer` shares `MarkdownRenderer.CreateBaseBuilder()` (tables, `DisableHtml`, mermaid) but not `ImageLinkMarkdownExtension`. It rewrites only `images/<stored name>` to the bare file name, and turns every other image (external URL, `data:`, odd path) into a plain link. As a second layer, compose starts Gotenberg with `--chromium-allow-list=^file:///tmp/.*`, because mermaid's sanitizer still allows an `<img>` inside a label.

**Mermaid renders inside Gotenberg's Chromium**, light theme, `securityLevel: 'strict'`, with the same parse-then-render and error fallback as `mermaid-preview.js`. An inline script sets `window.jnotesPdfReady` once fonts and diagrams are done, and Gotenberg's `waitForExpression` waits for it. If the flag never flips, Gotenberg's own 30 s timeout answers 503. Checklists are static HTML mirroring `ChecklistPanel`: items only, grouped by heading, with CSS-drawn boxes.

Compose: the `gotenberg` service sits behind the `pdf` profile, with no published port, and `jnotes` has no `depends_on` on it (that would break a plain `up` with the profile off). The app always gets `GOTENBERG_URL=${GOTENBERG_URL:-}`, which is empty (off) unless `.env` sets it.

## Auto-tagging (optional)

**Runs whenever `OLLAMA_URL` is a valid absolute http(s) URL — there is no separate flag** (`AutoTagOptions.IsEnabled` = URL via `PdfExportOptions.ParseBaseUri` + a non-blank `OLLAMA_MODEL`, default `phi4`; also `OLLAMA_TIMEOUT_SECONDS` 120 clamped 10..600, `AUTO_TAG_INTERVAL_MINUTES` 10 clamped 1..1440, `AUTO_TAG_BATCH_SIZE` 10 clamped 1..100, all flat keys read in `AddJNotesCore`). `AUTO_TAG_ENABLED` is ignored: `Program.cs` logs one warning if it is still set. The launch profiles set `OLLAMA_URL` empty and tests pass an empty `ConfigurationBuilder`, so dev and tests never auto-enable. There is deliberately no editor/`NoteView` UI and no per-user opt-out (an admin decision; documented in the README's privacy note). `Program.cs` logs the state and never the URL.

**Layering**, in `JNotes.Core/Tagging/`: pure static helpers (`AutoTagPromptBuilder`, `AutoTagResponseParser`, `AutoTagNormalizer`, `AutoTagSchedule`, `AutoTagCandidates`); `ITagModelClient`/`OllamaTagModelClient` (typed `HttpClient`, `POST /api/chat`, non-streaming, JSON-schema `format`, `num_ctx` 8192) whose failures are `AutoTagException` with an `AutoTagFailureKind` (`Unavailable`: connection, 5xx incl. 503 overloaded, 429, 404 model missing; `Timeout`; `BadResponse`: garbage, 200 with `error`, other 4xx). The client logs at Debug only, never an exception object (its message carries host:port), and its `HttpClient` has `RemoveAllLoggers()` because the default handler logs the request URL on every call — **the Ollama URL, note text, titles and tags are never logged**. `INoteAutoTagger`/`NoteAutoTagger` (scoped, **explicit `userId`** like `IImageCleanupService`, since its caller has no current user) is driven by `Web/Tagging/AutoTagBackgroundService` — the image-cleanup pattern: one scope per pass, initial 1-minute delay then a `PeriodicTimer`, never rethrows. A pass takes up to `BatchSize` notes across all users (users shuffled, per-user candidates interleaved round-robin), one at a time; it deliberately bypasses `NoteService` (implicit user, and its autosave stamps `UpdatedAt`). Web never talks to Ollama.

**Which notes:** live, indexed notes with **zero tags**, some title or body, and index `UpdatedAt` older than `AutoTagSchedule.MinNoteAge` (10 min), newest first (`PostgresAutoTagStateRepository.GetCandidateNoteIdsAsync`, one `NOT EXISTS` query over `NoteIndexTags` and the attempt table). The index only *nominates*: the file is re-read raw first and the note is deferred if it now has tags, is blank, or its mtime is too recent. Tagged notes are never touched and tags are never added to existing ones. The first enable **backfills** existing untagged notes, throttled by batch and interval; setting `OLLAMA_URL` is the opt-in. Vocabulary is the note owner's own tags (`INoteIndexRepository.SuggestTagsAsync(userId, …)`), so a matching tag takes *their* spelling; normalisation is lowercase kebab-case, generic tags dropped, at most 5.

**`NoteAutoTagAttempts` is not derived and not `NoteIndex*`** (`NoteAutoTagAttempt`, PK `(UserId, NoteId)`, `Outcome` as a string, FK to `Users` with cascade, no FK to the truncatable index). It is the tagger's memory, so it can't be rebuilt from the files: truncating it is safe but re-attempts every untagged note once, including ones whose tags a user removed. One final attempt per note (`Tagged`/`NoTags`/`Skipped`), so tags a user later removes never come back and "nothing useful" isn't retried. Per-note failures (`BadResponse`, `Timeout`, IO/parse) back off 1 h/6 h/24 h/72 h (`AutoTagSchedule.RetryDelay`), and the fifth becomes `GaveUp`. `Unavailable` records nothing and stops the pass (cooldown = the next interval tick); `Timeout` records a failure *and* stops it, so one huge note can't block the queue.

**Write safety** (all deliberate): (1) the `MinNoteAge` quiet period, because an open editor autosaves its whole in-memory note; (2) a **canonical-format guard** — a note is rewritten only if `Serialize(Deserialize(raw)) == raw`, otherwise it is recorded `Skipped` and never touched, so a background write changes only `tags:` (hand-edited files with extra front-matter keys, CRLF or leading blank body lines are left alone; the serializer would drop or normalise them); (3) `INoteFileStore.ReplaceIfUnchangedAsync`, a compare-and-swap that holds the file `FileShare.None` across read, ordinal compare and write, and returns `false` (nothing written, nothing recorded, retried later) if the text changed while the model ran; a concurrent editor autosave in that window gets an `IOException`; (4) order is file, then `UpsertAsync`, then the attempt row, with `UpdatedAt` **unchanged** (list order, relative-date grouping and `LastUsedAt` don't move) and `IndexedFileWriteTimeUtc` = the post-write mtime, so the reconciler doesn't re-parse the note. Known limitation, documented: an editor left open **idle for more than 10 minutes** on an untagged note, then edited, overwrites the auto-tags (last-writer-wins, as everywhere else); the note stays `Tagged`.

**Status card:** Settings → Administration → "Auto-tagging" (read-only; no "Run now" button, since a pass can take minutes) reads the in-memory `AutoTagStatus` singleton (last pass, tagged since startup, "Ollama unavailable since …" with a URL-free reason). It resets on restart and doesn't update live. Logging is state-transition only: one Warning when it becomes unavailable, Information when it recovers, a pass summary at Information only when something happened.

Compose: the optional `ollama` service sits behind the `ollama` profile (no published port, named `ollama-models` volume, no `depends_on` from `jnotes`), and `jnotes` gets `extra_hosts: host.docker.internal:host-gateway` so an Ollama on a Linux host is reachable; `AUTO_TAG_INTERVAL_MINUTES`/`AUTO_TAG_BATCH_SIZE` are passed through with their defaults.

## Tests

`tests/JNotes.Tests/`: pure unit tests (`Serialization/`, `Time/`, `Notes/`) and integration tests
(`Integration/`, `Users/`) that build the real `AddJNotesCore` graph against a **real PostgreSQL**
(`JNOTES_TEST_POSTGRES_CONNECTION_STRING`, default `Host=localhost;Port=5432;Database=jnotes;
Username=jnotes;Password=jnotes`; e.g. `docker run --rm -p 5432:5432 -e POSTGRES_USER=jnotes
-e POSTGRES_PASSWORD=jnotes -e POSTGRES_DB=jnotes postgres:18-alpine`) and a temp folder — not EF
Core's in-memory provider, so real PostgreSQL behaviour (the ICU collation, the filtered unique
indexes, `ILIKE` escaping, the migration itself) is exercised. Each test class creates and drops its
own `jnotes_test_<guid>` database (`TestPostgresDatabase`) — a whole database, not a schema, so
migrations run against a virgin database exactly as in production; the configured user needs
`CREATEDB`. Two Npgsql pooling gotchas the helper handles: `NpgsqlConnection.ClearAllPools()` before
`DROP DATABASE` (disposing EF's contexts only returns pooled connections, it doesn't close them), and
`WITH (FORCE)` as the backstop. No Razor component tests (no bUnit); UI changes are verified manually
in a browser.

**Use the standing debug containers, and never remove them.** `docker-compose.debug.yml` (project
`jnotes-debug`) defines the instances used for running tests and manual checks on this machine:
`jnotes-debug-db` (PostgreSQL, `localhost:5432`, `jnotes`/`jnotes` — the tests' default connection
string, so no `docker run` is needed), `jnotes-debug-pdf` (Gotenberg, `localhost:3000`) and
`jnotes-debug-ollama` (Ollama, `localhost:11434`, model pulled with
`docker exec jnotes-debug-ollama ollama pull phi4`). They are the user's debugging environment: don't
stop, remove, recreate or `down` them (or `down -v` their volumes), and don't run `docker compose up` for
the whole file — start just the one service needed (`up -d jnotes-debug-ollama`), since recreating
`jnotes-debug-db` wipes its tmpfs. The first two were created from a separate copy of the file, so a
whole-project `up` may recreate them. Tests only create/drop their own `jnotes_test_<guid>` databases;
the `jnotes` database is the user's real dev database. For a manual run of the app, point
`POSTGRES_DB` and `NOTES_PATH` at a throwaway database/folder rather than the user's — background
auto-tagging (set `OLLAMA_URL`) rewrites the tags of *every* untagged note it finds, so never enable it
against real notes with a stub or a test model.

`CoreServicesFixture` seeds two users and registers a mutable `TestCurrentUserContext`
(`ActAsUserA()`/`ActAsUserB()`) in place of the real one, so cross-user isolation is asserted
through `NoteService` itself. `Users/EfUserRepositoryTests` and `UserManagementServiceTests` build
their own provider because they need things the fixture doesn't expose (`ISetupStateProvider`,
`IUserManagementService`); the latter covers move-before-delete, including idempotency on retry.
`CoreServicesFixture` also swaps in `RecordingPdfRenderer`/`StubPdfExportAssetSource` and enables
`PdfExportOptions`, so `NoteExportService` runs without Gotenberg. It likewise swaps in
`RecordingTagModelClient` (with an `OnCall` hook) and a `TestTimeProvider` and sets `OLLAMA_URL`, so `NoteAutoTagger` runs without Ollama and backoff is testable. No hosted services run in tests.
`CoreServicesFixture` also exposes `NoteSharing` (`INoteSharingService`), `ShareRepository`
(`INoteShareRepository`) and `UserManagement`, plus `CreateUserAsync(username, role)` and `ActAs(userId)` for
users beyond A and B; `Integration/NoteSharingTests` (access, permissions, listing/search, trash/purge, user
deletion, images, PDF, the locked-file CAS conflict) and `NoteShareRepositoryTests` (check constraint, cascades,
recipient search) build on them.

## CSS

Plain hand-authored CSS directly under `wwwroot/css/` — no Sass, no build step. MudBlazor supplies
components and the palette (see "UI is MudBlazor"); our CSS only adds what it doesn't cover.
`variables.css` holds custom properties (`:root { --note-editor-height: 60vh; ... }`); `fonts.css`
declares the vendored Roboto; `note-editor.css`, `note-list.css`, `nav-menu.css`, `settings.css` use
native nesting (`&`); `markdown-preview.css` styles the class-less HTML Markdig emits (MudBlazor doesn't
touch bare `h1`/`p`/`pre`/`table`); `auth.css` is the static-SSR login/setup look; `app.css` (linked
from `App.razor`) `@import`s the rest. Add a new concern as its own `.css` file `@import`ed from `app.css`.
`dotnet watch` and `app.MapStaticAssets()` serve these like any static asset.

## Docker build gotcha

The Dockerfile restores against just the `.csproj` files first (layer caching), then copies full
source. The publish step must **not** pass `--no-restore`: otherwise the static web assets manifest
is computed from the incomplete early restore and the image ships without
`_framework/blazor.web.js`, silently breaking all client-side interactivity while `curl` checks
still look fine. If touching the Dockerfile, verify in a real browser (or at least
`curl -H "Accept-Encoding: gzip"`), not a plain `curl` health check.

**PostgreSQL's data directory, not just the app's.** `postgres:18`+ images moved `PGDATA` to
`/var/lib/postgresql/18/docker` and declare the image's `VOLUME` one level up, at
`/var/lib/postgresql` — mounting `/var/lib/postgresql/data` (the older convention) is **silently
ignored**: data lands in an anonymous volume and `initdb` re-runs on every container recreate, with
no error. `docker-compose.yml` mounts the named volume at `/var/lib/postgresql` for this reason.
This volume is **not optional**: PostgreSQL holds user accounts, settings and share grants (the
`NoteIndex*` tables beside them are a derived cache, rebuilt on "Rescan Now"), which exist nowhere else — `docker compose down -v` deletes them, and a wrong mount
path silently does the same on every restart.
