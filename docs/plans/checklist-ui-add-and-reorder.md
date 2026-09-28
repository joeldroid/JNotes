# Plan: UI-driven checklist item add + checked-items-sink-to-bottom (JNotes)

**Status:** Ready for implementation. No code written yet.
**Scope:** `JNotes.Core` (parser + note service), `JNotes.Web` (note view + checklist editor), tests, docs.
**Author context:** planned against commit `19a55ee` (`main`, clean tree).

> **Note on `CLAUDE.md`:** §7 proposes edits to `CLAUDE.md`. Those are **held for the user's approval** — the executor must NOT edit `CLAUDE.md` unless the user explicitly confirms. `README.md` updates are in scope.

---

## 1. Context and current behaviour (verified against the code)

| Concern | Today |
| --- | --- |
| Parsing checklist lines | `ChecklistMarkdownParser.Parse(body)` → `IReadOnlyList<ChecklistItem>` (`LineIndex`, `Text`, `IsChecked`). Regex: `^(?<indent>\s*)-\s\[(?<mark>[ xX])\]\s(?<text>.*)$` — only `-` bullets, exactly one whitespace char after `-` and after `]`. |
| Mutating a checklist | `ChecklistMarkdownParser.ToggleItem(body, lineIndex)` — flips only that line's bracket char, everything else byte-identical, CRLF preserved via a `usesCrLf` sniff + re-replace. |
| Service surface | `INoteService.ToggleChecklistItemAsync(noteId, lineIndex)` → reads the file, guards `NoteType.Checklist`, calls `ToggleItem`, `AutosaveAsync(note, NoteSaveScope.ContentOnly)`. |
| Read-only view | `src/JNotes.Web/Components/Pages/NoteView.razor` — renders `ChecklistMarkdownParser.Parse(_note.Body)` as checkboxes, single `_isToggling` bool gate, no way to add an item. |
| Editor | `src/JNotes.Web/Components/Notes/ChecklistNoteEditor.razor` — checkbox column + optional raw-markdown `<textarea>` side panel; empty state literally says *"No checklist items yet — add some in the raw markdown."* Toggles bubble up as `ItemToggled` → `NoteEditor.razor.ToggleItemAsync` → `NoteService.ToggleChecklistItemAsync` (a **disk read-modify-write**, serialized through `_saveGate`). |
| New checklist note | `NavMenu.razor.CreateNoteAsync` seeds body `"- [ ] "` (a real item with empty text — it *does* match the regex and renders as an empty checkbox row). |

Call sites of the pieces being changed (complete, from grep):
`src/JNotes.Core/Notes/NoteService.cs:61`, `src/JNotes.Core/Notes/INoteService.cs:35`,
`src/JNotes.Web/Components/Pages/NoteView.razor:54,59,111`,
`src/JNotes.Web/Components/Pages/NoteEditor.razor:47,154,170`,
`src/JNotes.Web/Components/Notes/ChecklistNoteEditor.razor:19,46,51`,
`tests/JNotes.Tests/Serialization/ChecklistMarkdownParserTests.cs`,
`tests/JNotes.Tests/Integration/NoteServiceTests.cs:54-88`.

Goal: (1) add items from the UI with no markdown editing, (2) make that work on `/notes/{id}` (view mode) as well as the editor, (3) checked items sort after unchecked ones.

---

## 2. Design decisions

### D1 — Reordering is **physical, in the stored markdown**, not display-only

**Decision:** when an item's checked state changes, its line is physically moved within the file.

**Reasoning:**

* The `.md` file is the source of truth, and the whole point of that rule is that the file and the app agree. A display-only sort would create a second, invisible ordering rule that only JNotes knows: open the same note in Obsidian/vim/GitHub and the items are in a different order than the app just showed you. "Portable and readable" means the file *reads* the way the app renders it.
* It would also visibly desync the editor: `ChecklistNoteEditor`'s whole side-by-side design (per CLAUDE.md) exists so the checkbox pane and the raw-markdown pane show the same thing. A display-only sort makes the left pane and the right pane disagree about order.
* "Completed items collected at the bottom" is exactly how a human writes a to-do list by hand, so the resulting file stays idiomatic markdown — no marker, no front-matter field, no hidden state. `ChecklistItem` stays a pure projection of the text.
* It does **not** violate the "line-based regex, never a Markdig AST round-trip" principle (CLAUDE.md). Each line's bytes are still preserved verbatim; only their *order* changes. Nothing is re-emitted from a parsed tree. CLAUDE.md's wording ("every other line is returned byte-identical") does need updating — see §7.

**Accepted cost:** a toggle can now change more than one line of the file (bigger git diff), and a user who hand-ordered a partially-checked list will see it rearranged on their first toggle. Fallback if this proves unpopular is cheap and is recorded in §11 (R1).

### D2 — "The bottom" means **the bottom of the item's own checklist block**, not the bottom of the body

A **checklist block** = a maximal run of *consecutive* lines that are all task-list lines **with the same `indent` capture**. Any other line — prose, a heading, a blank line, or a task line at a different indent — ends the block.

**Reasoning:**

* Notes legitimately mix content: `## Groceries` / items / blank / `## Hardware` / items. Checking "milk" must not teleport it below the Hardware heading.
* The same-indent rule handles nested lists for free with no special case: for
  `- [ ] a` / `  - [ ] a1` / `  - [ ] a2` / `- [ ] b`, the blocks are `{a}`, `{a1,a2}`, `{b}`. Checking `a1` reorders it among its siblings; checking `a` (a single-item block) moves nothing, so a parent can never be dragged away from its children. Conservative and correct.
* Blank lines splitting a block is a deliberate simplification (a "loose" markdown list is technically still one list). It is the predictable reading of "adjacent items" and keeps the rule stateless.

### D3 — Ordering rule: **stable partition, toggled item last in its destination group**

Within the affected block, after the mark is set:

1. Remove the target line from the block.
2. Stably partition the remaining lines: all unchecked (original relative order), then all checked (original relative order).
3. Re-insert the target: **checked → at the very end of the block**; **unchecked → at the end of the unchecked run** (i.e. immediately above the first checked line, or at the end if there are none).

**Answers to the questions asked:**

* *Where does an un-checked item go?* To the bottom of the unchecked section — the natural inverse of "check sends it to the bottom". It deliberately does **not** return to its pre-check position: remembering that would require state that lives outside the file, which D1 rules out.
* *How is relative order preserved?* Every non-toggled line keeps its relative order inside its own group (stable partition). Only the toggled line jumps.
* *Why partition the whole block instead of moving just the one line?* Moving one line is enough to *preserve* the invariant on a file the app itself created, but it cannot *restore* it on a hand-edited or imported file. Making the partition a post-condition of the operation means "after any toggle, that block is unchecked-then-checked" is always true, it is a single crisp thing to test, and it self-heals externally-edited files — the same self-healing philosophy `NoteIndexReconciler` already embodies. Diff cost is bounded to one block.
* *Already at the bottom / nothing to move?* The algorithm is a no-op on ordering (same lines, same order); the only byte that changes is the bracket. Covered by a test.

### D4 — Service API changes from **toggle** to **set**

Replace `ToggleChecklistItemAsync(noteId, lineIndex)` with
`SetChecklistItemCheckedAsync(noteId, lineIndex, isChecked)`.

**Reasoning:** reordering makes `LineIndex` volatile, so a stale or duplicated event is now *semantically* dangerous, not merely redundant. A checkbox `@onchange` already carries the desired state from the browser, so "set to `true`" costs nothing extra and is idempotent: replaying it can never double-flip. This is strictly safer than a relative flip and reads better with the new behaviour ("set checked → sinks to the bottom"). Only two call sites and two test classes are affected; `ToggleItem`/`ToggleChecklistItemAsync` have no other consumers, so they are removed rather than kept as dead wrappers.

### D5 — Save scope: **`NoteSaveScope.ContentOnly`** for both set-checked and add-item

Both operations change only `Body`; title and tags are untouched. This is exactly the existing toggle behaviour and keeps the documented trade-off consistent: the note does not jump to the top of the `UpdatedAt`-sorted list, and the new item's text is not searchable until a `Metadata`-scope save or the next reconciliation pass (startup, Settings → Rescan Now, or the auto-index background service). Using `Metadata` for add-item would make new items searchable instantly but would introduce an inconsistency with toggles for no structural reason. Default: `ContentOnly`. (See §11/R4 for the alternative.)

### D6 — In the **editor**, checklist mutations happen **in memory** and ride the existing autosave; the service methods are for the **view** page

`NoteEditor.ToggleItemAsync` today calls a service method that **re-reads the file**, mutates disk truth, and then assigns `result.Body` back over `_note.Body`. If the user has body edits still sitting in the 800 ms content debounce, those edits are silently discarded. That is a pre-existing latent bug, and adding an "add item" button to the editor would make it far easier to hit.

**Decision:** `ChecklistNoteEditor` mutates its own `Body` via the pure parser and raises `BodyChanged`, which `@bind-Body:after="OnContentEdited"` already routes into the existing debounce + `_saveGate`. `NoteEditor.razor` loses `ToggleItemAsync` and the `ItemToggled` parameter entirely.

**Reasoning:** no disk race, no clobbering, the raw-markdown panel stays in sync for free (it binds to the same `Body`), and it removes code rather than adding it. Calling the pure static `ChecklistMarkdownParser` from Web is already precedent (`NoteView.razor:54`, `ChecklistNoteEditor.razor:51`) and is not a banned symbol — the analyzer bans `File`/`Directory`/`DbContext`/`SqliteConnection`/Redis types, not Core's pure helpers.

**Behaviour change to accept:** an editor toggle now persists on the content debounce (≤800 ms, plus the `DisposeAsync` flush) instead of immediately. The editor already trusts autosave for everything else.

**Interaction when both surfaces are used:** the view page's add/set methods are disk read-modify-write; the editor's autosave writes the whole body from memory. Across two tabs/circuits, last writer wins. That is the pre-existing model (there is no cross-circuit file lock today) and is explicitly out of scope; it is not made worse by this change, because within a single editor circuit everything funnels through the one `_saveGate`.

### D7 — View-page gating: one `_isMutating` flag covering both toggle and add

`NoteView` has no autosave to coordinate with, so the existing `_isToggling` pattern stands in for the editor's semaphore. Rename it `_isMutating` and have it disable the checkboxes, the add input, and the Add button. Blazor Server dispatches circuit events one at a time up to the first `await`, so setting the flag synchronously at the top of each handler is sufficient to drop a racing second click.

### D8 — Blazor DOM-diff hazard: `@key` bumped by a `_listVersion` counter (**do not skip this**)

With reordering, the classic Blazor checkbox-diff bug becomes likely: the user clicks the checkbox at position 0 (the browser sets `checked` in the DOM), the server moves that item to the bottom, and the *new* item now occupying position 0 is also unchecked — so the render diff sees no change for that attribute, does not patch it, and the DOM checkbox stays visually ticked while the model says otherwise.

**Fix:** keep an `int _listVersion` in both `NoteView.razor` and `ChecklistNoteEditor.razor`, increment it after every successful mutation, and key each rendered row with it:
`@key="(_listVersion, item.LineIndex)"` on the `<li>`, and use the same two values in the `id`/`for` pair (e.g. `id="@($"item-{_listVersion}-{item.LineIndex}")"`). Changing the key forces Blazor to rebuild those elements from the model. Cost is negligible for a checklist; correctness is not optional here.

### D9 — Add-item text handling

* **Trim** each line; **reject** input that is empty/whitespace-only after trimming (the parser throws `ArgumentException`; the UI also disables the button and returns silently, so the user never sees an exception for the normal case).
* **Newlines:** normalize `\r\n` → `\n`, split, trim, drop blanks, and create **one item per surviving line**, in order. A single-line typed entry is the degenerate case; a multi-line paste becomes a multi-item add, which is the useful behaviour.
* **Escaping: none is needed, deliberately.** The regex's `(?<text>.*)$` is greedy to end-of-line, so any single-line text round-trips. `- [ ] - [ ] x` parses back as text `- [ ] x`; `- [ ] ---` is harmless because `FrontMatterSplitter.Split` only inspects the *start* of the file. The only genuine hazards are newlines (split, above) and leading/trailing whitespace (trimmed). Document this explicitly rather than adding a pointless escaping layer.
* **Length:** no cap. The body has no cap either, and markdown handles long lines.
* **Indent:** a new item inherits the indent string of the block it is appended to, so it stays visually part of that list.
* **Bullet style:** always `- [ ] `. The parser only recognises `-` bullets today (pre-existing limitation; `* [ ]` is invisible to it). Not changed here.

### D10 — Where a new item goes

Append to the **last checklist block in the body**, at the **end of that block's unchecked run** (so a new item lands above already-checked items, preserving the D3 invariant by induction). If the body contains **no** task-list lines at all (a brand-new or prose-only checklist note), append a new line at the end of the body, **before any trailing run of blank lines** — this preserves an existing trailing newline instead of leaving a stray blank line in the middle.

There is one add box per surface (bottom of the list), so "the last block" is the only coherent target; there is no UI affordance for choosing a section.

### D11 — New checklist notes start with an empty body

`NavMenu.CreateNoteAsync` currently seeds `"- [ ] "`, which renders as a stray empty checkbox row. With a real add-item input on both surfaces, seed `""` instead and let the empty state + add box do the work.

---

## 3. Files to change

No new files. Deliberately: the parser is the right home for the line manipulation, and everything else is an edit to an existing call site.

| # | File | Change |
| --- | --- | --- |
| 1 | `src/JNotes.Core/Serialization/ChecklistMarkdownParser.cs` | Replace `ToggleItem` with `SetItemChecked`; add `AppendItems`; add private block/join helpers. |
| 2 | `src/JNotes.Core/Notes/INoteService.cs` | Replace `ToggleChecklistItemAsync` with `SetChecklistItemCheckedAsync`; add `AddChecklistItemAsync`. |
| 3 | `src/JNotes.Core/Notes/NoteService.cs` | Implement both. |
| 4 | `src/JNotes.Web/Components/Pages/NoteView.razor` | Set-checked call, add-item input, `_isMutating`, `_listVersion`/`@key`, empty state. |
| 5 | `src/JNotes.Web/Components/Notes/ChecklistNoteEditor.razor` | In-memory toggle + add-item input, `_listVersion`/`@key`, drop `ItemToggled`, new empty-state copy. |
| 6 | `src/JNotes.Web/Components/Pages/NoteEditor.razor` | Delete `ToggleItemAsync` and the `ItemToggled=` attribute. |
| 7 | `src/JNotes.Web/Components/Layout/NavMenu.razor` | Checklist default body `""`. |
| 8 | `tests/JNotes.Tests/Serialization/ChecklistMarkdownParserTests.cs` | Rewrite toggle tests, add reorder + append tests. |
| 9 | `tests/JNotes.Tests/Integration/NoteServiceTests.cs` | Rename toggle tests, add set/add tests. |
| 10 | `CLAUDE.md` | **HELD — only with explicit user approval.** Update three paragraphs, add one (§7). |
| 11 | `README.md` | Features + note-format note. |

**No CSS changes.** Bootstrap's `input-group` / `list-group` / `form-check` cover the new UI; adding a module to `wwwroot/css/` would be unjustified. (`note-editor.css` and `variables.css` were reviewed — nothing there needs touching.)

---

## 4. Exact API surface

### 4.1 `ChecklistMarkdownParser` (public static partial class, `JNotes.Core.Serialization`)

```csharp
// unchanged
public static IReadOnlyList<ChecklistItem> Parse(string body);

/// <summary>
/// Sets the task-list line at <paramref name="lineIndex"/> to <paramref name="isChecked"/> and
/// moves it within its checklist block so checked items sort after unchecked ones. Every line's
/// own text is preserved byte-for-byte; only the target's bracket character and line order change.
/// </summary>
/// <exception cref="ArgumentOutOfRangeException">lineIndex is outside the body.</exception>
/// <exception cref="InvalidOperationException">The targeted line is not a checklist item.</exception>
public static string SetItemChecked(string body, int lineIndex, bool isChecked);

/// <summary>
/// Appends one unchecked task-list line per non-blank line of <paramref name="text"/> to the last
/// checklist block in <paramref name="body"/> (above any checked items in it), or to the end of
/// the body if it has no checklist lines.
/// </summary>
/// <exception cref="ArgumentException">text is null, empty, or only whitespace/blank lines.</exception>
public static string AppendItems(string body, string text);
```

Removed: `ToggleItem`.

Private helpers to add:

```csharp
private static (int Start, int EndExclusive) BlockBounds(string[] lines, int lineIndex, string indent);
private static bool IsTaskLineWithIndent(string line, string indent);
private static bool IsLineChecked(string line);           // regex match + IsCheckedMark
private static (int InsertAt, string Indent) AppendPoint(List<string> lines);
private static string Join(IEnumerable<string> lines, bool usesCrLf);
// existing: IsCheckedMark, SplitLines, TaskListLineRegex
```

**`SetItemChecked` algorithm:**

```
usesCrLf = body.Contains("\r\n", Ordinal)
lines    = SplitLines(body)
guard lineIndex in range                       -> ArgumentOutOfRangeException
match    = TaskListLineRegex().Match(lines[lineIndex]); !Success -> InvalidOperationException

// 1. set the mark in place (byte-identical apart from one character)
mark = isChecked ? "x" : " "
lines[lineIndex] = line[..markGroup.Index] + mark + line[(markGroup.Index + 1)..]

// 2. block = maximal run of consecutive task lines with the same indent capture
indent          = match.Groups["indent"].Value
(start, endEx)  = BlockBounds(lines, lineIndex, indent)

// 3. stable partition, target last in its destination group
target   = lines[lineIndex]
rest     = lines[start..endEx] minus the target slot          // order preserved
unchecked = rest.Where(l => !IsLineChecked(l))                // stable
checked_  = rest.Where(IsLineChecked)                         // stable
reordered = isChecked ? [..unchecked, ..checked_, target]
                      : [..unchecked, target, ..checked_]
copy reordered back over lines[start..endEx]

return Join(lines, usesCrLf)
```

`BlockBounds`: walk left while `IsTaskLineWithIndent(lines[start-1], indent)`, walk right while `IsTaskLineWithIndent(lines[endEx], indent)`.

**`AppendItems` algorithm:**

```
texts = text.Replace("\r\n","\n").Split('\n').Select(Trim).Where(non-empty)
if texts is empty -> ArgumentException(nameof(text))
usesCrLf = body.Contains("\r\n", Ordinal)
lines = SplitLines(body).ToList()
foreach t in texts:
    (insertAt, indent) = AppendPoint(lines)
    lines.Insert(insertAt, $"{indent}- [ ] {t}")
return Join(lines, usesCrLf)
```

`AppendPoint`:

```
L = last index whose line is a task line
if none:
    idx = lines.Count; while idx > 0 && lines[idx-1].Length == 0: idx--    // before trailing blanks
    return (idx, "")
indent         = indent capture of lines[L]
(start, endEx) = BlockBounds(lines, L, indent)
idx = start; while idx < endEx && !IsLineChecked(lines[idx]): idx++        // end of unchecked run
return (idx, indent)
```

Worked examples (assert these in tests):
`""` → `"- [ ] a\n"`; `"- [ ] a"` → `"- [ ] a\n- [ ] b"`; `"- [ ] a\n- [x] b\n"` → `"- [ ] a\n- [ ] c\n- [x] b\n"`; `"some prose"` → `"some prose\n- [ ] a"`.

Mixed line endings in one body are normalised to the detected style — pre-existing `ToggleItem` behaviour, carried forward unchanged.

### 4.2 `INoteService` / `NoteService` (`JNotes.Core.Notes`)

```csharp
/// <summary>
/// Sets one checklist item's checked state and re-sorts its block so checked items sit after
/// unchecked ones. Idempotent: setting an item to the state it is already in only re-sorts.
/// </summary>
/// <exception cref="InvalidOperationException">The note is not a Checklist note, or the line isn't a checklist item.</exception>
Task<Note> SetChecklistItemCheckedAsync(Guid noteId, int lineIndex, bool isChecked, CancellationToken cancellationToken = default);

/// <summary>
/// Appends one unchecked item per non-blank line of <paramref name="text"/> to the note's last
/// checklist block, above any checked items in it.
/// </summary>
/// <exception cref="InvalidOperationException">The note is not a Checklist note.</exception>
/// <exception cref="ArgumentException">text has no non-blank content.</exception>
Task<Note> AddChecklistItemAsync(Guid noteId, string text, CancellationToken cancellationToken = default);
```

Both implementations mirror the existing `ToggleChecklistItemAsync` shape exactly: `_fileStore.ReadAsync(_currentUser.UserId, noteId, ct)` → `if (note.Type != NoteType.Checklist) throw new InvalidOperationException($"Note '{noteId}' is not a checklist note.")` → mutate `note.Body` through the parser → `return await AutosaveAsync(note, NoteSaveScope.ContentOnly, ct);`. Keep the existing explanatory comment about why `ContentOnly` is correct here.

Removed: `ToggleChecklistItemAsync` from both the interface and the implementation.

---

## 5. UI behaviour

### 5.1 `NoteView.razor` (`/notes/{id}`)

* Checkbox handler becomes state-carrying:
  `@onchange="e => SetItemCheckedAsync(item.LineIndex, e.Value is bool b && b)"`.
* Row markup gains the D8 key and id: `<li class="list-group-item" @key="(_listVersion, item.LineIndex)">`, `id="@($"item-{_listVersion}-{item.LineIndex}")"` with a matching `for`.
* New add-item row, rendered **below the `<ul>`, only for `NoteType.Checklist`**, and rendered even when the list is empty:

```razor
<div class="input-group mt-3">
    <input class="form-control" placeholder="Add an item" aria-label="Add a checklist item"
           value="@_newItemText" @oninput="OnNewItemInput" @onkeydown="OnNewItemKeyDownAsync"
           disabled="@_isMutating" @ref="_newItemInput" />
    <button class="btn btn-outline-primary" type="button"
            disabled="@(_isMutating || string.IsNullOrWhiteSpace(_newItemText))"
            @onclick="AddItemAsync">Add</button>
</div>
```

  Use the one-way `value=` + manual `@oninput` pattern (same as `NavMenu`'s search box) rather than `@bind`, so the field can be cleared server-side after a successful add.
* **Enter to add:** `@onkeydown` with `KeyboardEventArgs` (`e.Key == "Enter"`), matching `NavMenu.OnSearchKeyDown`. `keydown` for Enter arrives after every prior character's `oninput`, so `_newItemText` is current.
* **Empty state:** when `Parse` yields no items, render `<li class="list-group-item text-body-secondary">No items yet — add one below.</li>`.
* **Empty/whitespace input:** `AddItemAsync` returns silently if `string.IsNullOrWhiteSpace(_newItemText)`; the button is disabled in that state too. No error banner for the normal case.
* **After a successful add:** `_newItemText = ""`, `_listVersion++`, `await _newItemInput.FocusAsync()` so several items can be typed in a row.
* **Gating:** `_isToggling` → `_isMutating`, set/cleared in `try/finally` in both `SetItemCheckedAsync` and `AddItemAsync`; both re-entry-guard on it at the top. Failures set `_actionError` exactly as the current toggle does.
* Both handlers assign only `result.Body` and `result.UpdatedAt` back onto `_note` and bump `_listVersion`.

### 5.2 `ChecklistNoteEditor.razor`

* Checkbox handler mutates locally:

```csharp
private Task OnItemCheckedAsync(int lineIndex, ChangeEventArgs e)
{
    _listVersion++;
    return BodyChanged.InvokeAsync(
        ChecklistMarkdownParser.SetItemChecked(Body, lineIndex, e.Value is bool b && b));
}
```

  `@bind-Body:after="OnContentEdited"` in `NoteEditor.razor` then marks the note dirty and triggers the content debounce — no extra wiring.
* Same add-item `input-group` as the view (placed under the checkbox column, inside the left `col-*` so it keeps the side-by-side layout), calling `ChecklistMarkdownParser.AppendItems(Body, text)` and raising `BodyChanged`. Reject whitespace-only input the same way; clear and re-focus after a successful add.
* **Raw-markdown panel stays in sync automatically** — it renders `value="@Body"`, which is the very value being replaced, so an add or a reorder shows up there on the next render. Typing in the raw panel still flows through `OnInput`/`BodyChanged` unchanged, and the checkbox column re-derives from it. `MarkdownToolbar` is untouched.
* Same `_listVersion` + `@key` treatment (D8).
* Empty-state copy: *"No items yet — add one below."* (the "add some in the raw markdown" hint is now wrong).
* Remove `[Parameter] public EventCallback<int> ItemToggled`.

### 5.3 `NoteEditor.razor`

* Delete `ToggleItemAsync` (lines 154-180) and the `ItemToggled="ToggleItemAsync"` attribute on `<ChecklistNoteEditor>`. Nothing else changes — `_saveGate`, the debouncers, `TrashAsync` and `DisposeAsync` are untouched and still cover the new in-memory mutations, because those now flow through the ordinary content-autosave path.

### 5.4 `NavMenu.razor`

`var defaultBody = type == NoteType.Checklist ? "- [ ] " : "";` → `var defaultBody = "";` (with a short comment: items are added from the UI now, so a placeholder empty item would just render as a stray blank checkbox).

---

## 6. Edge cases and how each is handled

| Case | Handling |
| --- | --- |
| Non-checklist lines mixed in (prose, headings, blank lines) | Never move. They terminate blocks (D2), so reordering is confined to one contiguous run of items. |
| Multiple checklist blocks in one note | Each reorders independently; add-item targets the **last** block (D10). |
| Nested / differently-indented items | Different indent ⇒ different block, so a parent never moves past its children and children reorder only among themselves (D2). |
| Checked items already at the bottom | The partition is a no-op; the only byte that changes is the bracket. Explicit test. |
| Single-item block | Nothing to reorder; bracket only. |
| Empty checklist / empty body | `Parse` returns empty, view shows the empty state, `AppendItems` takes the no-block path and produces a first item (`""` → `"- [ ] a\n"`). |
| Prose-only body on a Checklist note | Same no-block path: item appended at the end of the body, before trailing blanks. |
| CRLF line endings | `usesCrLf` sniff + normalize + re-apply on join, in **both** `SetItemChecked` and `AppendItems` (same helper as today). Mixed endings normalize to the detected style — pre-existing behaviour. |
| Trailing newline in the body | Preserved: trailing blank lines are never task lines, so they sit outside every block, and `AppendPoint` inserts before any trailing blank run. |
| Whitespace-only add text | `ArgumentException` from `AppendItems`; the UI short-circuits before calling, so the user just sees nothing happen. |
| Multi-line paste | One item per non-blank line, in order (D9). |
| Very long item text | No cap; stored as one line. Test asserts the line is not split or wrapped. |
| Text containing `- [ ]`, `---`, `#`, etc. | Stored verbatim after the marker; round-trips through `Parse`. No escaping needed (D9) — front-matter detection only looks at the file's start. |
| Uppercase `[X]` marks on other lines | Untouched — only the target line's bracket is rewritten; other lines move as whole strings. Explicit byte-identity test. |
| **Indices shifting after a reorder** | `LineIndex` is re-derived by `Parse` from the body returned by the mutation, so every render carries fresh indices. The gap that matters is a click made against a *stale* render: `_isMutating` (view) drops any event that arrives while a mutation is in flight, and the D4 "set, don't toggle" API makes a duplicate event idempotent instead of a double-flip. The residual window (a click on a *different* item in the few ms between a mutation completing and the re-render landing) is the same window the existing `_isToggling` design already accepts; it is bounded by one wrong item's state, self-corrects on the next render, and is recorded as R5 in §11. |
| Rapid repeated clicks on the same item | Dropped by `_isMutating`; if one does slip through, "set to `true`" applied twice is a no-op. |
| Note not a checklist | `InvalidOperationException` from both service methods (existing precedent). |
| Note trashed/deleted mid-interaction | `NoteNotFoundException` surfaces via the existing `catch (Exception ex)` → `_actionError`. Unchanged. |
| Blazor checkbox DOM staleness after a reorder | `@key` bumped by `_listVersion` (D8). |

---

## 7. Documentation updates

### `CLAUDE.md` — **HELD: do not apply without explicit user approval**

Proposed content, for the user to approve or decline:

1. **"Checklist toggling is line-based regex, not a Markdig AST round-trip"** (~line 381) — keep the Markdig prohibition verbatim; amend the "only that line's bracket character changes / rest of the file byte-identical" claim: a checked item's **line is also moved** within its block. Each line's own bytes are still preserved; only order changes; still no AST round-trip. Mention the rename to `SetItemChecked`/`AppendItems` and why "set" beat "toggle" (D4).
2. **New paragraph — "Checked checklist items sink to the bottom of the file, not just the screen."** Cover: physical vs display-only and why (D1); the block definition, including that blank lines and differing indents split blocks (D2); stable partition + toggled-item-last, and that un-checking returns an item to the bottom of the unchecked run rather than its old position, because remembering that would need state outside the file (D3); that the partition repairs hand-edited blocks, mirroring the reconciler's self-healing philosophy; and the `@key`/`_listVersion` requirement with the Blazor diff reason (D8) — that one is exactly the kind of non-obvious gotcha this file exists to record.
3. **"Opening a note lands on a read-only view"** (~line 291) — the view can now *add* items too, so "the one thing the view can change is toggling an item" is stale. Name `SetChecklistItemCheckedAsync`/`AddChecklistItemAsync`, note `_isToggling` → `_isMutating` covering both, and that body *editing* still lives only in the editor.
4. **`ChecklistNoteEditor`/`TextNoteEditor` paragraph** (~line 368) — record D6: the editor's checklist mutations are pure in-memory parser calls raising `BodyChanged`, no longer a disk read-modify-write through `INoteService`; this removed `ItemToggled` and fixed the clobbering of debounced body edits. Note the raw-markdown panel stays in sync for free because both panes render the same `Body`. Also record R10 (cross-tab writes are last-writer-wins) as a known limitation.

### `README.md` (in scope)

* Features (~line 14): checklist items can be added from the note view or the editor without touching markdown, and checking one moves it below the unchecked ones.
* Note file format (~line 186): one sentence that JNotes keeps checked items after unchecked ones within each run of adjacent items, and that the file is still ordinary markdown you can reorder by hand.

---

## 8. Ordered task list for the executor

1. **Rewrite `ChecklistMarkdownParser.cs`** — add `SetItemChecked`, `AppendItems`, and the private helpers (`BlockBounds`, `IsTaskLineWithIndent`, `IsLineChecked`, `AppendPoint`, `Join`); delete `ToggleItem`. Keep/extend the class-level `<remarks>` explaining the line-based-not-AST rationale plus the new reorder rule.
2. **Rewrite `ChecklistMarkdownParserTests.cs`** (§9 list) and run
   `dotnet test tests/JNotes.Tests --filter "FullyQualifiedName~ChecklistMarkdownParserTests"` — pure unit tests, **no Redis needed**, so iterate here first until the algorithm is right.
3. **`INoteService.cs`** — swap `ToggleChecklistItemAsync` for `SetChecklistItemCheckedAsync`, add `AddChecklistItemAsync`, with the doc comments from §4.2.
4. **`NoteService.cs`** — implement both against the parser, preserving the `NoteType.Checklist` guard, the `ContentOnly` scope, and the existing explanatory comment.
5. **`NoteServiceTests.cs`** — rename the three `ToggleChecklistItemAsync_*` tests and add the new ones (§9). Needs Redis.
6. **`NoteView.razor`** — set-checked handler, add-item input group, `_isMutating`, `_newItemText`/`_newItemInput`, `_listVersion` + `@key`/`id`, empty state.
7. **`ChecklistNoteEditor.razor`** — in-memory `OnItemCheckedAsync`, add-item input group, `_listVersion` + `@key`/`id`, new empty-state copy, remove `ItemToggled`.
8. **`NoteEditor.razor`** — delete `ToggleItemAsync` and the `ItemToggled=` attribute.
9. **`NavMenu.razor`** — checklist default body `""`.
10. **`README.md`** per §7. **Skip `CLAUDE.md`** unless the user has approved it.
11. **Verify** (§10), then manually smoke-test in the browser (§10.2) if a browser is available; otherwise report it as not done.

Steps 1-2 and 3-5 are the only hard ordering constraint (parser before service before UI); 6-9 can be done in any order once 3-4 compile.

---

## 9. Tests to add

### 9.1 `tests/JNotes.Tests/Serialization/ChecklistMarkdownParserTests.cs` (pure unit, no Redis)

Keep the two existing `Parse_*` tests unchanged. Replace all `ToggleItem_*` tests. Suggested shared fixtures: the existing `MixedBody`, plus a `MultiBlockBody` (`## Groceries` / two items / blank / `## Hardware` / one item) and a `NestedBody` (parent + two indented children + sibling).

`SetItemChecked`:
1. `SetItemChecked_checking_an_item_moves_its_line_to_the_bottom_of_its_block`
2. `SetItemChecked_unchecking_an_item_moves_it_to_the_end_of_the_unchecked_run`
3. `SetItemChecked_preserves_relative_order_within_each_group`
4. `SetItemChecked_is_idempotent_when_the_item_is_already_in_the_requested_state`
5. `SetItemChecked_leaves_an_already_ordered_block_untouched_apart_from_the_bracket`
6. `SetItemChecked_does_not_move_prose_or_blank_lines`
7. `SetItemChecked_never_moves_an_item_out_of_its_own_block` (uses `MultiBlockBody`)
8. `SetItemChecked_treats_a_blank_line_as_a_block_boundary`
9. `SetItemChecked_treats_differently_indented_items_as_separate_blocks` (uses `NestedBody`; checking the parent moves nothing, checking a child reorders only among children)
10. `SetItemChecked_does_nothing_to_order_for_a_single_item_block`
11. `SetItemChecked_repairs_an_interleaved_block_into_unchecked_then_checked`
12. `SetItemChecked_preserves_CRLF_line_endings_when_present`
13. `SetItemChecked_preserves_uppercase_X_marks_on_other_lines`
14. `SetItemChecked_throws_when_target_line_is_not_a_checklist_item` (`InvalidOperationException`)
15. `SetItemChecked_throws_when_line_index_is_out_of_range` (`ArgumentOutOfRangeException`)
16. `SetItemChecked_then_Parse_reports_line_indices_matching_the_reordered_body` (round-trip guard for the index-shift concern)

`AppendItems`:
17. `AppendItems_adds_a_task_line_to_an_empty_body`
18. `AppendItems_appends_after_the_last_unchecked_item_and_above_checked_ones`
19. `AppendItems_appends_to_the_last_block_when_the_body_has_several_blocks`
20. `AppendItems_appends_at_the_end_of_a_body_with_no_checklist_lines`
21. `AppendItems_preserves_a_trailing_newline`
22. `AppendItems_trims_surrounding_whitespace_from_the_text`
23. `AppendItems_creates_one_item_per_non_blank_line_of_multiline_text`
24. `AppendItems_matches_the_indent_of_the_block_it_appends_to`
25. `AppendItems_preserves_CRLF_line_endings_when_present`
26. `AppendItems_keeps_markdown_looking_text_on_a_single_line` (`Theory`: `"- [ ] nested"`, `"---"`, `"# heading"`, `"[x]"` — assert `Parse` round-trips the text)
27. `AppendItems_stores_very_long_text_as_one_line`
28. `AppendItems_throws_for_text_with_no_non_blank_content` (`Theory`: `""`, `"   "`, `"\n\n"`, `"\r\n"` → `ArgumentException`)

### 9.2 `tests/JNotes.Tests/Integration/NoteServiceTests.cs` (real SQLite + Redis + temp folder)

Rename/replace the three existing toggle tests, then add:
29. `SetChecklistItemCheckedAsync_checks_the_item_and_moves_it_to_the_bottom_on_disk` (assert via `GetAsync`, not just the return value)
30. `SetChecklistItemCheckedAsync_unchecking_moves_the_item_back_above_the_checked_ones`
31. `SetChecklistItemCheckedAsync_throws_for_a_non_checklist_note`
32. `SetChecklistItemCheckedAsync_does_not_update_the_index_immediately` (port of the existing `ToggleChecklistItemAsync_does_not_update_the_index_immediately`, comment intact)
33. `AddChecklistItemAsync_appends_the_item_and_persists_it_to_disk`
34. `AddChecklistItemAsync_places_a_new_item_above_already_checked_items`
35. `AddChecklistItemAsync_adds_one_item_per_line_for_multiline_text`
36. `AddChecklistItemAsync_throws_for_a_non_checklist_note`
37. `AddChecklistItemAsync_throws_for_whitespace_only_text`
38. `AddChecklistItemAsync_does_not_update_the_index_immediately`
39. `AddChecklistItemAsync_then_a_rescan_makes_the_new_item_searchable` — add, `await _fixture.Reconciler.RescanAsync(_fixture.UserAId)`, then `ListAsync(new NoteSearchQuery(SearchText: ...))` finds it. Pins the documented `ContentOnly` staleness contract (D5) as intended behaviour rather than an accident.

No Razor component tests exist in this repo (no bUnit), so the UI is verified manually (§10.2) — consistent with how every other page in the app is covered.

---

## 10. Verification

### 10.1 Commands (from the repo root)

```bash
dotnet build JNotes.slnx
dotnet test tests/JNotes.Tests --filter "FullyQualifiedName~ChecklistMarkdownParserTests"   # fast loop, no Redis
dotnet test JNotes.slnx
dotnet format JNotes.slnx --verify-no-changes
```

**Integration tests need a real Redis** — `CoreServicesFixture` connects to `localhost:6379` unless `JNOTES_TEST_REDIS_CONNECTION_STRING` is set. Start one first (e.g. `docker run --rm -p 6379:6379 valkey/valkey`) or the `Integration/` and `Users/` suites will fail for environmental reasons, not code ones.

### 10.2 Manual smoke test (`dotnet watch --project src/JNotes.Web run`)

1. New note → Checklist → editor shows the empty state and the add box (no stray empty checkbox).
2. Add three items from the editor; confirm the raw-markdown panel shows three `- [ ] …` lines in order.
3. Open `/notes/{id}` → add a fourth item with Enter; confirm focus stays in the box and a fifth can be typed straight away.
4. Check the first item → it drops to the bottom, strikethrough, **and the checkbox you clicked is the one that ends up ticked** (this is the D8 regression: if the wrong row appears ticked, the `@key` is missing or wrong).
5. Uncheck it → it returns just above the checked items, not to its original slot.
6. Reload the page; order persists. Open the `.md` under `src/JNotes.Web/App_Data/notes/{userId}/` and confirm the file order matches the screen.
7. Hand-edit the file to interleave checked/unchecked, reload, toggle one item → the block repairs itself.
8. Note with `## Heading` + items + blank line + second `## Heading` + items: check items in each block, confirm nothing crosses a heading or blank line.
9. Try to add an empty/whitespace item: the Add button is disabled and Enter does nothing.
10. In the editor, type into the body raw-markdown panel and *immediately* tick a checkbox — the typed text must survive (this is the D6 fix).

---

## 11. Risks and open questions (each with a recommended default)

**R1 — Physical reorder rewrites more of the file than a toggle used to.** A user who deliberately hand-ordered a partially-checked list will see it rearranged on the first toggle, and git diffs get bigger.
*Default:* ship physical reorder (D1). *Cheap fallback if rejected:* revert to display-only by sorting the parsed list in the two Razor components (`Items.OrderBy(i => i.IsChecked)`) and dropping the reorder from the parser — index-based mutation keeps working because `LineIndex` travels on each `ChecklistItem`. That fallback costs ~2 lines but breaks file/UI agreement and desyncs the editor's two panes, which is why it is not the default.

**R2 — Blank lines split a block, so items separated by a blank line never reorder relative to each other.** Markdown would still call that one loose list.
*Default:* keep the simple "consecutive lines" rule and document it. Revisit only if it annoys someone in practice.

**R3 — The full-block partition can move several lines on the first toggle of an imported file.**
*Default:* keep it (self-healing, one crisp post-condition). *Alternative:* move only the toggled line, which preserves but never restores the invariant.

**R4 — `ContentOnly` means a newly added item isn't searchable until a rescan.** With the auto-index background service this is typically minutes.
*Default:* `ContentOnly`, consistent with toggles. *Alternative:* `Metadata` for `AddChecklistItemAsync` only — makes new items instantly searchable and bumps the note to the top of the list, at the cost of an inconsistency with toggling and an index write per added item.

**R5 — Residual stale-index window on the view page.** A click on a *different* item landing in the few ms between a mutation completing and the re-render arriving could apply to the wrong line.
*Default:* accept — it is the same window the existing `_isToggling` design already accepts, the D4 "set" API removes the double-flip failure mode, and the state self-corrects on the next render. *If it ever matters:* extend the API to `SetChecklistItemCheckedAsync(noteId, lineIndex, expectedText, isChecked)` and no-op on a text mismatch; that closes it completely in the user-visible sense (a mismatch only matters when the texts differ) at the cost of a wider signature and a swallow-on-mismatch policy.

**R6 — D6 changes editor toggle persistence from immediate to debounced.** If the browser tab is killed within 800 ms of a toggle *and* `DisposeAsync` doesn't run, that toggle is lost.
*Default:* accept — the editor already trusts autosave for every keystroke of body text; a toggle is no more precious. This step is separable: skipping it leaves the editor calling `SetChecklistItemCheckedAsync` as before (with the pre-existing debounced-edit clobber bug intact), so do not skip it without recording why.

**R7 — Removing `ToggleChecklistItemAsync`/`ToggleItem` is a breaking API change.** Both are internal to this solution (grep confirms: two call sites and two test classes, all in-repo).
*Default:* remove. Keeping a delegating wrapper would be dead code.

**R8 — `AppendItems` name vs. single-item usage.** The method appends one *or more* items depending on newlines in the text, which reads slightly oddly at a single-item call site.
*Default:* keep `AppendItems` (honest about the multi-line case) and put the behaviour in the XML doc, rather than an `AppendItem` that can secretly append several.

**R9 — Changing `NavMenu`'s checklist default body to `""`** alters what a brand-new checklist looks like.
*Default:* do it — a stray empty checkbox row was only ever a workaround for having no add-item UI. Existing notes are unaffected.

**R10 — Cross-tab writes are last-writer-wins.** View-page add/set is a disk read-modify-write; an editor circuit's autosave writes its whole in-memory body. Two tabs on the same note can overwrite each other.
*Default:* out of scope and unchanged by this work. Worth one line in the CLAUDE.md paragraph (if approved) so it is a known, recorded limitation rather than a surprise.
