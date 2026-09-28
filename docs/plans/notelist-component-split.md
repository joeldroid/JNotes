# Plan: Split `NoteList.razor` into a thin page + `NoteListView` / `NoteGroupList` components

## Goal

Nothing changes for the user: `/` (with `?q=`) looks and behaves exactly as today. Internally,
`src/JNotes.Web/Components/Pages/NoteList.razor` becomes a thin routed page that owns only routing, the `?q=`
parameter, `INoteService` data loading and filter state, and delegates all markup to two new reusable components in
`Components/Notes/`:

- `NoteListView` — search header + Type/Tag filters + list region.
- `NoteGroupList` — the relative-date `MudExpansionPanels` sections and the collapsed-group state.

Pure refactor, no feature changes.

## Context found

- `Components/Pages/NoteList.razor` — 139 lines; `@page "/"`, `@attribute [Authorize]`, injects `INoteService` and
  `IAppTimeZoneProvider`. Contains the "Search results for … · Clear" header, `.jn-filters` with two `MudSelect T="string"`,
  the loading / empty / `MudExpansionPanels` block, and `@code` with `Query`, `_notes`, `_groups`, `_availableTags`,
  `AllTypes = "all"`, `AllTags = ","`, `_typeFilter`, `_tagFilter`, `_collapsedGroups`, `OnInitializedAsync` (tags),
  `OnParametersSetAsync` → `RefreshAsync`, `OnTypeFilterChangedAsync`, `OnTagFilterChangedAsync`, `SetGroupExpanded`.
- `Components/Notes/NoteListItem.razor` — `[Parameter, EditorRequired] NoteSummary Note`; injects `IAppTimeZoneProvider`
  itself. Unchanged.
- `Components/Notes/ChecklistPanel.razor` — conventions to mirror: leading `@* … *@` comment block, `[Parameter, EditorRequired]`
  for required data, `EventCallback<T> OnXxx` for actions, everything inline in `@code` (there are **no** `.razor.cs`
  code-behind files anywhere in Web).
- `JNotes.Core/Notes/NoteGrouping.cs` + `NoteGroup.cs` (`sealed record NoteGroup(string Label, IReadOnlyList<NoteSummary> Notes)`)
  — pure static, **stays where it is**. It drops empty buckets, so `GroupByRelativeDate` returns an empty list **iff** the
  input is empty. That is what lets the page pass groups (not notes) down while keeping the loading/empty states identical.
- `Components/_Imports.razor` already imports `JNotes.Web.Components.Notes`, `JNotes.Core.Notes`, `JNotes.Core.Models`,
  `JNotes.Core.Time`, `MudBlazor`. **No `_Imports` change needed.**
- `wwwroot/css/note-list.css` — global stylesheet (`.jn-filters`, `.jn-group-count`, `.jn-note-list`, `.jn-note-item`, …),
  also used by `Pages/Trash.razor`. **It has uncommitted local modifications — do not revert them.** `note-view.css` is also
  locally modified and unrelated.
- Other references to `NoteList`: `Layout/NavMenu.razor:103` (a comment, still true → no edit),
  `CLAUDE.md` (see Step 5), `docs/plans/mermaid-flowcharts-in-text-notes.md` (historical, leave alone), and
  `tests/JNotes.Tests/Notes/NoteGroupingTests.cs` (tests Core only → no change). No bUnit.

Constraints from CLAUDE.md the refactor must respect:

- `ICurrentUserContext` gotcha: the routed page must resolve/use `INoteService` from its **first** lifecycle method.
- `?q=` is read with `[SupplyParameterFromQuery(Name = "q")]` and refreshed from `OnParametersSetAsync`.
- `_collapsedGroups` must be owned by a component that is not destroyed on re-render.
- Type/Tag "no filter" values are explicit sentinels (`MudSelect` can't label an empty-string value).
- Web must not touch filesystem/EF/Redis directly. The new components add no service dependencies.

## Design decision (drives every step)

**The page keeps data loading, `INoteService`, `IAppTimeZoneProvider`, the `?q=` parameter and the filter *values*; the two
new components are presentational and inject nothing.**

1. `AuthenticatedCurrentUserContext` misresolves unless `INoteService` is touched from the routed page's first lifecycle
   method. Do **not** inject `INoteService` into `NoteListView`/`NoteGroupList`.
2. `?q=` is a routing concern (`[SupplyParameterFromQuery]` only works on a routable component), and the refresh needs the
   current filter values, so those live beside it in the page.
3. `NoteGrouping.GroupByRelativeDate(...)` stays in `RefreshAsync` in the page, so the "now" snapshot is taken at the same
   moment as today.

**Collapsed-group state lives in `NoteGroupList`** and survives filter re-renders because `NoteGroupList` is rendered
**unconditionally** by `NoteListView` and renders its own loading / empty / panels branches *internally*. A component
instance survives changes to its own internal `@if` branches, so the `HashSet` is never thrown away, including when a filter
matches zero notes and back again. Do **not** wrap `<NoteGroupList …/>` in an `@if` in `NoteListView`.

## Steps

### Step 1 — Create `src/JNotes.Web/Components/Notes/NoteGroupList.razor`

Markup moved verbatim from `NoteList.razor` lines 33–67, plus `_collapsedGroups` and `SetGroupExpanded` (lines 84–86, 128–138).

```razor
@* The relative-date sections of the notes list (Today/Yesterday/This week/…), as multi-expand
   MudExpansionPanels. Purely presentational: it renders the NoteGroups it's handed — grouping
   itself is NoteGrouping.GroupByRelativeDate in Core, called by the page that loads the notes.
   It also owns its own loading/empty placeholders on purpose: being rendered unconditionally by
   its parent is what keeps this instance (and _collapsedGroups with it) alive when a filter
   matches nothing. *@

@if (Groups is null)
{
    <MudText Color="Color.Secondary">Loading…</MudText>
}
else if (Groups.Count == 0)
{
    <MudText Color="Color.Secondary">@EmptyText</MudText>
}
else
{
    <MudExpansionPanels MultiExpansion="true" Elevation="0" Outlined="true" Gutters="false">
        @foreach (var group in Groups)
        {
            var label = group.Label;
            <MudExpansionPanel @key="label" Expanded="@(!_collapsedGroups.Contains(label))"
                               ExpandedChanged="@(expanded => SetGroupExpanded(label, expanded))">
                <TitleContent>
                    <MudText Typo="Typo.subtitle1" HtmlTag="h2">
                        @label <span class="jn-group-count">(@group.Notes.Count)</span>
                    </MudText>
                </TitleContent>
                <ChildContent>
                    <div class="jn-note-list">
                        @foreach (var note in group.Notes)
                        {
                            <NoteListItem Note="note" />
                        }
                    </div>
                </ChildContent>
            </MudExpansionPanel>
        }
    </MudExpansionPanels>
}

@code {
    /// <summary>The sections to render, already ordered. <c>null</c> means "still loading"; empty means "no notes matched".</summary>
    [Parameter] public IReadOnlyList<NoteGroup>? Groups { get; set; }

    /// <summary>Shown when <see cref="Groups"/> is empty — the caller phrases it (search vs. no notes at all).</summary>
    [Parameter] public string EmptyText { get; set; } = "No notes.";

    // Groups the user collapsed. Tracked here (not left to the panel) because the panel's Expanded
    // parameter would otherwise snap back to open on every re-render, e.g. when a filter changes.
    private readonly HashSet<string> _collapsedGroups = [];

    private void SetGroupExpanded(string label, bool expanded)
    {
        if (expanded)
        {
            _collapsedGroups.Remove(label);
        }
        else
        {
            _collapsedGroups.Add(label);
        }
    }
}
```

The `Loading…` and empty `MudText` keep `Color="Color.Secondary"`; `@key="label"`, `Elevation="0" Outlined="true"
Gutters="false" MultiExpansion="true"` and the `jn-group-count` / `jn-note-list` classes must not change. **Before writing,
compare against the current `NoteList.razor` markup and keep the current file's exact attributes/text if they differ from
the snippet above** — the behaviour must be identical to today's.

### Step 2 — Create `src/JNotes.Web/Components/Notes/NoteListView.razor`

Markup moved from `NoteList.razor` lines 8–31 (search header + filters) plus the `<NoteGroupList>` call. The
`AllTypes`/`AllTags` sentinels move here as `public const`; the page references them as `NoteListView.AllTypes` /
`NoteListView.AllTags` so there is exactly one definition.

```razor
@* The default notes screen: the search-results header, the Type/Tag filters, and the grouped list.
   Presentational on purpose — it injects nothing and never calls INoteService. The routed page
   (Pages/NoteList.razor) owns data loading, because AuthenticatedCurrentUserContext must be resolved
   from a routed page's first lifecycle method (see CLAUDE.md, "ICurrentUserContext gotcha"). *@

@if (!string.IsNullOrWhiteSpace(Query))
{
    <MudText Color="Color.Secondary" Class="mb-3">
        Search results for &ldquo;@Query&rdquo; &middot; <MudLink Href="/">Clear</MudLink>
    </MudText>
}

<div class="jn-filters">
    <MudSelect T="string" Label="Type" Variant="Variant.Outlined" Margin="Margin.Dense"
               Value="TypeFilter" ValueChanged="@(value => TypeFilterChanged.InvokeAsync(value))">
        <MudSelectItem T="string" Value="@AllTypes">All types</MudSelectItem>
        <MudSelectItem T="string" Value="@("Text")">Text</MudSelectItem>
        <MudSelectItem T="string" Value="@("Checklist")">Checklist</MudSelectItem>
    </MudSelect>

    <MudSelect T="string" Label="Tag" Variant="Variant.Outlined" Margin="Margin.Dense"
               Value="TagFilter" ValueChanged="@(value => TagFilterChanged.InvokeAsync(value))">
        <MudSelectItem T="string" Value="@AllTags">All tags</MudSelectItem>
        @foreach (var tag in AvailableTags)
        {
            <MudSelectItem T="string" Value="@tag">@tag</MudSelectItem>
        }
    </MudSelect>
</div>

@* Rendered unconditionally (it handles its own loading/empty states) so its collapsed-group set
   survives a filter that matches nothing. *@
<NoteGroupList Groups="Groups" EmptyText="@EmptyText" />

@code {
    // MudSelect can't show a label for an empty-string value, so "no filter" is an explicit sentinel.
    // AllTags is a comma because a tag can never contain one (tags are split on commas).
    // Public so the page can translate these back into a NoteSearchQuery without redefining them.
    public const string AllTypes = "all";
    public const string AllTags = ",";

    /// <summary>The active free-text search (the page's <c>?q=</c>), or null/blank for the plain list.</summary>
    [Parameter] public string? Query { get; set; }

    /// <summary>Sections to render; <c>null</c> while the first load is in flight.</summary>
    [Parameter] public IReadOnlyList<NoteGroup>? Groups { get; set; }

    [Parameter] public IReadOnlyList<string> AvailableTags { get; set; } = [];

    [Parameter] public string TypeFilter { get; set; } = AllTypes;
    [Parameter] public string TagFilter { get; set; } = AllTags;

    [Parameter] public EventCallback<string?> TypeFilterChanged { get; set; }
    [Parameter] public EventCallback<string?> TagFilterChanged { get; set; }

    // A property, not an inline conditional in the NoteGroupList attribute: keeps the quoted strings
    // out of a Razor attribute expression.
    private string EmptyText =>
        string.IsNullOrWhiteSpace(Query)
            ? "No notes yet — create one to get started."
            : "No notes match your search.";
}
```

As with Step 1, keep the exact wording, attributes and item values from the current `NoteList.razor` where they differ from
this snippet.

### Step 3 — Rewrite `src/JNotes.Web/Components/Pages/NoteList.razor`

Replace with the thin page below. Keep `@page`, `@attribute [Authorize]`, both `@inject`s, `<PageTitle>`, `Query`,
`OnInitializedAsync`, `OnParametersSetAsync` and `RefreshAsync`'s semantics unchanged.

```razor
@page "/"
@attribute [Authorize]
@inject INoteService NoteService
@inject IAppTimeZoneProvider TimeZoneProvider

@* Thin routed shell: owns the URL (?q=), data loading and the filter values; NoteListView draws
   the screen. INoteService is deliberately resolved and used only from this page's first lifecycle
   methods — see CLAUDE.md's ICurrentUserContext gotcha. *@

<PageTitle>Notes</PageTitle>

<NoteListView Query="@Query"
              Groups="_groups"
              AvailableTags="_availableTags"
              TypeFilter="_typeFilter"
              TagFilter="_tagFilter"
              TypeFilterChanged="OnTypeFilterChangedAsync"
              TagFilterChanged="OnTagFilterChangedAsync" />

@code {
    [SupplyParameterFromQuery(Name = "q")]
    public string? Query { get; set; }

    // null until the first load finishes — NoteGroupList shows "Loading…" for null and the empty
    // message for an empty list. Equivalent to the old "_notes is null / _notes.Count == 0" test:
    // GroupByRelativeDate drops empty buckets, so it returns an empty list exactly when the note
    // list was empty.
    private IReadOnlyList<NoteGroup>? _groups;
    private IReadOnlyList<string> _availableTags = [];

    private string _typeFilter = NoteListView.AllTypes;
    private string _tagFilter = NoteListView.AllTags;

    protected override async Task OnInitializedAsync()
    {
        _availableTags = await NoteService.GetDistinctTagsAsync();
    }

    protected override async Task OnParametersSetAsync()
    {
        // Fires on first load and again whenever Query changes (the sidebar search box navigates
        // to /?q=... without leaving this page) — the single place RefreshAsync needs to be
        // triggered from for anything driven by the URL, same reasoning NoteEditor.razor's Id
        // route parameter uses OnParametersSetAsync instead of OnInitializedAsync for.
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var type = Enum.TryParse<NoteType>(_typeFilter, out var parsed) ? parsed : (NoteType?)null;
        var query = new NoteSearchQuery(
            SearchText: string.IsNullOrWhiteSpace(Query) ? null : Query,
            Tag: _tagFilter == NoteListView.AllTags ? null : _tagFilter,
            Type: type);

        // ListAsync already orders by UpdatedAt descending (RedisNoteIndexRepository.SearchAsync) —
        // grouping never re-sorts, so that order carries straight through into each bucket.
        var notes = await NoteService.ListAsync(query);
        _groups = NoteGrouping.GroupByRelativeDate(notes, DateTimeOffset.UtcNow, TimeZoneProvider.TimeZone);
    }

    private async Task OnTypeFilterChangedAsync(string? value)
    {
        _typeFilter = value ?? NoteListView.AllTypes;
        await RefreshAsync();
    }

    private async Task OnTagFilterChangedAsync(string? value)
    {
        _tagFilter = value ?? NoteListView.AllTags;
        await RefreshAsync();
    }
}
```

### Step 4 — CSS: leave `wwwroot/css/note-list.css` untouched

Every class used by the moved markup (`jn-filters`, `jn-group-count`, `jn-note-list`) is global and keeps working from the
new component files.

- Do **not** create `NoteListView.razor.css` / `NoteGroupList.razor.css`: `.jn-note-list`, `.jn-note-main`, `.jn-note-title`,
  `.jn-tags` are shared with `Pages/Trash.razor` and `Notes/NoteListItem.razor`; scoping them would break the trash page.
- Do **not** run `git restore` / `git checkout` / `git stash` on any file: `note-list.css` and `note-view.css` have
  **uncommitted user modifications** that must survive. Run `git diff --stat src/JNotes.Web/wwwroot/css/note-list.css`
  before and after and confirm it is identical (ideally the file is never opened for writing).
- Do not touch `note-view.css`.

### Step 5 — `CLAUDE.md` update: **APPLIED afterwards at the user's request** (originally deferred, not part of the executor's run)

The planner suggested updating the "Sidebar → Search" bullet and the "The default notes view groups by relative date"
paragraph in `CLAUDE.md` to describe the new split. This was **not** requested by the user, so the executor must **not**
edit `CLAUDE.md`. Instead, the executor's handoff should list the two paragraphs as needing a follow-up, and the user
decides whether to apply it. Suggested content for that follow-up:

- Search bullet: `NoteList.razor` is a thin page holding the query, filter values and `INoteService` calls;
  `NoteListView` (`Components/Notes/`) renders the header, the filters and the list.
- Grouping paragraph: the page passes `DateTimeOffset.UtcNow` + `IAppTimeZoneProvider.TimeZone` to
  `NoteGrouping.GroupByRelativeDate` in `RefreshAsync`; `NoteListView` owns the `AllTypes`/`AllTags` sentinels
  (`public const`); `NoteGroupList` renders the panels **and** the loading/empty placeholders and owns `_collapsedGroups`,
  rendered unconditionally so the set survives a zero-result filter; the new components inject nothing.

## Tests to add or update

None. `tests/JNotes.Tests` covers Core only; `NoteGroupingTests` tests `NoteGrouping`, which is untouched; there is no
bUnit. Run the existing suite unchanged as a regression check.

## Verification

Commands (from repo root):

```bash
dotnet build JNotes.slnx
dotnet format JNotes.slnx --verify-no-changes
docker run --rm -d -p 6379:6379 valkey/valkey     # integration tests need a REAL Redis
dotnet test JNotes.slnx
git status --short                                 # note-list.css and note-view.css must still show "M"
git diff --stat src/JNotes.Web/wwwroot/css/note-list.css   # same diff as before the refactor
dotnet watch --project src/JNotes.Web run
```

Then in a browser (you need several notes with different `UpdatedAt` dates — at least one today, one yesterday, one older —
plus at least two different tags and at least one checklist note; bump a note by editing its title if needed):

1. **Grouping order and counts** — sections in order Today → Yesterday → This week → Last week → This month → Previous, each
   with a grey `(n)` count matching its rows, newest first within a section.
2. **Empty buckets skipped** — no section with `(0)`.
3. **Collapse/expand persists across a filter change** — collapse "Today" and one other section; change the Tag filter to a
   matching tag; sections stay collapsed; back to "All tags": still collapsed. Expand one, change the Type filter: stays expanded.
4. **Collapse persists across a zero-result filter (key regression)** — with a section collapsed, pick a Tag that matches no
   notes → empty message shown; switch back to "All tags" → the section must still be **collapsed**. If it springs open,
   `NoteGroupList` is being rendered inside an `@if` — fix that.
5. **Type / Tag filters** — Text / Checklist / All types; tag dropdown lists all distinct tags + "All tags".
6. **Search via `?q=`** — sidebar search + Enter → `/?q=<term>`; "Search results for “term” · Clear" shows above filters;
   a second, different term without leaving the page updates the list; **Clear** → `/`, header gone, full list.
7. **No hits / empty** — gibberish search → "No notes match your search."; no notes at all → "No notes yet — create one to get started."
8. **Loading state** — hard reload; "Loading…" appears briefly then sections, no extra layout jump.
9. **Open a note** — click a row → `/notes/{id}` renders; back → list rebuilt normally.
10. **Filters layout** — selects side by side, wrap on a narrow window.
11. **Both themes** — Settings → Appearance dark/light; panel outlines, `(n)` colour, row hover as before.
12. **Console clean** — no errors/warnings (no "unhandled error has occurred").
13. **Trash page unaffected** — `/trash` rows still styled.

## Risks and edge cases

- **Collapsed state lost on zero-result filter** — mitigated by `NoteGroupList` owning its placeholders and being rendered
  unconditionally. Do not "tidy" it into an `@if`.
- **`ICurrentUserContext` gotcha** — all `INoteService` use stays in the page's first lifecycle methods.
- **`?q=` refresh path** — `Query` stays on the routed page; `RefreshAsync` stays in `OnParametersSetAsync`.
- **Loading vs empty** — now `_groups is null` instead of `_notes is null`; safe only because `GroupByRelativeDate` drops empty
  buckets.
- **Sentinel duplication** — `AllTypes`/`AllTags` defined once on `NoteListView`.
- **MudSelect `ValueChanged` typing** — lambda forwarding into `EventCallback<string?>`; page handlers keep `?? AllTypes` / `?? AllTags`.
- **Scoped-CSS trap** — no `.razor.css` files; would silently unstyle `Trash.razor`.
- **User's uncommitted CSS edits** — never revert or reformat `note-list.css` / `note-view.css`.
- **`dotnet format`** — run after edits so `--verify-no-changes` passes.

## Out of scope

- `NoteGrouping` / `NoteGroup` in `JNotes.Core`; no new Core types.
- `NoteListItem.razor` (used as-is), `Trash.razor`, `NoteView.razor`, `NoteEditor.razor`, other `Components/Notes/*`,
  `Layout/NavMenu.razor`, `_Imports.razor`, `Program.cs`, DI, `BannedSymbols.txt`.
- Any CSS rule change, new `.css`, or scoped `.razor.css`.
- **`CLAUDE.md` edits** (Step 5 is deferred to the user).
- Tests: no new test project, no bUnit, nothing under `tests/`.
- `docs/plans/*.md` historical documents (other than this file).
- Any feature change: filters, sorting, pagination, search semantics, wording, group labels, row layout.
