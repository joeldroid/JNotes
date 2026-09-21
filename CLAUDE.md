# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

JNotes is a self-hosted, multi-user note-taking app: Blazor Server (.NET 10) UI built on MudBlazor, notes stored as
portable markdown files on disk (each a readable `.md` with YAML front matter, one subfolder per
user), a Redis index over them for fast search — the index is a **derived cache**, never the
source of truth — and a SQLite database for everything else (settings, user accounts). See
README.md for features, the note file format, and the configuration reference.

## Commands

```bash
# Build / test / format (from repo root)
dotnet build JNotes.slnx
dotnet test JNotes.slnx                          # integration tests need a real Redis (see Tests)
dotnet test tests/JNotes.Tests --filter "FullyQualifiedName~ChecklistMarkdownParserTests"   # single class
dotnet format JNotes.slnx
dotnet format JNotes.slnx --verify-no-changes    # CI-style check

# Run locally (needs .NET 10 SDK; data under src/JNotes.Web/App_Data/, gitignored)
dotnet run --project src/JNotes.Web
dotnet watch --project src/JNotes.Web run       # live-reloads C#, Razor, and wwwroot/css/*.css

# EF Core migrations — startup project is Core, NOT Web: Web deliberately has no EF Core Design reference.
dotnet ef migrations add <Name> --project src/JNotes.Core --startup-project src/JNotes.Core -o Data/Migrations
dotnet ef database update --project src/JNotes.Core --startup-project src/JNotes.Core
# `dotnet ef` is a local tool (.config/dotnet-tools.json) — run `dotnet tool restore` once after cloning.

docker compose up --build   # production-shaped: what Docker actually runs, not `dotnet run`
```

## Architecture

**Three projects**, one-directional dependency: `JNotes.Web` (Blazor Server UI + composition root)
→ `JNotes.Core` (domain, storage, indexing, EF Core, Redis) ← `JNotes.Tests` (xUnit, tests Core only).

**Web never touches the filesystem, EF Core/SQLite, or Redis directly** — only Core's interfaces
(`INoteService`, `ISettingsService`, `INoteIndexReconciler`, `IUserRepository`,
`IUserManagementService`, `IImageService`, `IImageCleanupService`). Enforced at build time: `Microsoft.CodeAnalysis.BannedApiAnalyzers`
(`src/JNotes.Web/BannedSymbols.txt`) bans `System.IO.File`/`Directory`, `DbContext`,
`SqliteConnection`, and Redis's `IConnectionMultiplexer`/`IDatabase` in Web. Pure static Core
helpers (`ChecklistMarkdownParser`, `NoteGrouping`, `UserInitials`, `UserValidation`) are fine to
call from Web.

All Core DI wiring is in `JNotes.Core/ServiceCollectionExtensions.AddJNotesCore`, **except**
`ICurrentUserContext`: declared in Core, registered in Web's `Program.cs`, because its only
implementation needs `IHttpContextAccessor` (an ASP.NET Core hosting type Core doesn't depend on).
Web's auth wiring (cookie/OIDC handlers, `[Authorize]`/`AuthorizeRouteView`, sign-in/out) lives in
`JNotes.Web/Auth/` for the same reason. Any service needing a hosting-layer dependency follows
this pattern: interface in Core, implementation + registration in Web.

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

**Theme.** Dark is the app-wide default: `SettingsService.GetThemeAsync` returns `AppTheme.Dark`
when a user has no `UserSetting` row. `App.razor`'s `OnInitializedAsync` prerenders
`<html data-theme>`, and always uses Dark for unauthenticated requests (`/login`, `/setup`) —
a fixed choice, since there's no signed-in user yet. A user who chose light sees a dark login
screen until they sign in.

**UI is MudBlazor** (NuGet `MudBlazor`, `AddMudServices()` in `Program.cs`; Roboto is vendored under
`wwwroot/lib/roboto` and `fonts.css`, so there are no external requests). Things that aren't obvious:
- **One theme, one owner.** `Theme/JNotesTheme.cs` is the shared `MudTheme` (palettes, Roboto stack).
  `MainLayout.razor` hosts `MudThemeProvider`/`MudPopoverProvider`/`MudDialogProvider`/
  `MudSnackbarProvider` and seeds the scoped `ThemeState` from `ISettingsService` (same
  `IsAuthenticated` guard as `App.razor` — `NotFound`/`Error` reuse `MainLayout` anonymously).
  `Settings.razor`'s dark-mode switch calls `ThemeState.Set`; `MainLayout` re-renders the provider and
  rewrites `<html data-theme>` over JS interop (that attribute is what `mermaid-preview.js` reads).
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

**Data Protection keys are persisted next to the database** (`{DB_PATH's directory}/keys/`,
`Program.cs`). They sign the auth cookie; at the default (ephemeral container filesystem) every
restart regenerates them and silently signs everyone out.

## Authentication and users

- Every `UserAccount` has a `Role` (`Admin`/`Member`), optional `PasswordHash`
  (`PasswordHasher<UserAccount>`), optional `Email`, optional `OidcSubject`; it needs a password or
  an email (for OIDC) to ever sign in.
- The first admin is created at the first-run `/setup` screen (gated by `ISetupStateProvider`,
  seeded at startup by `HostExtensions.InitializeSetupStateAsync` from "does any `UserAccount`
  exist"). There is no auto-created default user.
- OIDC (`OIDC_AUTHORITY`/`OIDC_CLIENT_ID`/`OIDC_CLIENT_SECRET`/`OIDC_DISPLAY_NAME`, read into
  `OidcOptions` like `NOTES_PATH`/`DB_PATH`/`TZ`) works alongside password login. Provisioning is
  admin-gated: an admin pre-creates the account with an `Email`, and the first OIDC login for that
  email links it (`OidcAccountLinker`, **unlinked** accounts only — never relinks or auto-creates).
  Password login can be disabled in Settings → Security once OIDC is configured;
  `ISettingsService.SetPasswordAuthEnabledAsync` refuses otherwise (lockout).
- **Username/email rules live in `JNotes.Core.Users.UserValidation`** (pure static; `IsLowercase`,
  `IsValidEmail` via `[GeneratedRegex]`). Enforced in two layers: `EfUserRepository.CreateUserAsync`
  throws `InvalidUsernameException`/`InvalidEmailException` so every creation path is covered, and
  `/setup` and the "Add user" form check up front so the error shows beside the form. Usernames
  must be lowercase (the NOCASE column already makes lookups case-insensitive, but mixed-case input
  would render inconsistently). Email is mandatory at `/setup` (checked in `Setup.razor`, not the
  repository — `CreateUserAsync`'s `email` stays nullable for password-only accounts) but only
  format-validated when adding a user. The `/setup` email `<InputText>` deliberately has no
  `type="email"`: native constraint validation would intercept the submit before `UserValidation`
  runs and hide JNotes's own message.
- **Profile editing** goes through one entry point,
  `IUserManagementService.UpdateProfileAsync(targetUserId, requestingUserId, ...)`, used by both
  `Profile.razor` (self-edit) and the admin section of `Settings.razor`. FirstName/LastName: your
  own, or anyone's if Admin. **Email is Admin-only, enforced in Core** (`InvalidOperationException`
  for a Member) as well as by the disabled input — a client that skips the UI mustn't get through.
  `NavMenu` shows `FirstName ?? Username`, re-queried on each circuit init (not cached in cookie
  claims, which would go stale), so both profile pages force a full reload after saving to refresh
  that sibling component.
- **Settings and user management share one page, split by role, not by route.** `Settings.razor`
  (`/settings`, `[Authorize]`) shows "App settings" (Appearance, your own password) to everyone and
  "Administration settings" (Security, Storage, Index, user table, add/edit/delete user) inside
  `<AuthorizeView Roles="Admin">`. Markup gating only stops *rendering*, not fetching into a
  Member's circuit state, so `OnInitializedAsync` checks `IsInRole(nameof(UserRole.Admin))` before
  calling `GetAllUsersAsync()`, `GetNotesFolderPathAsync()`, or `RefreshCountAsync()`. The user
  mutations (create/delete/reset-password) have no server-side role check beyond what
  `UpdateProfileAsync`/`DeleteUserAsync` do; they rely on Blazor Server's render-tree-scoped event
  dispatch (a Member's circuit never receives a handler id for a button `AuthorizeView` didn't
  render). The three user dialogs (edit profile, reset password, delete — inline `MudDialog`s bound to
  the `*Target` fields) are therefore declared *inside* the Administration `AuthorizeView`. Storage/Index only act on *your own* notes but live in Administration settings to keep
  "App settings" to what every Member needs.

## Sidebar (`NavMenu.razor`)

- **Layout:** one `MudDrawer` (`Variant="Responsive"`, md breakpoint, `ClipMode.Always`) in
  `MainLayout.razor` hosts a single `NavMenu` — a persistent sidebar on desktop, an overlay opened by the
  app bar's menu button below md (so the menu, and its search box state, exists once). `MudDrawer`'s
  content is already a full-height flex column; `.jn-account { margin-top: auto }` (`nav-menu.css`)
  bottom-anchors the account section. `MainLayout` closes the overlay on navigation when the viewport
  is below md (`IBrowserViewportService`).
- **Account widget:** avatar initials from `UserInitials.Compute` (pure static: `FirstName[0]` +
  `LastName[0]` if both, else first two letters of first name, then username; max 2 chars).
  Clicking toggles a `MudCollapse` (`_accountOpen`) with "My Profile" and "Sign out" (a plain
  `<form method="post" action="/logout">` with a `MudButton ButtonType="Submit"`, never an
  `OnClick`); `/profile` has no top-level nav entry.
- **Order:** Search, New text note, New checklist, Notes, Settings, Trash. Icons are MudBlazor's built-in
  `Icons.Material.*` (compiled SVG path strings — no icon font, no network). The vendored UI
  dependencies are Roboto and mermaid, under `wwwroot/lib/`; MudBlazor itself comes from NuGet. The
  search field has a leading search icon and clears itself on every `@onclick` (an unmatched attribute
  on `MudTextField`, which lands on its root element).
- **"New text note" / "New checklist" live here, not in `NoteList.razor`, so they work from any page.** Each calls
  `INoteService.CreateAsync`, then navigates to `/notes/{id}/edit` (a new note has nothing to
  view). New notes of every type, checklists included, start with an **empty body**. They're two
  plain `MudNavLink`s with an `OnClick` and no `Href` (MudBlazor only raises `OnClick` when `Href` is
  unset) rather than one "New note" dropdown — a `MudMenu` inside `MudNavMenu` didn't work in practice, and one
  click per type is faster anyway. They're wrapped in an unqualified `<AuthorizeView>` (not a manual
  `IsAuthenticated` check) because `NavMenu` also renders for `NotFound`/`Error` (`[AllowAnonymous]`,
  same `MainLayout`), and an anonymous circuit never gets a handler id for markup `AuthorizeView`
  didn't render.
- **Search** is a single free-text box (title, body, tags) with no type/tag options. Enter navigates
  to `/?q=<url-encoded text>` (empty → `/`); `NoteList.razor` reads it via
  `[SupplyParameterFromQuery(Name = "q")]` and refreshes from `OnParametersSetAsync`, not
  `OnInitializedAsync` — the page instance is reused across searches, only the query string
  changes (same reason `NoteEditor.razor` reacts to its `Id` parameter). Routing through the URL is
  what makes search work from any page. `NoteList.razor` keeps its own page-local Type/Tag filters
  and shows "Search results for … · Clear" when `Query` is set.

## Storage and indexing

**Files are the source of truth; Redis is a derived index.** Notes live at
`{NotesFolder}/{userId}/{noteId}.md` (`FileSystemNoteFileStore`).

- **Writes go file first, then index** (`NoteService`): a crash leaves an orphaned file the next
  reconciliation indexes, never an index row for a file that was never written. Trashing likewise.
- **`NoteIndexReconciler.RescanAsync()`** diffs the folder against the index — added, updated (disk
  mtime vs. last-indexed mtime), removed, and corrupted front matter (error recorded, row kept) —
  at startup, from Settings → "Rescan Now", and via the auto-index background service. This is what
  makes hand-edited or externally added/removed `.md` files converge. Deliberately no
  `FileSystemWatcher` (unreliable across Docker bind-mount backends).
- **`ListAsync`/search read only the Redis index** (never disk); only `GetAsync` reads the file.
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
  There's deliberately **no Redis index for the trash**: `ListTrashAsync` reads the trash folder
  directly, skipping a note on `NoteNotFoundException` (purged mid-listing) or `NoteParseException`
  (corrupted). `TrashAsync` stamps mtime explicitly with `File.SetLastWriteTimeUtc` (a same-volume
  move doesn't change it); that stamp is "deleted at" (`GetTrashedAtUtcAsync`). `RestoreAsync`
  reindexes like any other note.
- **Front matter is hand-split, not Markdig-parsed** (`FrontMatterSplitter`: does the file start
  with a `---` block, then YamlDotNet). `NoteMarkdownSerializer` maps it to the `Note` model.
  Timestamps are UTC in storage; `JNotesOptions.TimeZoneId`/`AppTimeZoneProvider` (`TZ` env var)
  affect only *display*.

## Notes UI

**Opening a note lands on a read-only view; editing is a separate route.** `NoteView.razor`
(`/notes/{id}`) renders title, tags, and body (`MarkdownPreview` for text notes; the shared
`ChecklistPanel` for checklists) with Edit and Delete (same soft-delete as the editor). The view
never edits free-form body text, but for checklists it can **toggle items, add items, and add
categories** via `INoteService.SetChecklistItemCheckedAsync`/`AddChecklistItemAsync`/
`AddChecklistCategoryAsync`. A single `_isMutating` flag (passed to the panel as `Disabled`, which
disables every control while a call is in flight) stands in for the editor's save gate. `NoteEditor.razor` is at `/notes/{id}/edit` with a "Save" link back (autosave has already written everything; the button just returns to the view) and a "Delete" button — the same filled-primary/outlined-error icon+text pair as the view's Edit/Delete.
The view's whole content (header, meta, body) sits in a centered `.note-view` column capped by
`--note-view-max-width` (56rem, `note-view.css`); the editor's two-pane split deliberately stays full width.

**Autosave has two scopes, and the cheaper one is deliberately incomplete.**
`NoteService.AutosaveAsync(note, NoteSaveScope)` is what the editor's debounced autosave,
checklist changes, and `UpdateAsync` funnel through — there's no Save button.
- `ContentOnly` (body edits, checklist toggles/adds) writes only the file, never the index. It's the
  same "file changed, index doesn't know yet" state as a hand edit, so the reconciler catches it up
  for free. Trade-off: the note doesn't move in the `UpdatedAt`-sorted list, and its indexed
  `Body` (used by search) stays stale, until a `Metadata` save or a reconciliation pass.
- `Metadata` (title/tag edits) writes the file **and** reindexes immediately
  (`RedisNoteIndexRepository.UpsertAsync` replaces the whole hash, tags included).

**`NoteEditor.razor` autosave:** two independent debounces (`AutosaveDebouncer`,
`Components/Notes/`) for content and metadata, but the actual save is serialized through one
`SemaphoreSlim(1, 1)` per open editor since both write the same file. `DeleteAsync` cancels pending
debounces and takes the same gate. `DisposeAsync` does one best-effort `Metadata`-scope flush of
anything unsaved (navigating away right after typing) so file and index catch up together.

**Search matches body content**, not just title: `NoteIndexEntry.Body` is a full copy, kept in sync
by the same file-first-then-index path and reconciler as `Title`, with the `ContentOnly` staleness
above. `RedisNoteIndexRepository.SearchAsync` loads a user's notes (one hash per note, tags as an
inline JSON field) and does case-insensitive `Contains` per field (title, body, each tag)
client-side rather than SQL `LIKE`.

**The default notes view groups by relative date.** `NoteGrouping.GroupByRelativeDate` buckets an
already-`UpdatedAt`-descending list into Today/Yesterday/This week/Last week/This month/Previous,
skipping empty buckets and never re-sorting. It's pure static with no clock/timezone reads —
`NoteList.razor` passes `DateTimeOffset.UtcNow` and `IAppTimeZoneProvider.TimeZone`, and both "now"
and each `UpdatedAt` are converted to that zone first (comparing raw UTC dates would misbucket).
Boundaries are checked in strict priority order (today → … → previous), not as independent ranges,
because `monthStart` can fall *after* `lastWeekStart` early in a month. Sections collapse with
`MudExpansionPanels` (multi-expand). Which groups the user collapsed is kept in `_collapsedGroups`, not
left to the panel, because the panel's `Expanded` parameter would snap back to open on every re-render
(e.g. when a filter changes). The Type/Tag filters are `MudSelect`s; "no filter" is an explicit sentinel
value because `MudSelect` can't label an empty-string value.

**`ChecklistNoteEditor` and `TextNoteEditor` deliberately share one layout.** A hide/show button on
top, then a `.jn-editor-split` CSS grid (one column, two from 960px when the right pane is shown — a
grid rather than `MudGrid`, whose negative-margin spacing risks horizontal overflow): the always-visible markdown `<textarea>` on the
left, and a togglable, **visible-by-default** right pane (`MarkdownPreview` / a read-only
`ChecklistPanel`) driven by a plain `bool` (`_showPreview`/`_showChecklist`), the left going full-width when
it's hidden. Both right panes share `.note-editor-preview`'s fixed height (see `note-editor.css`);
the checklist pane is a flex column so its list scrolls. The one
difference: `TextNoteEditor` has the `MarkdownToolbar` (bold etc.); `ChecklistNoteEditor`
deliberately doesn't. **The editor's checklist pane is a pure preview** — inert checkboxes, no
add-item/add-category boxes (`ChecklistPanel ReadOnly="true"`): once the user is editing the raw
markdown, a second way to edit it just duplicates and crowds the UI. Checking, adding items, and
adding categories happen in `NoteView` only. The body input stays a **native `<textarea>`** (styled as
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
  `/images/<file>`; external URLs and anything odd pass through. `ImageLinks` holds the prefixes so
  inserter and renderer can't drift.
- **`GET /images/{fileName}`** (`Program.cs`, `RequireAuthorization`) serves from the *caller's own*
  folder, so another user's name is a 404 with no user id in the URL. Names are GUIDs, never reused,
  hence `Cache-Control: private, max-age=31536000, immutable`; `nosniff`.
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
  `<html>`: `MainLayout` flips that attribute live with no reload (when `ThemeState` changes), so a
  server-side parameter would go stale. The theme is part of the cache key, so both variants coexist.
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
blocks toggling and `.checklist-readonly` sets `pointer-events: none` (`note-editor.css`); the rest of this paragraph describes the interactive mode. It groups items by section, has a category `MudSelect<int>` (shown only once
headings exist; defaults to the last category, and a new category is auto-selected) beside the
add-item box, and a separate "New category" box. It owns no persistence: it renders the `Body` it's
given and reports actions through `OnItemChecked`/`OnAddItem`/`OnAddCategory` callbacks, so the
panel stays free of any save logic. The category
choice is remembered by heading *line index*, which raw edits can shift, so a stale one falls back to
the last category. Renaming, deleting, or reordering categories is done in the editor's markdown
textarea (the editor's panel is read-only). After an add, the panel refocuses its item input on the next render where it's enabled
(the view disables it during a save, which drops focus).

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

## Tests

`tests/JNotes.Tests/`: pure unit tests (`Serialization/`, `Time/`, `Notes/`) and integration tests
(`Integration/`, `Users/`) that build the real `AddJNotesCore` graph against a temp-file SQLite
database, a **real Redis** (`localhost:6379`, or `JNOTES_TEST_REDIS_CONNECTION_STRING`; e.g.
`docker run --rm -p 6379:6379 valkey/valkey`), and a temp folder — not EF Core's in-memory
provider, so SQLite-specific behavior (e.g. no `ORDER BY DateTimeOffset`, worked around
client-side in `EfUserRepository.GetAllUsersAsync`) is exercised. No Razor component tests (no
bUnit); UI changes are verified manually in a browser.

`CoreServicesFixture` seeds two users and registers a mutable `TestCurrentUserContext`
(`ActAsUserA()`/`ActAsUserB()`) in place of the real one, so cross-user isolation is asserted
through `NoteService` itself. `Users/EfUserRepositoryTests` and `UserManagementServiceTests` build
their own provider because they need things the fixture doesn't expose (`ISetupStateProvider`,
`IUserManagementService`); the latter covers move-before-delete, including idempotency on retry.

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
