# Plan: Replace the two sidebar "New … Note" links with one filled "New Note" dropdown button

## Goal
The sidebar currently has two separate links, "New Text Note" and "New Checklist Note". They become one filled primary "New Note" button, placed right under the search box and above "All Notes". It opens a dropdown with two items, "Text" and "Checklist", which go to `/notes/new/text` and `/notes/new/checklist`. As before, nothing is created until Save. The button stays signed-in-only, and while a new-note draft is open it gets an outline ring so you can tell you're on that route.

## Context found
- `src/JNotes.Web/Components/Layout/NavMenu.razor` (98 lines):
  - Lines 5–14: an unqualified `<AuthorizeView>` around `div.jn-nav-search` (a `MudTextField` with `@onclick="ClearSearch"`).
  - Lines 16–35: a `MudNavMenu Class="jn-nav-list"` holding All Notes, a nested `<AuthorizeView>` (divider, the two `MudNavLink`s with `OnClick="() => NewNote(...)"` and `Class="@(IsNewNoteRouteActive(...) ? "jn-nav-active" : null)"`, divider), then Trash.
  - `@code`: `OnInitialized` subscribes to `NavigationManager.LocationChanged` → `InvokeAsync(StateHasChanged)`; `Dispose` unsubscribes. It also has `NewNote(NoteType)`, `IsNewNoteRouteActive(NoteType)`, and the search handlers.
- `src/JNotes.Web/wwwroot/css/nav-menu.css`: the header comment (lines 1–4) mentions `.jn-nav-active`; lines 113–130 are the `.jn-nav-active > .mud-nav-link` rules and their comment. Those rules exist only for the two links being removed. `.jn-nav-search { margin-bottom: 0.5rem; }` is at lines 41–43.
- Pattern to copy: `src/JNotes.Web/Components/Layout/AccountMenu.razor`.
  - It uses `MudMenu` with `ActivatorContent` + `ChildContent`, toggles via `e => context.ToggleAsync(e)`, and uses `MudMenuItem Href=...` for navigation.
  - Its `AuthorizeView` is renamed with `Context="auth"` so it doesn't collide with `ActivatorContent`'s `context`. NavMenu must do the same, or you get a Razor compile error about a duplicate `context` name.
- `MainLayout.razor`:
  - It hosts `MudPopoverProvider` (line 11). That puts the menu's popover outside the drawer's DOM, so the drawer can't clip it.
  - Its `OnLocationChanged` closes the drawer below md on any navigation. This works for `MudMenuItem Href` navigation with no changes.
- MudBlazor is **9.10.0** (`JNotes.Web.csproj` line 16). Checked against `~/.nuget/packages/mudblazor/9.10.0/lib/net10.0/MudBlazor.xml`:
  - `MudMenu`: `Label`, `AriaLabel`, `ListClass`, `PopoverClass`, `Icon`, `StartIcon`, `EndIcon`, `Color`, `Size`, `Variant` (default Text), `Dense`, `FullWidth` ("Expands this menu to the same width as its parent"), `ActivatorContent` (a `MenuContext` with `OpenAsync`/`CloseAsync`/`ToggleAsync(EventArgs)`/`CloseAllAsync`), `AnchorOrigin` (default null), `TransformOrigin` (default `Origin.TopLeft`), `RelativeWidth` (`DropdownWidth.Relative|Adaptive|Ignore`; `Adaptive` sets the popover's min-width to the parent width), `Open`/`OpenChanged`, `ChildContent`.
  - `MudMenuItem`: `Label`, `ChildContent`, `Href`, `Target`, `ForceLoad`, `Icon`, `IconColor`, `AutoClose`, `OnClick`.
- **About "a MudMenu inside MudNavMenu didn't work in practice" (CLAUDE.md, Sidebar section):** in this design the `MudMenu` is not inside `MudNavMenu`. It sits in its own `div` between the search box and `MudNavMenu`, as a sibling, just like `AccountMenu`'s `MudMenu` sits in the app bar. Nothing is inherited from `MudNavMenu` (nav-link styling, click handling). The popover renders through `MudPopoverProvider`, and that pattern already works in `AccountMenu`. The original failure could not be reproduced without a browser, so it must be confirmed in the browser checks below.
- Project rules that apply: nav labels are title case; icons come from `Icons.Material.*`; new CSS goes in the existing concern file (`nav-menu.css`); no bUnit tests, so UI is checked by hand; `dotnet format --verify-no-changes` must pass.

## Steps

### Phase 1: markup (`src/JNotes.Web/Components/Layout/NavMenu.razor`)

1. **Rename the top AuthorizeView's context and move the new button into it.** Replace lines 5–14 with a single `<AuthorizeView Context="auth">` holding:
   - the existing `div.jn-nav-search` block, unchanged (its comment and `@onclick="ClearSearch"` stay as they are);
   - directly after it, a new block:
     ```razor
     @* One "New Note" button (filled, primary) with a Text/Checklist dropdown. Sits OUTSIDE MudNavMenu on
        purpose: a MudMenu nested inside MudNavMenu didn't work in practice. As a sibling it renders like
        AccountMenu's MudMenu, with its popover in MainLayout's MudPopoverProvider. Items only navigate:
        NoteEditor's /notes/new/{type} route holds an in-memory draft until Save. The AuthorizeView's
        Context is "auth" so it can't collide with ActivatorContent's own `context`. *@
     <div class="@(IsAnyNewNoteRouteActive ? "jn-nav-new jn-nav-new-active" : "jn-nav-new")">
         <MudMenu FullWidth="true" RelativeWidth="DropdownWidth.Adaptive"
                  AnchorOrigin="Origin.BottomLeft" TransformOrigin="Origin.TopLeft"
                  PopoverClass="jn-nav-new-popover">
             <ActivatorContent>
                 <MudButton Variant="Variant.Filled" Color="Color.Primary" FullWidth="true"
                            StartIcon="@Icons.Material.Filled.Add" EndIcon="@Icons.Material.Filled.ArrowDropDown"
                            aria-haspopup="menu" OnClick="e => context.ToggleAsync(e)">New Note</MudButton>
             </ActivatorContent>
             <ChildContent>
                 <MudMenuItem Href="/notes/new/text" Icon="@Icons.Material.Outlined.NoteAdd"
                              IconColor="@(IsNewNoteRouteActive(NoteType.Text) ? Color.Primary : Color.Inherit)">Text</MudMenuItem>
                 <MudMenuItem Href="/notes/new/checklist" Icon="@Icons.Material.Outlined.Checklist"
                              IconColor="@(IsNewNoteRouteActive(NoteType.Checklist) ? Color.Primary : Color.Inherit)">Checklist</MudMenuItem>
             </ChildContent>
         </MudMenu>
     </div>
     ```
   Why `ActivatorContent` with our own `MudButton`, not `Label`/`Variant` on `MudMenu`? The docs only promise that `MudMenu.FullWidth` widens the menu, not that the built-in button fills it. Our own `MudButton FullWidth="true"` does fill it, and it matches the `AccountMenu` pattern. `MudMenu FullWidth="true"` is still needed so the menu's wrapper fills `.jn-nav`.

   Why `Href`, not `OnClick`? `AccountMenu` already navigates with `MudMenuItem Href` (for example `/account`), and `Href` also allows middle-click to open in a new tab. The paths must match the ones `NewNote` built (`type.ToString().ToLowerInvariant()` gives `text`/`checklist`, which `NoteEditor`'s `@page "/notes/new/{TypeName}"` accepts).

   If `IconColor="Color.Inherit"` fails to compile or looks wrong, use `Color.Default`.

2. **Rebuild `MudNavMenu`** (old lines 16–35) as:
   ```razor
   <MudNavMenu Class="jn-nav-list">
       <MudNavLink Href="/" Match="NavLinkMatch.All" Icon="@Icons.Material.Outlined.StickyNote2">All Notes</MudNavLink>
       <MudDivider Class="my-2" />
       <MudNavLink Href="/trash" Icon="@Icons.Material.Outlined.DeleteOutline">Trash</MudNavLink>
   </MudNavMenu>
   ```
   Delete the old comment block (lines 18–25), the nested `<AuthorizeView>`, both `MudNavLink`s, and the second divider. The one divider is now unconditional (see Open questions); anonymous visitors on NotFound/Error see All Notes / divider / Trash, which is harmless.

3. **Update `@code`:**
   - Delete `NewNote(NoteType)` (lines 63–66). Its callers are gone and `Href` does the navigating.
   - Keep `IsNewNoteRouteActive(NoteType)`, but change its comment to: "Mirrors the `/notes/new/{type}` routes the New Note menu items link to, so the button and the current item can show that a draft is open."
   - Add:
     ```csharp
     private bool IsAnyNewNoteRouteActive => IsNewNoteRouteActive(NoteType.Text) || IsNewNoteRouteActive(NoteType.Checklist);
     ```
   - Keep the `OnInitialized` `LocationChanged` subscription and `Dispose`. Change the comment's "(and therefore the "New Text Note"/"New Checklist Note" highlight)" to "(and therefore the New Note button's active ring)".
   - Search handlers stay unchanged.
   - `NoteType` is still used, so no `@using` changes. Check it builds.

### Phase 2: CSS (`src/JNotes.Web/wwwroot/css/nav-menu.css`)

4. **Header comment (lines 1–4):** change "except .jn-nav-active below" to "except the New Note dropdown button (.jn-nav-new) below".
5. **Delete** lines 113–130: the `.jn-nav-active` comment and both rules. Nothing uses them any more; grep for `jn-nav-active` should come back empty afterwards.
6. **Add** (placed after `.jn-nav-search`, using native nesting like the rest of the file):
   ```css
   /* "New Note" (NavMenu.razor): a filled MudButton activating a MudMenu, kept outside MudNavMenu.
      .jn-nav-new-active marks that a /notes/new/{type} draft is open (the button has no route of its
      own, so there is no MudBlazor "active" state to reuse). */
   .jn-nav-new {
       margin-bottom: 0.5rem;

       &.jn-nav-new-active .mud-button-root {
           outline: 2px solid var(--mud-palette-primary-lighten);
           outline-offset: 2px;
       }
   }
   ```
   Only add a `.jn-nav-new-popover` rule if the browser check shows the popover needs it (for example `min-width`). Otherwise leave `PopoverClass` as a hook with no CSS.

### Phase 3: docs (`CLAUDE.md`, Sidebar section) — PROPOSED, NOT TO BE EXECUTED WITHOUT USER APPROVAL
The planner suggested updating CLAUDE.md so it stops describing the two links. The executor must **not** edit CLAUDE.md unless the user approves this phase explicitly.

7. Replace the **Order** bullet with: "**Order:** Search, **New Note** (a filled primary button with a Text/Checklist dropdown), All Notes, divider, Trash. Search and New Note share one unqualified `<AuthorizeView Context="auth">` outside `MudNavMenu`." Keep the rest of that bullet (icons, vendored deps, search field clearing, title case).
8. Rewrite the **"New Text Note"/"New Checklist Note"** bullet as a **"New Note"** bullet covering: it lives in `NavMenu` so it works from any page; `MudMenu` with a `Variant.Filled`/`Color.Primary` `MudButton` activator and two `MudMenuItem Href`s; nothing created until Save; sits outside `MudNavMenu` (a nested `MudMenu` didn't work in practice); `AuthorizeView Context="auth"` to avoid the `context` collision, and still an `AuthorizeView` because `NavMenu` renders for NotFound/Error; active indication via `IsNewNoteRouteActive` + `jn-nav-new-active`. Remove all `jn-nav-active` / `.mud-nav-item` / `LinkClassname` text.

## Tests to add or update
None automated. The change is Web-only markup and CSS, and the repo has no bUnit tests (per CLAUDE.md). Core is unchanged, so the existing `dotnet test` suite only confirms nothing regressed.

## Manual verification (for the tester)
Setup:
- Use a throwaway database and folder, never the user's `jnotes` database or `App_Data/notes`. Set `POSTGRES_DB=jnotes_manual_newnote` (the app migrates at startup; check whether the DB must exist first — `docker exec jnotes-debug-db createdb -U jnotes jnotes_manual_newnote` is harmless if it already exists) and point `NOTES_PATH` at a scratch folder. Leave `OLLAMA_URL` empty.
- Do not stop or recreate the `jnotes-debug-*` containers.

Commands:
1. `dotnet build JNotes.slnx` → 0 errors, 0 new warnings. Watch especially for BannedApi warnings and duplicate-`context` Razor errors.
2. `dotnet format JNotes.slnx --verify-no-changes` → exit 0.
3. `dotnet test JNotes.slnx` → all pass (uses `jnotes-debug-db` on localhost:5432).
4. `dotnet run --project src/JNotes.Web` with the overrides above. Complete `/setup` to create an admin, then sign in.

Browser checks (open devtools console; it must stay free of errors throughout, and the "An unhandled error has occurred" bar must never appear):
5. **Desktop (≥ 960px)**, on `/`: the sidebar shows, in order, the search box, a full-width filled primary "New Note" button with a + icon and dropdown arrow, All Notes, a divider, Trash. There is no "New Text Note" or "New Checklist Note". The button has no outline ring.
6. Click New Note. A dropdown opens under the button, at least as wide as the button, with "Text" and "Checklist" items and their icons. It is not clipped by the drawer and appears above the main content.
7. Click Text. The URL becomes `/notes/new/text` and the new-text-note editor shows (empty title/tags/body). The menu closes. The button now has an outline ring. Reopen the menu: the Text icon is primary-coloured and the Checklist icon is not. No note file is created (the list at `/` is unchanged).
8. Open the menu and choose Checklist. The URL is `/notes/new/checklist`, the checklist editor shows, and the ring stays. Reopen: now the Checklist icon is highlighted.
9. Type a title in the draft, then choose New Note → Text. The existing "discard draft?" confirmation (NavigationLock) appears. Cancel keeps you on the page and the menu is closed.
10. Click All Notes. The ring disappears and All Notes gets MudBlazor's normal active style.
11. Open the menu, then press Escape or click elsewhere. The menu closes with no navigation.
12. Search: click the search box and it clears. Type a term and press Enter: you land on `/?q=term` and the New Note button is unaffected. The search box still clears when clicked again.
13. Middle-click Text in the menu: it opens `/notes/new/text` in a new tab, which works when signed in.
14. **Mobile (devtools, 390px wide):** the drawer is hidden. Open it with the app-bar menu button. New Note is under the search box, full width. Open the dropdown: it appears above the drawer overlay and can be clicked. Tap Checklist: you go to `/notes/new/checklist`, **the drawer closes**, and the menu isn't left open. Reopen the drawer: the button shows the ring. The account widget is still bottom-anchored in the drawer.
15. **Unauthenticated:** sign out, then open `/some-missing-page` (NotFound). The sidebar shows All Notes / divider / Trash, with no search box and no New Note button, and no console errors. `/login` renders normally (static SSR, no sidebar).
16. Keyboard: Tab to New Note, press Enter or Space and the menu opens; arrow keys or Tab reach the items, and Enter navigates.

## Risks and edge cases
- **`context` name collision:** if `Context="auth"` is left off the outer `AuthorizeView`, Razor fails to compile (`ActivatorContent` also defines `context`). Step 1 handles this.
- **Popover z-index on mobile:** the drawer overlay and the popover are both MudBlazor layers (the popover normally sits above the drawer). If the dropdown appears behind the overlay, add `.jn-nav-new-popover { z-index: calc(var(--mud-zindex-drawer) + 2); }` or similar. This is check 14.
- **The earlier "MudMenu didn't work" failure:** its cause wasn't recorded. The design avoids the nesting, but if clicks on items don't navigate, try `OnClick` + `NavigationManager.NavigateTo` (bring `NewNote` back) instead of `Href`. `AutoClose` defaults to closing the menu.
- **Picking the same route again** (Text while already on `/notes/new/text`): this behaves exactly as today's `NavigateTo`. The NavigationLock prompts if the draft has content; if it has none, the draft is rebuilt, and it was empty anyway. No change.
- **`MudMenu.FullWidth` + `RelativeWidth.Adaptive`:** if the popover comes out narrower or wider than expected, drop `RelativeWidth`. This is cosmetic only.
- **`IconColor="Color.Inherit"`:** if it renders oddly next to the default item colour, use `Color.Default`.
- **The ring colour** `--mud-palette-primary-lighten` is emitted at runtime by `MudThemeProvider` (like the other palette variables). If it's undefined in the dark palette, fall back to `var(--mud-palette-primary)`.

## Open questions
- **Divider between All Notes and Trash:** keep one unconditional divider (recommended default). Alternatives are no divider, or wrapping it in `AuthorizeView`; the difference is cosmetic.
- **Showing the draft is open:** outline ring on the button plus a primary icon on the current item (recommended). Alternative: no indication, since the editor page already makes it obvious.

## Out of scope
- `NoteEditor.razor` (draft, Save, NavigationLock logic), `MainLayout.razor` (drawer auto-close already covers this), `AccountMenu.razor`, and anything in Core or Tests.
- Search behaviour (`ClearSearch`, `OnSearchKeyDown`) must stay as it is.
- Don't add a "New Note" button to `NoteList.razor`, and don't touch other `docs/plans/*.md` or README.
- Don't stop, remove or recreate the `jnotes-debug-*` containers, and don't use the real `jnotes` database or `App_Data/notes` for the manual run.

## Files involved
- `src/JNotes.Web/Components/Layout/NavMenu.razor`
- `src/JNotes.Web/wwwroot/css/nav-menu.css`
- `CLAUDE.md` (Phase 3 only, pending approval)
- Reference only: `AccountMenu.razor`, `MainLayout.razor`, `NoteEditor.razor`, MudBlazor 9.10.0 `MudBlazor.xml`
