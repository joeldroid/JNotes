# Plan: Show the 10 most recently updated notes in the sidebar

## Goal
Signed-in users get a "Recent" section at the bottom of the sidebar's nav list, below Trash. It shows up to 10 of their own and shared-with-them notes, newest `UpdatedAt` first (the same index order as All Notes). Each entry has a type icon and the title. The currently open note (`/notes/{id}` or `/notes/{id}/edit`) is highlighted. The list re-queries on every in-app navigation, so creating, trashing, leaving or renaming a note and then navigating shows up straight away. The cap of 10 is applied in SQL (`LIMIT`), not by loading the whole list and calling `Take` in memory.

## Decisions (made by the user, not to be re-opened)
- "Last updated" = index `UpdatedAt`, as the All Notes list uses it. The documented `ContentOnly` staleness is accepted; autosave is unchanged.
- Scope: own notes + notes shared with me (`NoteListScope.All`).
- Refresh: re-query on every navigation. No timers, no cross-component notification service.
- Placement/look: in `NavMenu.razor`, below the nav links (after Trash), a "Recent" heading, then up to 10 compact links (type icon, title, empty title shown as "Untitled"), open note highlighted.

## Context found
- **Core query path:**
  - `src/JNotes.Core/Notes/NoteSearchQuery.cs:15` is `public sealed record NoteSearchQuery(string? SearchText = null, string? Tag = null, NoteType? Type = null, NoteListScope Scope = NoteListScope.All);`. Callers use named arguments plus one positional first argument, so a new **trailing** optional parameter is source-compatible.
  - `NoteService.ListAsync` (`NoteService.cs:40`) passes straight through to `INoteIndexRepository.SearchAsync(_currentUser.UserId, query, ct)`.
  - `PostgresNoteIndexRepository.SearchAsync` applies the scope, then the Type/SearchText/Tag filters, then `.OrderByDescending(e => e.UpdatedAt).Select(...)` with per-row subqueries (tags, share count, owner). A `Take` placed after the order and before `Select` becomes a SQL `LIMIT` and also cuts the subquery work.
  - `NoteSummary` needs no change. No fakes or other implementations of `INoteIndexRepository`/`INoteService` exist.
- **Layout:**
  - `MainLayout.razor` already injects `ICurrentUserContext`, `AuthenticationStateProvider`, `NavigationManager`; loads the display name only when `IsAuthenticated` (NotFound/Error are `[AllowAnonymous]`); subscribes to `LocationChanged` to close the mobile drawer; passes `DisplayName`/`Initials` down to `NavMenu` → `AccountMenu`. That is the established "layout queries once, children inject nothing" pattern.
  - **`ICurrentUserContext` gotcha:** `AuthenticatedCurrentUserContext` captures the claim in its constructor and throws only from the `UserId` getter. `NoteService`'s constructor only stores dependencies. `MainLayout` already forces `ICurrentUserContext` to be built at circuit start, so `@inject INoteService` there is built at the same safe moment; later calls from `LocationChanged` reuse that scoped instance.
  - `NavMenu`: search + New Note in `<AuthorizeView Context="auth">`, then `<MudNavMenu Class="jn-nav-list">`, then `<AccountMenu>`, which **must stay the last child** of `div.jn-nav`.
- **Highlighting:** `MudNavLink.Match` defaults to `NavLinkMatch.Prefix` (MudBlazor 9.10.0). `NavLink` prefix matching needs a separator after the href, so `Href="/notes/{id}"` is active on `/notes/{id}` and `/notes/{id}/edit`, and inactive on `/notes/new/text`. No URL parsing needed; confirm in the browser.
- `Routes.razor` has `<FocusOnNavigate Selector="h1">`, so the "Recent" heading must be an `h2`.
- UNVERIFIED (check in devtools): that `MudNavMenu` renders a `<nav>` passing `aria-label`/`aria-labelledby` through; that the inner class is `mud-nav-link-text`.
- `App.razor` prerenders, so `MainLayout.OnInitializedAsync` runs twice on a full page load: one extra cheap query.
- Existing empty-title wording is "Untitled note" elsewhere; the sidebar uses "Untitled" (user's choice).
- Rules: Web only talks to `INoteService`; CSS in `wwwroot/css/nav-menu.css` (native nesting, `var(--mud-palette-*)`); `Icons.Material.*`; no bUnit; integration tests use real PostgreSQL via `CoreServicesFixture`; never touch the `jnotes-debug-*` containers or the real `jnotes` database in manual runs.

## Steps

### Phase 1: Core, SQL-side limit
1. `src/JNotes.Core/Notes/NoteSearchQuery.cs`: append `int? Limit = null` as the 5th, trailing positional parameter, with an XML doc: "at most this many rows, newest first, applied in SQL after every filter; `null` = no limit; must be >= 1."
2. `src/JNotes.Core/Indexing/PostgresNoteIndexRepository.cs`, `SearchAsync`:
   - At the top: `if (query.Limit is < 1) throw new ArgumentOutOfRangeException(nameof(query), query.Limit, "Limit must be at least 1.");`
   - Replace `.OrderByDescending(e => e.UpdatedAt)` with `.OrderByDescending(e => e.UpdatedAt).ThenByDescending(e => e.Id)` assigned to a variable, then `IQueryable<NoteIndexEntry> page = query.Limit is { } limit ? ordered.Take(limit) : ordered;`, then the existing `Select(...)` unchanged on `page`. The `ThenBy` makes ties stable so `LIMIT` is deterministic.
3. `src/JNotes.Core/Indexing/INoteIndexRepository.cs`: extend the `SearchAsync` summary ("newest first, ties by id; at most `query.Limit` rows when set").
4. `src/JNotes.Core/Notes/INoteService.cs`: extend the `ListAsync` summary ("Honours `NoteSearchQuery.Limit`, SQL-side"). `NoteService` is unchanged.

### Phase 2: Web, data loading in `MainLayout`
5. `src/JNotes.Web/Components/Layout/MainLayout.razor`:
   - Add `@inject INoteService NoteService` and `@inject ILogger<MainLayout> Logger`.
   - Add fields `private const int RecentNotesCount = 10;`, `private IReadOnlyList<NoteSummary>? _recentNotes;`, `private CancellationTokenSource? _recentCts;`, `private bool _disposed;`.
   - Extend the existing comment block: `INoteService` is injected here at circuit start for the same `ICurrentUserContext` reason, and is only *called* after an auth check.
   - In `OnInitializedAsync`, inside the existing `IsAuthenticated` branch after the user lookup: `await RefreshRecentAsync();`.
   - Add:
     ```csharp
     // Re-checks auth every time: a deleted/demoted user's open circuit flips via the revalidating provider,
     // and a deleted user's index rows survive (CLAUDE.md, Note sharing), so never trust a cached flag.
     private async Task RefreshRecentAsync()
     {
         _recentCts?.Cancel();
         _recentCts?.Dispose();
         var cts = _recentCts = new CancellationTokenSource();
         try
         {
             var auth = await AuthenticationStateProvider.GetAuthenticationStateAsync();
             if (auth.User.Identity?.IsAuthenticated != true) { _recentNotes = null; return; }
             var notes = await NoteService.ListAsync(new NoteSearchQuery(Limit: RecentNotesCount), cts.Token);
             if (!cts.IsCancellationRequested) _recentNotes = notes;
         }
         catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
         catch (Exception ex) { Logger.LogWarning(ex, "Could not load recent notes for the sidebar."); } // keep previous list; never kill the circuit
     }
     ```
     Log no titles or ids.
   - Rewrite `OnLocationChanged` so one handler does both jobs (drawer logic unchanged):
     ```csharp
     private void OnLocationChanged(object? sender, LocationChangedEventArgs e) => _ = InvokeAsync(async () =>
     {
         try
         {
             if (_drawerOpen && await Viewport.GetCurrentBreakpointAsync() is Breakpoint.Xs or Breakpoint.Sm)
             {
                 _drawerOpen = false;
                 StateHasChanged();
             }
         }
         catch (JSDisconnectedException) { }

         await RefreshRecentAsync();
         if (!_disposed) StateHasChanged();
     });
     ```
     Cancelling `_recentCts` collapses rapid navigations into one in-flight query. Query-string-only changes (`/?q=`) also re-query, per "every navigation".
   - In `Dispose`: `_disposed = true; _recentCts?.Cancel(); _recentCts?.Dispose();`, keeping the existing unsubscribe.
   - Pass the list down: `<NavMenu DisplayName="@_displayName" Initials="@_initials" RecentNotes="_recentNotes" />`.

### Phase 3: Web, rendering
6. New `src/JNotes.Web/Components/Layout/RecentNotes.razor` (injects nothing; header comment: data comes from MainLayout, see the `ICurrentUserContext` gotcha; highlighting is `MudNavLink`'s own Prefix match so `/notes/{id}/edit` counts as open):
   - `[Parameter] public IReadOnlyList<NoteSummary>? Notes { get; set; }`; render nothing when `Notes is null or { Count: 0 }`.
   - Otherwise:
     ```razor
     <div class="jn-nav-recent">
         <MudDivider Class="my-2" />
         <h2 class="jn-nav-recent-heading" id="jn-nav-recent-heading">Recent</h2>
         <MudNavMenu Dense="true" Class="jn-nav-recent-list" aria-labelledby="jn-nav-recent-heading">
             @foreach (var note in Notes)
             {
                 <MudNavLink @key="note.Id" Href="@($"/notes/{note.Id}")"
                             Icon="@(note.Type == NoteType.Checklist ? Icons.Material.Outlined.Checklist : Icons.Material.Outlined.Description)"
                             title="@DisplayTitle(note)">@DisplayTitle(note)</MudNavLink>
             }
         </MudNavMenu>
     </div>
     ```
     with `private static string DisplayTitle(NoteSummary n) => string.IsNullOrWhiteSpace(n.Title) ? "Untitled" : n.Title;`.
   - `@key` on `note.Id`, not the index. If `aria-labelledby` doesn't reach the `<nav>`, use `aria-label="Recent notes"`. If `title` doesn't land usefully, drop it.
7. `src/JNotes.Web/Components/Layout/NavMenu.razor`:
   - Add `[Parameter] public IReadOnlyList<NoteSummary>? RecentNotes { get; set; }`.
   - Between `</MudNavMenu>` and the AccountMenu comment:
     ```razor
     @* Signed-in only: NavMenu also renders for NotFound/Error ([AllowAnonymous]). MainLayout never loads the
        list for an anonymous user either; this is the markup half. *@
     <AuthorizeView Context="auth">
         <RecentNotes Notes="RecentNotes" />
     </AuthorizeView>
     ```
   - Add `aria-label="Main navigation"` to the existing `<MudNavMenu Class="jn-nav-list">`.
   - Do not add a `LocationChanged` subscription to NavMenu.

### Phase 4: CSS (`src/JNotes.Web/wwwroot/css/nav-menu.css`)
8. Update the header comment to mention the Recent list (`.jn-nav-recent`).
9. After the `.jn-nav-new` block:
   ```css
   /* "Recent" (RecentNotes.razor): up to 10 compact MudNavLinks under Trash. Titles are one line, ellipsised. */
   .jn-nav-recent {
       min-width: 0;

       & .jn-nav-recent-heading {
           margin: 0.25rem 16px 0.25rem;
           font-size: 0.75rem;
           font-weight: 500;
           letter-spacing: 0.08em;
           text-transform: uppercase;
           color: var(--mud-palette-text-secondary);
       }

       & .mud-nav-link { min-width: 0; }

       & .mud-nav-link-text {
           min-width: 0;
           overflow: hidden;
           text-overflow: ellipsis;
           white-space: nowrap;
       }
   }
   ```
   Confirm the real inner class names in devtools and adjust (UNVERIFIED: `mud-nav-link-text`).

### Phase 5: docs (`CLAUDE.md`) — **DONE by the planner/orchestrator after user approval; executor must not edit it** (re-check the wording against the final code)
10. Sidebar section: append "Recent" to the **Order** bullet; add a "Recent notes" bullet (MainLayout loads `ListAsync(new NoteSearchQuery(Limit: 10))` in `OnInitializedAsync` and on every `LocationChanged`, cancels in-flight loads, re-checks auth, keeps the old list on error; passes it to `NavMenu` → `RecentNotes.razor`; highlight via `MudNavLink` Prefix match; no refresh without navigation; `h2` because of `FocusOnNavigate`); reword "No active-route indication" so it is clearly about the New Note button only.
11. Note sharing → "Listing and search": mention `NoteSearchQuery`'s 5th parameter `Limit`.
12. Architecture → "`ICurrentUserContext` gotcha": one sentence that `MainLayout` also injects `INoteService` and only calls it after an `IsAuthenticated` check.

## Tests to add or update
Integration tests (real PostgreSQL via `CoreServicesFixture`):
- `tests/JNotes.Tests/Integration/NoteIndexRepositoryTests.cs` (use the existing `NewEntry(..., updatedAt:)` helper):
  - `SearchAsync_with_a_limit_returns_only_the_newest_rows_in_order`: 12 entries for A, `updatedAt = now - i minutes`, `Limit: 10` gives the 10 newest ids descending; no limit gives 12.
  - `SearchAsync_applies_the_limit_after_filters`: 3 entries match `SearchText` plus newer non-matching ones; `Limit: 2` returns the 2 newest *matching*.
  - `SearchAsync_breaks_UpdatedAt_ties_deterministically`: 3 entries with identical `updatedAt`; `Limit: 2` returns the same ids twice, ordered by `Id` descending.
  - `[Theory] [InlineData(0)] [InlineData(-1)] SearchAsync_rejects_a_limit_below_one`: `ArgumentOutOfRangeException`.
- `tests/JNotes.Tests/Integration/NoteSharingTests.cs` (use `CreateSharedWithBAsync` and `Task.Delay(20)` like the existing listing test):
  - `A_limited_listing_merges_own_and_shared_notes_newest_first`: A shares one note with B; B creates 2 of their own; A also has an unshared note. As B, `Limit: 2` gives the 2 newest visible notes (never A's unshared one); `Limit: 10` gives all 3, with `SharedWithMe` set on the shared row.
  - `A_trashed_or_left_note_drops_out_of_a_limited_listing`: after A trashes the shared note it is gone from B's listing; after restore and B's `LeaveAsync`, gone again.
- No Web tests (no bUnit). Existing tests must pass unchanged.
- Run: `dotnet test tests/JNotes.Tests --filter "FullyQualifiedName~NoteIndexRepositoryTests|FullyQualifiedName~NoteSharingTests"`, then the full suite.

## Manual verification
**Setup:** throwaway database and folder, never `jnotes` or `App_Data/notes`; don't stop or recreate `jnotes-debug-*` containers; `OLLAMA_URL` empty.

```powershell
docker exec jnotes-debug-db createdb -U jnotes jnotes_manual_recent   # "already exists" error is fine
$env:ASPNETCORE_ENVIRONMENT="Development"; $env:ASPNETCORE_URLS="http://localhost:5231"
$env:POSTGRES_HOST="localhost"; $env:POSTGRES_PORT="5432"; $env:POSTGRES_DB="jnotes_manual_recent"
$env:POSTGRES_USER="jnotes"; $env:POSTGRES_PASSWORD="jnotes"; $env:TZ="Australia/Melbourne"
$env:NOTES_PATH="C:\Users\Joel\AppData\Local\Temp\jnotes-manual-recent\notes"
$env:DATA_PROTECTION_KEYS_PATH="C:\Users\Joel\AppData\Local\Temp\jnotes-manual-recent\keys"; $env:OLLAMA_URL=""
dotnet build JNotes.slnx            # 0 errors, no new warnings
dotnet format JNotes.slnx --verify-no-changes
dotnet test JNotes.slnx
dotnet run --project src/JNotes.Web --no-launch-profile
```

Open http://localhost:5231, complete `/setup` (admin `alice`), add Member `bob` in Administration, use a second browser profile/incognito for bob. Keep the devtools console open: no errors, and the "An unhandled error has occurred" bar must never appear.

1. **Empty state:** no notes, no "Recent" heading and no divider below Trash.
2. **Max 10 and ordering:** create 12 text notes R1..R12 in order. Exactly 10 links, R12 at top down to R3, text icon, order matches top of All Notes.
3. **Checklist icon:** create checklist "C1": it is on top with a checklist icon and R3 drops off.
4. **Highlight:** click R5; `/notes/{id}`, R5 highlighted (`aria-current="page"`). Click Edit (`/edit`): still highlighted. Go to `/`: nothing in Recent highlighted, All Notes is.
5. **Refresh on navigation + Metadata save:** rename R5 to "R5 renamed", wait ~2 s, Close; it is at the top. (A body-only edit may not reorder: accepted `ContentOnly` staleness.)
6. **Untitled:** a note with an empty `title:` (hand-edit a `.md`, Rescan Now, navigate) reads "Untitled".
7. **Long title:** ~120 chars shows one line with ellipsis, no horizontal scrollbar, full title on hover (if `title` landed).
8. **Trash:** open a note in Recent, Delete, confirm: gone from Recent. Restore from `/trash`: Recent changes only after the next navigation (expected).
9. **Shared:** alice shares R10 with bob (Read). In bob's window navigate: R10 appears and highlights when opened. Bob Leaves: after `/shared`, R10 gone. alice's unshared notes never appear for bob.
10. **New-note draft:** New Note > Text: no Recent item highlighted. Save with title: new note on top, highlighted on its view page.
11. **Rapid navigation:** click quickly through 6-8 Recent links; list settles, no console errors.
12. **Search:** Enter in search box works, Recent content unchanged.
13. **Mobile (390x844):** Recent under Trash; account widget still bottom-anchored; tapping an item navigates, closes the drawer, item highlighted on reopen. At 390x600 the drawer scrolls, nothing clipped.
14. **Desktop short (1280x600):** drawer scrolls if needed, New Note dropdown still opens.
15. **Accessibility:** two `<nav>` landmarks ("Main navigation", "Recent notes" or `aria-labelledby`), heading is `h2`, Tab reaches each link and Enter opens it, focus goes to the page `h1` after navigation.
16. **Anonymous:** signed out, `/some-missing-page` has no Recent (and no search/New Note), clean console; `/login` renders normally.
17. **Revoked user:** alice deletes bob while his window is open; bob's next navigation or revalidation tick sends him to `/login`, no stale Recent.

## Risks and edge cases
- Prefix-match highlight relies on `NavLink`'s separator check. If MudBlazor 9 differs, add `Match="NavLinkMatch.Prefix"` explicitly; if that fails, pass the open note id from `MainLayout` (parse `Navigation.ToBaseRelativePath(e.Location)`) and set the active class manually.
- Race with the editor's dispose flush: Recent may show the pre-flush order until the next navigation. Accepted.
- No refresh without navigation (Restore/Purge on `/trash`, checklist toggles, other users' edits, Rescan Now). Per decision.
- Prerender double load: one extra small query. Keep the call in `MainLayout`.
- Deleted users' index rows survive; the per-refresh auth check plus the revalidating provider bound exposure.
- `Limit` of 0 now throws; only the new call site uses it.
- `ThenByDescending(Id)` changes tie order in all listings (previously undefined); mention in the commit.
- 10 more rows can push the mobile account widget below the fold; the drawer must scroll (checks 13-14).

## Out of scope
- Autosave/`ContentOnly` behaviour, `NoteEditor`, `NoteView`, `NoteListItem` wording.
- Timers, notification services, SignalR push.
- Changes to `NoteSummary`, index schema, migrations, `AccountMenu`/New Note markup.
- Share chips, dates or tags in Recent; a per-user count setting.
- README and other `docs/plans/*`.
- Touching `jnotes-debug-*` containers or the `jnotes` database / `App_Data/notes`.

## Handoff to executor
- [ ] Phase 1: `Limit` on `NoteSearchQuery`; `SearchAsync` validates it, orders `UpdatedAt` desc then `Id` desc, `Take` before `Select`; update doc comments.
- [ ] Add the 4 repository tests and 2 sharing tests; run `dotnet test` (uses `jnotes-debug-db`).
- [ ] Phase 2: `MainLayout` changes (inject, `RefreshRecentAsync`, merged `OnLocationChanged`, dispose guard, pass `RecentNotes`).
- [ ] Phase 3: `RecentNotes.razor`; `NavMenu` parameter, `AuthorizeView` block before `AccountMenu`, `aria-label` on the main nav.
- [ ] Phase 4: `.jn-nav-recent` CSS; verify inner class names in devtools if a browser is available, otherwise flag for tester.
- [ ] **Skip Phase 5 (CLAUDE.md)** pending user approval.
- [ ] `dotnet build JNotes.slnx`, `dotnet format JNotes.slnx --verify-no-changes`, `dotnet test JNotes.slnx` all clean.
- [ ] Do not commit. Return a handoff for the tester listing changed files and any deviations.

## Handoff to tester
- [ ] Throwaway DB `jnotes_manual_recent` and temp `NOTES_PATH`, `--no-launch-profile`; containers untouched.
- [ ] Build, format and test commands pass.
- [ ] Manual checks 1-17.
- [ ] Console free of errors and no "unhandled error" bar throughout.
- [ ] Report the UNVERIFIED items (`<nav>` aria passthrough, `mud-nav-link-text`, `/edit` highlight) with evidence. Never edit source.
