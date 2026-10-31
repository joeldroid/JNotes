# Plan: Remove checklist notes, make New Note a plain button, and reset the EF Core baseline

> Status: draft from the `planner` agent. Open questions at the bottom are resolved in "Decisions" before implementation.

## Goal
JNotes will have one kind of note: free-form markdown text. All checklist UI, Core APIs, PDF rendering and the note-type concept itself are removed. The sidebar's "New Note" becomes a single button that opens a new text-note draft at `/notes/new`, with no dropdown. The EF Core migrations are squashed back into one fresh `InitialCreate` that has no `NoteType` column.

## Context found
- **Type model:** `src/JNotes.Core/Models/NoteType.cs` (`Text`, `Checklist`). It is used by:
  - `Note.Type` (`Models/Note.cs`)
  - `NoteSummary` (positional 3rd arg) and `TrashedNoteSummary` (3rd arg) in `src/JNotes.Core/Notes/`
  - `NoteSearchQuery.Type` (3rd positional param, `Notes/NoteSearchQuery.cs`)
  - `NoteIndexEntry.NoteType` (string column, `Indexing/NoteIndexEntry.cs`)
  - `PostgresNoteIndexRepository.SearchAsync` (type filter at lines 47-51, projection at lines 82 and 108) and `UpsertAsync` (lines 173 and 186)
  - `NoteIndexReconciler.cs:64`
  - `NoteAutoTagger.cs:213` (prompt) and `:241` (index entry)
  - `AutoTagPromptBuilder.Build(NoteType type, ...)` (`Tagging/AutoTagPrompt.cs:30-45`, writes a "Note type:" line)
  - `NoteMarkdownSerializer` (lines 39-41 throw on an unknown type; line 62 writes it)
  - `NoteFrontMatter.Type` (string)
  - `NoteService.CreateAsync(NoteType type, ...)`, the recipient type-change guard (`NoteService.cs:147-150`), `ListTrashAsync` (line 205) and `IndexAsync` (line 376)
- **Checklist code:**
  - Core: `Serialization/ChecklistMarkdownParser.cs`, `Models/ChecklistItem.cs`, `Models/ChecklistSection.cs`.
  - `INoteService.SetChecklistItemCheckedAsync`, `AddChecklistItemAsync` and `AddChecklistCategoryAsync` (`INoteService.cs:55-77`), plus `NoteService.MutateChecklistBodyAsync` and `RequireChecklist` (`NoteService.cs:83-130`).
  - PDF: `Export/NotePdfDocumentBuilder.cs` (type branch at lines 73-87, `RenderChecklist` at lines 120-155) and `Export/note-pdf.css` (`.checklist li` at line 171, lines 219-274: `.checklist *`, `.box`, `li.checked *`, `.empty`; `.empty` is only emitted by `RenderChecklist`).
- **Checklist and type UI in `src/JNotes.Web/Components/`:**
  - `Notes/ChecklistPanel.razor` and `Notes/ChecklistNoteEditor.razor`.
  - `Pages/NoteView.razor`: type chip at line 69; checklist branch at lines 83-94; `SetItemCheckedAsync`/`MutateChecklistAsync`/`_isMutating` at lines 138 and 172-207; comment at line 51; share text at line 110.
  - `Pages/NoteEditor.razor`: `@page "/notes/new/{TypeName}"`; `TypeName`; `IsNew => TypeName is not null`; enum parse at lines 168-178; checklist branch at lines 92-100; `CreateAsync(_note.Type, ...)` at line 330.
  - Type chips: `Pages/Trash.razor:28` and `Notes/NoteListItem.razor:7`. Type icon: `Layout/RecentNotes.razor:13`.
  - `Layout/NavMenu.razor:13-32` (the `MudMenu` dropdown).
  - Comments only: `Notes/NoteSharePanel.razor:35` (mentions `ChecklistPanel`'s `_listVersion`).
- **CSS/JS:**
  - `wwwroot/css/note-editor.css`: line 2 comment; `.note-editor-preview-column` (lines 111-115); `.jn-fill` (lines 118-122, used only by `ChecklistPanel`); `.jn-check-*` and `.checklist-readonly` (lines 124-162).
  - `wwwroot/css/nav-menu.css:45-85` (`.jn-nav-new` rules written for a button inside `.mud-menu`, plus the dropdown arrow rule).
  - `wwwroot/js/markdown-toolbar.js:37-67` (`insertLine`, used only by `ChecklistNoteEditor`).
- **Comments citing `ChecklistMarkdownParser` as the example of a pure helper:** `Notes/NoteGrouping.cs:7`, `Users/UserValidation.cs:8`, `Tagging/AutoTagPrompt.cs:9`. Also `Sharing/NoteSharePermission.cs:9`.
- **Migrations:** `src/JNotes.Core/Data/Migrations/` contains `20260930213805_InitialCreate.cs`, `20260930213805_InitialCreate.Designer.cs` and `JNotesDbContextModelSnapshot.cs`. All three are fully generated, with no hand-edited SQL; the collation, check constraint and filtered indexes come from the model. `JNotesDbContextFactory` needs no database for `migrations add`. The app calls `MigrateAsync()` at startup (`HostExtensions.cs:42`).
- **Blazor routing:** when one component has several `@page` templates, parameters used only by the other templates are set to `null` on navigation and the page instance is reused. Optional route parameters should therefore be nullable.
- **Markdig pipeline:** `Serialization/MarkdownRenderer.cs` has no `UseTaskLists()`, so `- [ ] x` renders as a bullet with the literal text `[ ] x`.
- **Auto-tagger canonical guard** (`NoteAutoTagger.cs:186-193`): a file is rewritten only if `Serialize(Deserialize(raw)) == raw`. Existing files contain `type: text` in the order id, title, type, tags, created, updated (YamlDotNet emits properties in declaration order). If that line changes, every existing untagged note becomes permanently `Skipped`.

### Decisions proposed by the planner (see Open questions)
1. **Remove the note-type concept from the domain, index and search** (enum, `Note.Type`, summaries, `NoteSearchQuery.Type`, the index column). **Keep writing a fixed `type: text` line in front matter, in the same position,** and **ignore whatever `type` says when reading**. Existing files stay byte-identical (auto-tagger guard), the documented format barely changes, and legacy `type: checklist` files open as text notes with the body untouched. No migration of files on disk is needed; their first JNotes save rewrites the line to `type: text`.
2. **Drop the `NoteType` index column.** It would always hold "Text", and the migration reset makes dropping it free.
3. **Route becomes `/notes/new`**, replacing `/notes/new/{TypeName}`. `NoteEditor.Id` becomes `Guid?` and `IsNew => Id is null`.

## Steps

### Phase 1: Web UI (still builds against the unchanged Core)
1. **`src/JNotes.Web/Components/Layout/NavMenu.razor`:** replace the `<MudMenu>...</MudMenu>` inside `div.jn-nav-new` with one button, keeping it inside the same `<AuthorizeView Context="auth">` and outside `MudNavMenu`:
   ```razor
   <MudButton Href="/notes/new" Variant="Variant.Filled" Color="Color.Primary" FullWidth="true"
              StartIcon="@Icons.Material.Filled.Add">New Note</MudButton>
   ```
   No `EndIcon`, no `aria-haspopup`. Rewrite the comment above it: it is a plain navigation link to the `/notes/new` draft, it sits outside `MudNavMenu` so it can be styled as a primary button, and it stays in the shared `AuthorizeView` because `NavMenu` also renders for anonymous `NotFound`/`Error`.
2. **`src/JNotes.Web/wwwroot/css/nav-menu.css`:**
   - Header comment (lines 3-4): "New Note dropdown button" becomes "New Note button".
   - `.jn-nav-new`: delete the `& .mud-menu { ... }` block and the `& .mud-button-icon-end` block.
   - Change `& .mud-menu .mud-button-root` to `& .mud-button-root`, keeping its declarations.
   - Keep the `& .mud-button-icon-start` rule.
   - Rewrite the comment (lines 45-52) to describe a plain full-width `MudButton` whose metrics mimic `.mud-nav-link`.
3. **`src/JNotes.Web/Components/Pages/NoteEditor.razor`:**
   - Line 2: `@page "/notes/new/{TypeName}"` becomes `@page "/notes/new"`.
   - Change `[Parameter] public Guid Id` to `Guid? Id`. Delete the `TypeName` parameter and its comment; replace with: "null on /notes/new - the router sets the unused route parameter to null, so a reused instance switches modes correctly."
   - `IsNew => Id is null`.
   - In `OnParametersSetAsync`, delete the `Enum.TryParse` block. Build the draft as `new Note { Id = Guid.Empty, Title = "", Type = NoteType.Text, ... }` for now (`Type` goes away in Phase 3). Update the comment: the instance is reused between `/notes/{id}/edit` and `/notes/new`.
   - Use `GetWithAccessAsync(Id!.Value)` and `NavigateTo($"/notes/{Id}", ...)`.
   - Lines 92-100: always render `<TextNoteEditor ...>`, deleting the checklist `@if`/`else`.
   - Line 330: keep `CreateAsync(NoteType.Text, ...)` until Phase 3.
4. **`src/JNotes.Web/Components/Pages/NoteView.razor`:**
   - Delete the type `MudChip` at line 69 and keep the rest of `.jn-meta`.
   - Replace lines 83-94 with just `<MarkdownPreview Markdown="@_note.Body" ImagesNoteId="@(_access.IsOwner ? null : _note.Id)" />`.
   - Delete `_isMutating`, `SetItemCheckedAsync` and `MutateChecklistAsync`. Keep `_actionError`, which Leave and Trash still use.
   - Line 51 comment: drop "so checklist changes made on this page are included" (becomes "the PDF reflects the file at click time.").
   - Line 110: "...they can also change its text."
5. **`Pages/Trash.razor:28` and `Notes/NoteListItem.razor:7`:** delete the "Checklist/Text" `MudChip`. The share chips and title stay.
6. **`Layout/RecentNotes.razor:13`:** `Icon="@Icons.Material.Outlined.Description"`.
7. **Delete** `src/JNotes.Web/Components/Notes/ChecklistPanel.razor` and `src/JNotes.Web/Components/Notes/ChecklistNoteEditor.razor`.
8. **`wwwroot/css/note-editor.css`:**
   - Delete the `&.note-editor-preview-column` block, `.jn-fill`, `.jn-check-heading`, `.jn-check-list`, `.jn-check-row`, `.jn-check-done`, `.jn-check-empty` and `.checklist-readonly`, plus their comments.
   - Line 2 comment: drop "and the checklist rows".
9. **`wwwroot/js/markdown-toolbar.js`:** delete `insertLine` and its comment (lines 37-67). Leave `fireInput` and everything else alone.
10. **`Notes/NoteSharePanel.razor:35` comment:** change "same gotcha as ChecklistPanel's _listVersion" to "same gotcha as Settings.razor's _securityVersion".
11. `dotnet build JNotes.slnx` must succeed.

### Phase 2: remove checklist Core APIs and PDF rendering (builds; tests pass)
12. **`src/JNotes.Core/Notes/INoteService.cs`:** delete the three checklist methods and their docs.
13. **`src/JNotes.Core/Notes/NoteService.cs`:** delete `SetChecklistItemCheckedAsync`, `AddChecklistItemAsync`, `AddChecklistCategoryAsync`, `MutateChecklistBodyAsync` and `RequireChecklist` (lines 83-130).
14. **Delete** `src/JNotes.Core/Serialization/ChecklistMarkdownParser.cs`, `src/JNotes.Core/Models/ChecklistItem.cs` and `src/JNotes.Core/Models/ChecklistSection.cs`.
15. **`src/JNotes.Core/Export/NotePdfDocumentBuilder.cs`:** always use the markdown branch (`bodyClass = "note-body markdown"`) and delete `RenderChecklist`. The meta line keeps `typeLabel` for now; Phase 3 removes it. Keep `using JNotes.Core.Serialization` (`MermaidCodeBlockRenderer`).
16. **`src/JNotes.Core/Export/note-pdf.css`:** remove `.checklist li` from the break-inside selector list (line 171; make sure the list still ends correctly at `pre.mermaid`), and delete everything from `/* Checklist (mirrors ChecklistPanel...` to the end of the file.
17. **Comments:** in `NoteGrouping.cs:7`, `UserValidation.cs:8` and `AutoTagPrompt.cs:9`, change the `ChecklistMarkdownParser` example to `UserInitials`. In `NoteSharePermission.cs:9`, "Change the body only (text, or checklist items and toggles)" becomes "Change the body only".
18. **Tests** (details under "Tests to add or update"): delete `tests/JNotes.Tests/Serialization/ChecklistMarkdownParserTests.cs`, then remove or adjust the checklist tests in `NoteServiceTests`, `NoteSharingTests`, `NotePdfDocumentBuilderTests` and `NoteExportServiceTests`. Run `dotnet build` and `dotnet test`.

### Phase 3: remove the note-type concept (builds; run tests only after Phase 4, because EF Core reports pending model changes until the snapshot is regenerated)
19. **Before touching the serializer,** add the byte-stability test from the Tests section (`Serialize_of_a_canonical_file_is_byte_identical`) and run it green against the current code. That proves the literal is right.
20. **Delete** `src/JNotes.Core/Models/NoteType.cs`.
21. **`Models/Note.cs`:** remove `Type`.
22. **`Serialization/NoteFrontMatter.cs`:** keep `public string Type { get; set; } = "text";` **in the same position** (between `Title` and `Tags`). New doc: "Always written as `text` and ignored on read: kept so existing files stay byte-identical (the auto-tagger's canonical-format guard) and the format stays stable; legacy `checklist` files open as text notes."
23. **`Serialization/NoteMarkdownSerializer.cs`:** delete the `Enum.TryParse` check (lines 39-42) and `Type = noteType`. In `Serialize`, set `Type = "text"`.
24. **`Notes/NoteSummary.cs`:** remove the `NoteType Type` positional parameter. **`Notes/TrashedNoteSummary.cs`:** remove `NoteType Type`. Remove `using JNotes.Core.Models` where it becomes unused.
25. **`Notes/NoteSearchQuery.cs`:**
    ```csharp
    public sealed record NoteSearchQuery(string? SearchText = null, string? Tag = null, NoteListScope Scope = NoteListScope.All, int? Limit = null);
    ```
    Remove the `Type` param doc and the unused `using`. Every existing call site uses named args or the first positional only (verified).
26. **`Notes/INoteService.cs` and `NoteService.cs`:**
    - The signature becomes `Task<Note> CreateAsync(string title, IEnumerable<string> tags, string body, CancellationToken cancellationToken = default)`.
    - In the recipient branch of `AutosaveAsync`, the lambda becomes just `current => current.Body = note.Body`.
    - Remove the "or an Edit recipient tried to change the note's type" text from the `AutosaveAsync` exception doc.
    - In the `ReplaceBodyAsync` doc, drop "Type" from "Title/Tags/Type/CreatedAt".
    - `ListTrashAsync`: `new TrashedNoteSummary(note.Id, note.Title, note.Tags, note.UpdatedAt, trashedAt)`.
    - `IndexAsync`: delete `NoteType = ...`.
27. **`Indexing/NoteIndexEntry.cs`:** delete the `NoteType` property and its doc.
28. **`Indexing/PostgresNoteIndexRepository.cs`:**
    - Delete the `query.Type` filter (lines 47-51).
    - Remove `e.NoteType` from the projection, and the `Enum.Parse` argument from `new NoteSummary(...)`. Update the "Projected to an anonymous type..." comment.
    - Delete `NoteType = entry.NoteType` and `existing.NoteType = entry.NoteType` in `UpsertAsync`.
29. **`Indexing/NoteIndexReconciler.cs:64` and `Tagging/NoteAutoTagger.cs:241`:** delete the `NoteType = ...` lines.
30. **`Tagging/AutoTagPrompt.cs`:** the signature becomes `Build(string? title, string? body, IReadOnlyCollection<string> currentTags, IReadOnlyCollection<string> vocabulary)`. Delete the `Note type:` line from `userMessage` and remove `using JNotes.Core.Models`. Keep the `SystemMessage` wording. **`NoteAutoTagger.cs:213`:** `AutoTagPromptBuilder.Build(note.Title, note.Body, [], vocabulary)`. Leave `AutoTagNormalizer`'s generic list (it still contains "checklist") alone.
31. **`Export/NotePdfDocumentBuilder.cs`:** delete `typeLabel`. The meta line becomes `<div class="note-meta">` + encoded `lastUpdatedText` + `</div>`.
32. **`Web/Components/Pages/NoteEditor.razor`:** drop `Type = NoteType.Text` from the draft, and use `NoteService.CreateAsync(title, _note.Tags, _note.Body)`.
33. **Tests:** mechanical updates (see Tests). Afterwards, `NoteType` must have no matches under `src/` or `tests/`.
34. `dotnet build JNotes.slnx` must succeed. Do **not** expect `dotnet test` to pass yet.

### Phase 4: reset the migrations
35. From the repo root:
    ```bash
    dotnet tool restore
    rm src/JNotes.Core/Data/Migrations/20260930213805_InitialCreate.cs \
       src/JNotes.Core/Data/Migrations/20260930213805_InitialCreate.Designer.cs \
       src/JNotes.Core/Data/Migrations/JNotesDbContextModelSnapshot.cs
    dotnet ef migrations add InitialCreate --project src/JNotes.Core --startup-project src/JNotes.Core -o Data/Migrations
    ```
    Do **not** use `dotnet ef migrations remove` (it consults a database) or `dotnet ef database update` against `jnotes`.
36. Verify the regenerated schema is the old one minus `NoteType`:
    ```bash
    diff <(git show HEAD:src/JNotes.Core/Data/Migrations/20260930213805_InitialCreate.cs) src/JNotes.Core/Data/Migrations/*_InitialCreate.cs
    git diff -- src/JNotes.Core/Data/Migrations/JNotesDbContextModelSnapshot.cs
    ```
    Expected: the only schema change is the removed `NoteType` column (and its snapshot block), possibly a `ProductVersion` annotation change. The collation `jnotes_case_insensitive`, `CK_NoteShares_NotSelf`, the filtered `Email`/`OidcSubject` indexes and every FK and index must still be present. Any other difference means model drift: stop and report it.
37. Run `dotnet format JNotes.slnx`, then `dotnet build JNotes.slnx` and `dotnet test JNotes.slnx`. The tests migrate fresh `jnotes_test_<guid>` databases, so a snapshot/model mismatch would surface there.
38. **USER-GATED: do not run without the user's explicit OK.** The user's dev database `jnotes` on `jnotes-debug-db` still has history row `20260930213805_InitialCreate`. The next `dotnet run` would try to apply the new baseline and fail with "relation already exists". Use the option the user picks. Both act only *inside* the running container; never stop, recreate or `down` it.
    - **Option A: drop and recreate the database.**
      ```bash
      docker exec jnotes-debug-db psql -U jnotes -d postgres -c 'DROP DATABASE IF EXISTS jnotes WITH (FORCE);'
      docker exec jnotes-debug-db psql -U jnotes -d postgres -c 'CREATE DATABASE jnotes OWNER jnotes;'
      ```
      On the next run, `MigrateAsync` builds the schema and `/setup` appears again. The new admin gets a new user id, so existing `src/JNotes.Web/App_Data/notes/<old-user-id>/` folders become invisible. To keep them, move their contents into the new `<new-user-id>/` folder and click Administration -> Index -> Rescan Now.
    - **Option B: patch in place, keeping accounts and shares.** Only valid if step 36 showed `NoteType` as the sole difference. `<NEW_ID>` is the new file's name without `.cs`.
      ```bash
      docker exec jnotes-debug-db psql -U jnotes -d jnotes -c 'ALTER TABLE "NoteIndexEntries" DROP COLUMN "NoteType";'
      docker exec jnotes-debug-db psql -U jnotes -d jnotes -c "UPDATE \"__EFMigrationsHistory\" SET \"MigrationId\" = '<NEW_ID>' WHERE \"MigrationId\" = '20260930213805_InitialCreate';"
      ```

### Phase 5: documentation
39. **`README.md`:**
    - Line 41: delete the "Checklist notes" bullet.
    - Line 40: "Text notes" becomes "Notes".
    - Line 42: drop "and checklists" from the PDF bullet.
    - Line 179: replace the test filter example with `NoteGroupingTests`.
    - Line 298: "Edit the body".
    - Note file format (lines 319-343): use a text-note example. Replace line 332 with: "`type` is always `text` (kept for compatibility; older files with `type: checklist` open as ordinary text notes)."
    - Delete the "JNotes keeps checked items..." paragraph (lines 342-343).
    - Lines 47-48: keep "type", or say "(title, tags, timestamps)", in line with step 22's wording.
40. **`CLAUDE.md`:**
    - Line 20: change the single-class test filter example to `NoteGroupingTests`.
    - Line 50: drop `ChecklistMarkdownParser` from the pure-helper list.
    - Line 133: cite `_securityVersion` only.
    - **Sidebar, New Note bullet (lines ~237-256):** rewrite as a single filled primary `MudButton Href="/notes/new"`, outside `MudNavMenu` in `div.jn-nav-new`, in the shared `AuthorizeView`, nothing created until Save. Remove the `MudMenu`, `ActivatorContent` and dropdown rationale, and the `.mud-menu` stretch note. Keep the "no active-route indication / no LocationChanged" point with `/notes/new`.
    - Line 266: "a type icon" becomes "a note icon".
    - Line 270: drop "type/" from "no type/tag options".
    - Line 344: `NoteSearchQuery.Tag` only (drop `/Type`).
    - **Notes UI (lines 355-366):** NoteView renders `MarkdownPreview` only, with no checklist toggling and no `_isMutating`. The editor's new-note route is `/notes/new` (`Id` is `Guid?`, `IsNew => Id is null`, router nulls the unused parameter). In the Shared-notes paragraph, remove "`ChecklistPanel ReadOnly`".
    - Lines 368-369: remove "checklist changes" and "checklist toggles/adds".
    - **Lines 420-432:** rewrite the "deliberately share one layout" paragraph as a `TextNoteEditor`-only description.
    - Line 507: "never checklists" becomes "never the list or the trash".
    - "Images: text notes only" becomes "Images".
    - **Delete the whole `## Checklists` section** (lines 550-608). Move the "two tabs on one note are last-writer-wins" limitation into the Autosave paragraph.
    - **Note sharing:** line 634 becomes "Edit: ... the body only (text and images)", with "type" removed from Owner-only. Lines 640-642: drop "Type" and the "only a Type change throws" clause.
    - Line ~668: `NoteSearchQuery`'s **3rd** parameter is `Scope` and its **4th** is `Limit`.
    - Line 674: "Text and tag filters".
    - Line 694: delete the "Checklists are static HTML..." sentence.
    - Add one sentence under "Storage and indexing": the front matter `type` is a fixed `text` marker, ignored on read; legacy `checklist` files are read as text; the index has no type column.
41. Leave `docs/plans/*.md` (historical) untouched.

### Phase 6: read-only task-list rendering (added by user decision; the planner did not detail this phase, so the executor must verify names against the code)
42. **`src/JNotes.Core/Serialization/MarkdownRenderer.cs`:** add Markdig's `UseTaskLists()` to the shared `CreateBaseBuilder()` (used by both the app preview and `PdfMarkdownRenderer`). It emits `<ul class="contains-task-list">`, `<li class="task-list-item">` and a **disabled** `<input disabled="disabled" type="checkbox" />` (Markdig puts no class on the checkbox; CSS targets `.task-list-item > input[type="checkbox"]`; plus `checked="checked"` when `[x]`). Confirm that `DisableHtml()` does not strip these generated elements (it only affects raw HTML in the source) and that no HTML sanitizer or allow-list in `MarkdownPreview`/`PdfMarkdownRenderer` removes `<input>`; if one does, allow exactly that element.
43. **Interactivity: none.** The checkboxes are display-only (`disabled`). Do not add toggling, JS or any `INoteService` method; users edit the markdown text.
44. **`src/JNotes.Web/wwwroot/css/markdown-preview.css`:** style `.contains-task-list` (no bullets, no left padding), `.task-list-item`, and `.task-list-item > input[type="checkbox"]` (margin, `accent-color` from `--mud-palette-primary`, keep the dark theme legible when disabled). Native nesting, matching the file's style.
45. **`src/JNotes.Core/Export/note-pdf.css`:** the same classes with literal colours (no `--mud-*` variables), light theme; add `.task-list-item` to the `break-inside: avoid` selector list. Nothing may make Chromium fetch anything: a checkbox `<input>` is local, so the `--chromium-allow-list` rule is unaffected.
46. **Docs:** README feature bullet says task lists (`- [ ]` / `- [x]`) render as read-only checkboxes in previews and PDF; CLAUDE.md gets one short paragraph (where `UseTaskLists()` is registered, display-only by design, checked items do not reorder).
47. `dotnet build` and `dotnet test` must be green after this phase; the final `dotnet format --verify-no-changes` runs after it.

## Tests to add or update
All in `tests/JNotes.Tests/`.
- **Delete:** `Serialization/ChecklistMarkdownParserTests.cs`.
- **`Integration/NoteServiceTests.cs`:**
  - Delete the 15 checklist tests (lines 54-210: `SetChecklistItemCheckedAsync_*`, `AddChecklistItemAsync_*`, `AddChecklistCategoryAsync_*`).
  - `ListTrashAsync_reflects_a_trashed_note_newest_first`: create `second` as a text note.
  - Rename `ListAsync_filters_by_type_and_tag` to `ListAsync_filters_by_tag`, using two text notes with tags `home` and `work`, and assert only the tag filter.
  - **Add:** `A_legacy_checklist_file_is_indexed_and_read_as_a_text_note`. Write a raw `{NotesFolder}/{userA}/{id}.md` with `type: checklist` and body `- [ ] milk\n- [x] eggs`, call `_fixture.Reconciler.RescanAsync(UserAId)`, then assert: `ListAsync(new NoteSearchQuery(SearchText: "milk"))` finds it; `GetAsync` returns the body verbatim; after `UpdateAsync`, the file contains `type: text` and not `checklist`.
- **`Integration/NoteSharingTests.cs`:**
  - `CreateSharedWithBAsync`: remove the `type` parameter.
  - `A_read_recipient_cannot_write...`: create with the default helper and delete the three checklist `Assert.ThrowsAsync` lines.
  - Delete `An_edit_recipients_checklist_toggle_updates_the_owners_index_row`, `An_edit_recipient_cannot_change_the_note_type` and `An_edit_recipient_can_toggle_and_add_checklist_items...`. Body-edit coverage remains in `An_edit_recipients_content_save_...` and `...metadata_save...`.
- **`Integration/NoteIndexRepositoryTests.cs`:** remove the `type` parameter from `NewEntry` and the `NoteType =` line, and delete `SearchAsync_filters_by_note_type`.
- **`Export/NotePdfDocumentBuilderTests.cs`:**
  - `MakeNote` loses its `type` parameter.
  - Replace the type-label theory with a fact asserting `<div class="note-meta">Last updated 1/2/2026 (UTC)</div>`.
  - Delete the six checklist tests (lines 108-161).
  - **Add (Phase 6):** `A_task_list_renders_read_only_checkboxes`. Body `"- [ ] milk\n- [x] eggs"`; assert the HTML contains `task-list-item`, a `disabled` checkbox for each line, `checked` only on `eggs`, does not contain `class="box"`, and `doc.HasMermaid` is false. The same assertions (disabled, correct checked state) for the app-side `MarkdownRenderer` in `MarkdownRendererTests`, plus one that raw `<input>` HTML typed in a note is still escaped by `DisableHtml()`.
- **`Integration/NoteExportServiceTests.cs`:** delete `A_checklist_note_renders_its_items`.
- **`Tagging/AutoTagPromptBuilderTests.cs`:** the helper loses `type`; delete `The_note_type_is_stated`; **add** `The_prompt_does_not_mention_a_note_type` (`Assert.DoesNotContain("Note type", ...)`).
- **`Serialization/NoteMarkdownSerializerTests.cs`:**
  - The round-trip test drops `Type`.
  - Replace `Deserialize_throws_NoteParseException_for_unknown_note_type` with `Deserialize_ignores_the_type_value` (a theory over `checklist`, `spreadsheet`, `text`, plus a missing `type:` key; all parse, and the body and title are preserved).
  - **Add:** `Serialize_always_writes_type_text`.
  - **Add (step 19, written first):** `Serialize_of_a_canonical_file_is_byte_identical`. Use a raw literal in the current format (id, title, `type: text`, tags, created, updated, then the body) and assert `Serialize(Deserialize(raw)) == raw`.
- **Mechanical, compile-driven:** remove `NoteType.Text,` from every `CreateAsync(` call (some are split across lines, e.g. `NoteExportServiceTests.cs:47-48`), remove `Type = NoteType.Text,` from `Note` initializers, and remove the 3rd positional arg from `NoteSummary` in `Notes/NoteGroupingTests.cs:9`. Affected files: `NoteServiceTests`, `NoteSharingTests`, `NoteExportServiceTests`, `TagSuggestionTests` (line 144 too), `NoteGroupingTests`, `FileSystemNoteFileStoreTests`, `UserManagementServiceTests`, `ImageCleanupServiceTests`, `NoteAutoTaggerTests`, `AllUsersIndexReconcilerTests`, `NoteIndexReconcilerTests`, and any others the compiler reports.

## Manual verification (for the tester)
Prerequisites: `jnotes-debug-db` (`localhost:5432`) and `jnotes-debug-pdf` (`localhost:3000`) are already running. Do not start, stop or recreate any debug container, and do not touch the `jnotes` database or `src/JNotes.Web/App_Data`.

1. **Static checks:**
   ```bash
   dotnet build JNotes.slnx                         # 0 errors
   dotnet test JNotes.slnx                          # all green; no ChecklistMarkdownParserTests class
   dotnet format JNotes.slnx --verify-no-changes    # exit 0
   ```
   Then confirm with Grep that `NoteType` and `Checklist` (case-sensitive) have no matches under `src/` or `tests/`, except `AutoTagNormalizer`'s generic word list ("checklist"), its test's `InlineData("Checklist")` and the `AutoTagPrompt` system message. Only one `*_InitialCreate.cs`, plus its Designer file and the snapshot, may exist in `src/JNotes.Core/Data/Migrations/`, and none may contain `NoteType`.
2. **Start a throwaway instance:**
   ```bash
   S=/private/tmp/claude-501/-Users-joel-Code-Github-JNotes/3c627dd4-edaa-4b00-a9ce-389ffb1746b7/scratchpad/manual
   mkdir -p "$S/notes" "$S/keys"
   ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5240 TZ=Australia/Melbourne \
   NOTES_PATH="$S/notes" DATA_PROTECTION_KEYS_PATH="$S/keys" \
   POSTGRES_HOST=localhost POSTGRES_PORT=5432 POSTGRES_DB=jnotes_manual_nochecklist POSTGRES_USER=jnotes POSTGRES_PASSWORD=jnotes \
   GOTENBERG_URL=http://localhost:3000 OLLAMA_URL= \
   dotnet run --project src/JNotes.Web --no-launch-profile
   ```
   Check the schema: `__EFMigrationsHistory` has one `..._InitialCreate` row, and `\d "NoteIndexEntries"` has no `NoteType` column.
3. **Sign in.** Open `http://localhost:5240/setup`. **The user creates the admin themselves** (convention: `admin` / `admin@example.com` / `Test1234`); the agent must not type credentials. Then, as admin, Administration -> Add user `bob` with a password, which the user also types.
4. **New Note button:** one filled "New Note" button with a + icon and **no dropdown arrow and no menu**. Clicking it lands on `/notes/new` showing a text editor. Desktop: the icon and text line up with "All Notes". Mobile width (< 960px): the drawer closes after clicking.
5. **Draft guard:** a body without a title -> Save shows "Give the note a title before saving."; clicking New Note while the draft has content shows a "Discard this new note?" prompt ("Keep editing" keeps the content). `/notes/new/text` and `/notes/new/checklist` show the not-found page.
6. **Create, view and edit:** title "Groceries", tag `shop`, a body with `- [ ] milk`, a mermaid fence and a pasted image. Save lands on `/notes/{id}`. **No "Text"/"Checklist" chip** appears in the view meta line, the All Notes list item or Recent (every icon is the same document icon). Edit -> autosave -> Close shows the changes. Open `/notes/{id}/edit`, then click New Note: an empty draft appears. Browser Back: the existing note's editor appears with its content, not a draft.
7. **Search:** `milk` finds the note, and so does searching its tag.
8. **Share:** as admin, Share -> bob -> Can edit. In a private window bob (the user signs in) sees it in Shared with Me; bob's body edit appears for the owner after reload. Switch bob to View only: no Edit button, and the dialog text reads "...they can also change its text."
9. **PDF:** Export PDF opens a light PDF with no "Text ·" before "Last updated". The image and diagram render, and `- [ ] milk` / `- [x] eggs` show as read-only checkboxes (unchecked / checked). In the on-screen preview and view page the same lines show disabled checkboxes that cannot be clicked, no bullets, and legible in dark theme.
10. **Trash:** delete the note. The Trash row has no type chip. Restore and Delete forever both work.
11. **Legacy checklist file:** write `$S/notes/<admin-user-id>/<new-guid>.md` with front matter `id: <same guid>`, `title: Old list`, `type: checklist`, `tags: []`, `created`/`updated` UTC timestamps, and body `- [ ] a\n- [x] b`. Administration -> Index -> Rescan Now (1 added, no errors). The note appears, opens as text and is editable. After an edit the file says `type: text` and the body is intact.
12. **Leftovers:** no checklist checkboxes or Item/Category buttons anywhere, and the browser console shows no errors.
13. **Cleanup:** stop the app, then
    ```bash
    docker exec jnotes-debug-db psql -U jnotes -d postgres -c 'DROP DATABASE IF EXISTS jnotes_manual_nochecklist WITH (FORCE);'
    rm -rf "$S"
    ```

## Risks and edge cases
- **Pending model changes between Phases 3 and 4:** EF Core reports them until the snapshot is regenerated, so integration tests can fail mid-way. Run the tests only after Phase 4.
- **Front matter stability:** `NoteFrontMatter.Type` must keep its declaration position and the value `text`; otherwise every existing file fails the auto-tagger's canonical guard and is permanently `Skipped`. The byte-identity test guards this.
- **Legacy checklist files:** with Phase 6 they render `[ ]`/`[x]` as read-only checkboxes (not literal text); a legacy untagged `type: checklist` file fails the canonical guard until a JNotes save rewrites it; checked-items-sink ordering no longer happens; files with an unknown or missing `type` that used to be corrupted are now indexed (intended behaviour change).
- **`NoteEditor.Id` as `Guid?`:** every use must handle null. Navigating from `/notes/{id}/edit` to `/notes/new` reuses the instance, and `OnParametersSetAsync` must rebuild the draft. Manual step 6 covers this.
- **Dev database:** if the user's `jnotes` database isn't handled (step 38), the app fails at startup with "relation already exists". Option A also orphans existing note folders because user ids are new.
- **`NoteSearchQuery` and `NoteSummary` positions shift.** All current Web call sites use named args (verified), but any positional use with three or more args would silently rebind. Check in review.
- **MudBlazor 9:** `MudButton` with `Href` renders an anchor that the Blazor router intercepts. Check `FullWidth`/`StartIcon` against `~/.nuget/packages/mudblazor/<ver>/lib/net10.0/MudBlazor.xml` if the build complains.

## Out of scope
- `docs/plans/*.md` (historical plans).
- Interactive checkboxes or any toggling of task-list items (rendering is read-only; see Phase 6).
- The Dockerfile, `docker-compose*.yml` and `launchSettings.json`.
- Any debug container: no `docker compose up/down/rm/restart` on `jnotes-debug-*`.
- The user's `jnotes` database without explicit consent (step 38).
- `src/JNotes.Web/App_Data`.
- `AutoTagNormalizer`'s generic-tag list and the auto-tag system-message wording.
- The sharing model, image cleanup, the reconciler algorithm and the `NoteIndexUserTags` logic, beyond the edits listed above.

## Open questions
1. **How far to remove the type concept?** Default: remove the enum and every `Type`/`NoteType` from domain, index and search, but keep writing a fixed `type: text` line in front matter and ignore it on read. Alternatives: (b) drop the `type:` key from files entirely (cleaner, but existing untagged notes fail the auto-tagger's canonical guard until re-saved); (c) keep `NoteType` with only `Text` (less churn, dead code and a constant index column).
2. **Existing `type: checklist` files on disk?** Default: no migration; read and indexed as text, rewritten to `type: text` on next save.
3. **Index column:** drop `NoteType` (default) or keep it?
4. **Route:** `/notes/new` (default) or keep `/notes/new/text`?
5. **Dev database `jnotes` on `jnotes-debug-db`:** needs explicit OK. Default Option A (drop and recreate); alternative Option B (in-place `DROP COLUMN` + history row rewrite).
6. **Render `- [ ]` as checkboxes in text-note previews and PDF** (Markdig `UseTaskLists()` + CSS)? Default: not in this change.

## Decisions
Answered by the user (Q2 and Q3 follow the planner's defaults, because the user accepted the type-concept recommendation):
1. **Type concept:** remove `NoteType`/`Note.Type`/summaries/`NoteSearchQuery.Type`; keep writing a fixed `type: text` line in the same front-matter position and ignore it on read. No `type:`-less format.
2. **Legacy `type: checklist` files:** no on-disk migration; read as text, rewritten to `type: text` on next save.
3. **Index column:** drop `NoteType` (folded into the baseline reset).
4. **Route:** `/notes/new`; `/notes/new/text` and `/notes/new/checklist` stop resolving.
5. **Dev database `jnotes` on `jnotes-debug-db`: Option A, drop and recreate, approved by the user** (Phase 4 step 38). Run it only after the migration regeneration and tests succeed, only the two `docker exec ... psql` statements, never touching the container itself. Report afterwards that the user must redo `/setup` and move old `App_Data/notes/<old-user-id>/` contents into the new user-id folder, then Rescan Now.
6. **Task lists: YES, add read-only checkbox rendering** in text-note previews and PDF export (Phase 6). This overrides the planner's "not in this change" default and the "Out of scope" line about it.
