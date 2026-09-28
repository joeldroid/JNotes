# Plan: Per-user tag auto-suggest backed by a derived `NoteIndexUserTags` table

## Goal
When typing tags in the note editor (existing notes and the `/notes/new/{type}` draft), the user gets suggestions drawn from tags they have already used. Suggestions come from a new per-user `NoteIndexUserTags` table. The table is a derived cache like the other `NoteIndex*` tables, is kept in sync inside the same repository transaction that already writes `NoteIndexTags`, and is rebuilt on every reconciliation pass. The comma-separated tag text box is replaced by a chip list plus a `MudAutocomplete<string>` that also accepts new tags typed as free text.

## Context found
- Tags today: `src/JNotes.Web/Components/Pages/NoteEditor.razor` lines 34-36 is a `MudTextField` bound to `_tagsText` (comma-separated). `ParseTags()` (line 184) splits on `,` with trim. `OnMetadataEdited` (line 169) sets `_note.Tags` and triggers the 500 ms metadata debounce (skipped when `IsNew`). `SaveNewAsync` (line 249) passes `ParseTags()` to `CreateAsync`. `HasDraftContent` (line 113) checks `_tagsText`. `@inject INoteService NoteService` already exists, so the `ICurrentUserContext` gotcha is already handled on this page.
- Tag filter on the list: `src/JNotes.Web/Components/Notes/NoteListView.razor` uses a `MudSelect` fed by `NoteService.GetDistinctTagsAsync()` from `src/JNotes.Web/Components/Pages/NoteList.razor` line 36. The comment on `AllTags = ","` says a tag can never contain a comma.
- Index: `src/JNotes.Core/Indexing/PostgresNoteIndexRepository.cs` is a singleton that makes one context per call through `IDbContextFactory<JNotesDbContext>`. `UpsertAsync` runs in an explicit transaction: `ExecuteDelete` the old tags, upsert the entry, add the tags, `SaveChanges`, commit. `DeleteAsync` has no transaction and relies on the tag cascade. `GetDistinctTagsAsync` does a join plus `Distinct`, then an in-memory `StringComparer.Ordinal` sort. Search uses `SqlLikePattern` (`src/JNotes.Core/Indexing/SqlLikePattern.cs`: `Escape`, `ContainsPattern`, `EscapeString`).
- `src/JNotes.Core/Indexing/INoteIndexRepository.cs` takes an explicit `userId`. Its only callers are `NoteService` and `NoteIndexReconciler`.
- `src/JNotes.Core/Notes/NoteService.cs` is the implicit-user front:
  - `CreateAsync`, `UpdateAsync` and `AutosaveAsync(Metadata)` go through `WriteAndIndexAsync`, then `IndexAsync`, then `UpsertAsync`.
  - `AutosaveAsync(ContentOnly)` writes the file only.
  - `TrashAsync` calls `DeleteAsync`. `RestoreAsync` calls `IndexAsync`.
  - `PurgeAsync` touches the file only, because a trashed note has no index row.
- `src/JNotes.Core/Indexing/NoteIndexReconciler.cs` `RescanAsync(userId)` skips files whose mtime is unchanged. That means a new table would never be filled for existing installs unless the reconciler rebuilds it explicitly or the migration backfills it. Startup runs it for every user (`HostExtensions.RunStartupReconciliationAsync` → `IAllUsersIndexReconciler`), as do the auto-index timer and Settings → Rescan Now (`Settings.razor` line 403).
- `src/JNotes.Core/Data/JNotesDbContext.cs`:
  - `NoteIndexEntries` deliberately has no FK to Users.
  - `NoteIndexTag` has PK `(NoteIndexEntryId, Ordinal)`, an index on `Tag`, and a cascade FK. Its `Tag` column uses the default deterministic collation, so tag equality is case-sensitive.
  - The `jnotes_case_insensitive` ICU collation exists but is only used for `Username`.
- The existing migration is `src/JNotes.Core/Data/Migrations/20260922125349_InitialCreate.cs`, alongside `JNotesDbContextModelSnapshot.cs`. Command: `dotnet ef migrations add <Name> --project src/JNotes.Core --startup-project src/JNotes.Core -o Data/Migrations` (run `dotnet tool restore` first).
- DI: `src/JNotes.Core/ServiceCollectionExtensions.cs`. No new registration is needed if this goes onto existing interfaces.
- MudBlazor is 9.10.0. API checked in `~/.nuget/packages/mudblazor/9.10.0/lib/net10.0/MudBlazor.xml`:
  - `MudAutocomplete<T>` parameters that exist: `SearchFunc` (receives a `CancellationToken`), `DebounceInterval` (default 100), `MaxItems`, `MinCharacters`, `CoerceValue`, `CoerceText`, `ResetValueOnEmptyText`, `SelectValueOnTab`, `OpenOnFocus`, `Strict`, `ItemTemplate`, `NoItemsTemplate`, `ShowProgressIndicator`, `Clearable`.
  - Methods that exist: `ClearAsync()`, `FocusAsync()`, `CloseMenuAsync()`, `OpenMenuAsync()`.
  - `MudBaseInput.OnKeyDown`/`Text`/`TextChanged`/`OnBlur` exist. `MudChip<T>.OnClose` exists.
  - There is no `MudChipField`; that component lives in the third-party MudExtensions.
- Rules that apply:
  - Web never touches EF or the filesystem (`BannedSymbols.txt`).
  - Components under `Components/Notes/` inject nothing; the routed page owns `INoteService` calls.
  - MudBlazor inputs keep their own state, so rebuild them with `@key` when needed.
  - Integration tests use `CoreServicesFixture` (`tests/JNotes.Tests/Integration/CoreServicesFixture.cs`: `ActAsUserA/B`, `Scope`, `FileStore`, `Reconciler`, `IndexRepository`, `NoteService`) against real PostgreSQL. There are no bUnit tests.

## Design decisions
1. **A materialized projection of `NoteIndexTags`, not a separately written ledger.** The new table `NoteIndexUserTags` has columns `UserId`, `Tag`, `NoteCount`, `LastUsedAt`. Its rows are recomputed from `NoteIndexTags` ⋈ `NoteIndexEntries` for the affected tags only, inside the same transaction as `UpsertAsync`/`DeleteAsync`.
   - It is therefore consistent by construction with every path that already maintains the index: NoteService create/metadata/restore/trash, and reconciler add/update/remove. Nothing is added in `NoteService` or the UI.
   - It can be rebuilt from `NoteIndexTags`, which itself rebuilds from disk. `RescanAsync` ends with a full per-user rebuild, which covers upgrades, TRUNCATE and drift.
   - Rejected: incrementing and decrementing counters from `NoteService`. That drifts on crashes, has no source to rebuild from, and needs code at every call site.
   - Rejected: a DB trigger. It hides logic from C# and tests, and EF model/migration churn.
   - Rejected: deriving suggestions on the fly from `NoteIndexTags`. The user explicitly asked for a dedicated table.
2. **Key `(UserId, Tag)` with the default (deterministic, case-sensitive) collation.** This matches `NoteIndexTags.Tag` and the case-sensitive exact tag filter. "Work" and "work" stay two rows. Using `jnotes_case_insensitive` would merge them and silently drop one spelling. Matching is still case-insensitive through `ILIKE`.
3. **`UserId` is a column; there is no FK to Users.** This mirrors `NoteIndexEntries`, so writing the cache can never fail because of account-row ordering. A deleted user's rows are left behind, as the existing index rows already are; user ids are never reused GUIDs.
4. **Trashed notes contribute no tags.** They have no index row. Restore brings their tags back, and Purge changes nothing.
5. **Scope and staleness.** Tags are metadata, so a `Metadata` save updates suggestions immediately. A `ContentOnly` save that happens to carry a changed tag set only reaches the index at the next Metadata save or rescan, which is the same documented trade-off as today. In practice the editor's 500 ms metadata debounce fires anyway.
6. **API surface.**
   - New `SuggestTagsAsync` and `RebuildUserTagsAsync` go on `INoteIndexRepository` (explicit `userId`), because sync must share the transaction.
   - `SuggestTagsAsync` also goes on the existing `INoteService` (implicit `ICurrentUserContext`). The user can therefore only ever query their own tags, and Web keeps its existing allowed-interface list.
   - `GetDistinctTagsAsync` switches to read the new table; its behaviour is unchanged.
7. **Matching and ranking.** Matching is case-insensitive contains, with `%`, `_` and `\` escaped. Rank order: prefix matches before contains matches, then `NoteCount` desc, then `LastUsedAt` desc, then `Tag`. An empty term returns the most-used tags. Tags already on the note are excluded case-insensitively. The default limit is 10, clamped to 1-50.
8. **UI.** A new `TagInput` component shows the current tags as closable `MudChip`s and has a `MudAutocomplete<string>` for adding.
   - Free text is supported by `SearchFunc` appending the typed text as a pseudo-item "Add new tag “x”". This happens only when the text is non-empty and doesn't case-insensitively equal a suggestion or an existing tag.
   - All adds therefore go through `ValueChanged`, with no fragile Enter-key interception and no `CoerceValue`, which would push partial text into Value while typing.
   - The list/search `MudSelect` stays as it is: it is an exact-match filter over every tag, so a full dropdown is already the suggestion UI, and free text there would only produce empty filters.

## Steps

### Phase 1: Core data model and migration (solution builds)
1. **Create `src/JNotes.Core/Data/NoteIndexUserTag.cs`** with `public sealed class NoteIndexUserTag`:
   - Properties: `required Guid UserId`, `required string Tag`, `required int NoteCount`, `required DateTimeOffset LastUsedAt`.
   - Doc comment: this is a derived per-user projection of `NoteIndexTags`. It is owned by `PostgresNoteIndexRepository` (recomputed in `UpsertAsync`/`DeleteAsync`, fully rebuilt by `RebuildUserTagsAsync` at the end of every `RescanAsync`), is safe to TRUNCATE, and deliberately has no FK to Users.
   - Define `NoteCount` as the number of distinct indexed notes carrying the tag, and `LastUsedAt` as the max `UpdatedAt` of those notes.
2. **Edit `src/JNotes.Core/Data/JNotesDbContext.cs`:**
   - Add `public DbSet<NoteIndexUserTag> NoteIndexUserTags => Set<NoteIndexUserTag>();`.
   - In `OnModelCreating`:
     ```csharp
     modelBuilder.Entity<NoteIndexUserTag>(entity =>
     {
         entity.ToTable("NoteIndexUserTags");
         entity.HasKey(e => new { e.UserId, e.Tag });
     });
     ```
   - Do not set a collation (deterministic on purpose; add a one-line comment explaining why). Add no FK.
3. **Generate the migration** from the repo root: `dotnet tool restore`, then `dotnet ef migrations add AddNoteIndexUserTags --project src/JNotes.Core --startup-project src/JNotes.Core -o Data/Migrations`.
   - Then edit the generated `Up` to add a backfill after `CreateTable`, so suggestions work immediately after upgrade even before the first rescan:
     ```sql
     INSERT INTO "NoteIndexUserTags" ("UserId","Tag","NoteCount","LastUsedAt")
     SELECT e."UserId", t."Tag", COUNT(DISTINCT e."Id"), MAX(e."UpdatedAt")
     FROM "NoteIndexTags" t JOIN "NoteIndexEntries" e ON e."Id" = t."NoteIndexEntryId"
     GROUP BY e."UserId", t."Tag";
     ```
     Add it via `migrationBuilder.Sql(...)`. `Down` just drops the table (generated).
   - Check that the generated column types are `uuid`, `text`, `integer` and `timestamp with time zone`, and that the snapshot was updated.

### Phase 2: Repository sync, suggest, rebuild
4. **Edit `INoteIndexRepository.cs`** and add:
   ```csharp
   /// Case-insensitive contains match over userId's distinct tags; prefix matches first, then by note count / last use.
   Task<IReadOnlyList<string>> SuggestTagsAsync(Guid userId, string? term, IReadOnlyCollection<string> exclude, int limit, CancellationToken cancellationToken = default);
   /// Recomputes every NoteIndexUserTags row for userId from NoteIndexTags/NoteIndexEntries.
   Task RebuildUserTagsAsync(Guid userId, CancellationToken cancellationToken = default);
   ```
5. **Edit `PostgresNoteIndexRepository.cs`.**
   - **Add a private helper** `RecomputeUserTagsAsync(JNotesDbContext db, Guid userId, string[] tags, CancellationToken ct)`. It returns immediately if `tags.Length == 0`. Otherwise it runs two `db.Database.ExecuteSqlAsync($"...")` statements (interpolated, so parameterized; Npgsql binds `string[]` for `= ANY(...)`).
     - (a) Delete the rows that are no longer used:
       ```sql
       DELETE FROM "NoteIndexUserTags" u
       WHERE u."UserId" = {userId} AND u."Tag" = ANY({tags})
         AND NOT EXISTS (SELECT 1 FROM "NoteIndexTags" t
                         JOIN "NoteIndexEntries" e ON e."Id" = t."NoteIndexEntryId"
                         WHERE e."UserId" = {userId} AND t."Tag" = u."Tag")
       ```
     - (b) Upsert the recomputed aggregates:
       ```sql
       INSERT INTO "NoteIndexUserTags" ("UserId","Tag","NoteCount","LastUsedAt")
       SELECT e."UserId", t."Tag", COUNT(DISTINCT e."Id"), MAX(e."UpdatedAt")
       FROM "NoteIndexTags" t JOIN "NoteIndexEntries" e ON e."Id" = t."NoteIndexEntryId"
       WHERE e."UserId" = {userId} AND t."Tag" = ANY({tags})
       GROUP BY e."UserId", t."Tag"
       ON CONFLICT ("UserId","Tag") DO UPDATE
         SET "NoteCount" = EXCLUDED."NoteCount", "LastUsedAt" = EXCLUDED."LastUsedAt"
       ```
     - Use `ON CONFLICT`, not delete-then-insert. Two circuits saving notes that share a tag would otherwise hit a PK violation.
   - **`UpsertAsync`:**
     - Before the existing `ExecuteDeleteAsync` of old tags, read the old tag strings for `entry.Id`: `db.NoteIndexTags.Where(t => t.NoteIndexEntryId == entry.Id).Select(t => t.Tag).ToListAsync()`.
     - After `SaveChangesAsync` and before `CommitAsync`, call `RecomputeUserTagsAsync(db, entry.UserId, old.Concat(tags).Distinct(StringComparer.Ordinal).ToArray(), ct)`. Include all the tags, not just the changed ones, because `LastUsedAt` depends on the entry's `UpdatedAt`.
     - Edge case: if `existing` belonged to a different `UserId`, which cannot happen in practice since ids are GUIDs, also recompute for `existing.UserId`. Capture the old `UserId` before overwriting and, if it differs, call the helper for it too.
   - **`DeleteAsync`:** wrap it in `BeginTransactionAsync`.
     - Read the note's tags, but only if the entry matches both `noteId` and `userId`: join `NoteIndexTags` to `NoteIndex` where `e.Id == noteId && e.UserId == userId`.
     - Then run the existing `ExecuteDeleteAsync`, call `RecomputeUserTagsAsync(db, userId, tags, ct)`, and commit.
     - Keep the userId scoping, so another user's id is still a no-op that leaves their tags untouched.
   - **`RebuildUserTagsAsync`:** in a transaction, `db.NoteIndexUserTags.Where(u => u.UserId == userId).ExecuteDeleteAsync()`, then the same `INSERT ... SELECT ... GROUP BY` as (b) filtered by `e."UserId" = {userId}` only (no `ANY`; keeping `ON CONFLICT DO UPDATE` is harmless and race-safe), then commit.
   - **`GetDistinctTagsAsync`:** replace the join with `db.NoteIndexUserTags.AsNoTracking().Where(u => u.UserId == userId).Select(u => u.Tag).ToListAsync()`. Keep the in-memory ordinal sort and its comment.
   - **`SuggestTagsAsync`:**
     - Clamp `limit` to 1-50. Set `var excluded = exclude.Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).Distinct().ToArray();`.
     - Base query: `db.NoteIndexUserTags.AsNoTracking().Where(u => u.UserId == userId && !excluded.Contains(u.Tag.ToLower()))`.
     - If `term` is not blank after trimming, add `Where(u => EF.Functions.ILike(u.Tag, SqlLikePattern.ContainsPattern(t), SqlLikePattern.EscapeString))`. Order with `OrderBy(u => EF.Functions.ILike(u.Tag, SqlLikePattern.Escape(t) + "%", SqlLikePattern.EscapeString) ? 0 : 1)`, then `ThenByDescending(u => u.NoteCount).ThenByDescending(u => u.LastUsedAt).ThenBy(u => u.Tag)`.
     - If the term is blank, use the same order without the prefix key.
     - Finish with `.Select(u => u.Tag).Take(limit).ToListAsync()`.
     - UNVERIFIED: that EF translates the `ILike ? 0 : 1` ordering to `CASE`. If it throws, fall back to materializing the filtered rows (one user's matches, small), then ordering and `Take` in memory with the same keys, using `StartsWith(t, StringComparison.OrdinalIgnoreCase)` for the prefix key.
   - Update the class doc comment to mention the third table.
6. **Edit `src/JNotes.Core/Indexing/NoteIndexReconciler.cs`:** after the removal loop and before `return`, add `await _indexRepository.RebuildUserTagsAsync(userId, cancellationToken);`. Comment it: it heals a truncated or drifted `NoteIndexUserTags` even when every file is unchanged, because the mtime skip would otherwise never touch it. Leave `ReconciliationResult` unchanged.

### Phase 3: Service front and pure helper
7. **Create `src/JNotes.Core/Notes/TagList.cs`**, a `public static class` holding pure logic that Web may call, like `UserInitials`:
   - `IReadOnlyList<string> ParseInput(string? text)`: split on `,`, trim each part, drop empties.
   - `IReadOnlyList<string> AddTags(IReadOnlyList<string> existing, IEnumerable<string> toAdd)`: append each parsed tag unless it is already present, compared with `StringComparer.OrdinalIgnoreCase` against `existing` and against tags already added in the same call. Preserve order.
   - `IReadOnlyList<string> RemoveTag(IReadOnlyList<string> existing, string tag)`: exact ordinal removal of the first occurrence.
8. **Edit `src/JNotes.Core/Notes/INoteService.cs`** and add:
   ```csharp
   /// <summary>The current user's previously used tags matching <paramref name="term"/> (case-insensitive; prefix matches first; empty term = most used), excluding <paramref name="exclude"/> case-insensitively. Index-only.</summary>
   Task<IReadOnlyList<string>> SuggestTagsAsync(string? term, IReadOnlyCollection<string> exclude, int limit = 10, CancellationToken cancellationToken = default);
   ```
9. **Edit `NoteService.cs`:** implement it as `_indexRepository.SuggestTagsAsync(_currentUser.UserId, term, exclude, limit, cancellationToken)`. Nothing else in `NoteService` changes; all sync happens in the repository.

### Phase 4: Web UI
10. **Create `src/JNotes.Web/Components/Notes/TagInput.razor`** (injects nothing).
    - Parameters:
      - `[Parameter, EditorRequired] IReadOnlyList<string> Tags`
      - `[Parameter] EventCallback<IReadOnlyList<string>> TagsChanged`
      - `[Parameter, EditorRequired] Func<string?, IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlyList<string>>> SuggestTags`
    - Markup: a `div.jn-tag-input` containing a `div.jn-tags`. That class is reused from `note-list.css`; confirm it is global (app.css imports it). Inside it, one `<MudChip T="string" Size="Size.Small" Variant="Variant.Outlined" OnClose="@(_ => RemoveAsync(tag))">@tag</MudChip>` per tag. Then:
      ```razor
      <MudAutocomplete @ref="_autocomplete" @key="_inputVersion" T="string" Value="_selected" ValueChanged="OnSelectedAsync"
          SearchFunc="SearchAsync" DebounceInterval="250" MaxItems="11" MinCharacters="0"
          ResetValueOnEmptyText="true" CoerceText="false" CoerceValue="false" SelectValueOnTab="true"
          Placeholder="Add a tag…" aria-label="Tags" Variant="Variant.Outlined" Margin="Margin.Dense" Dense="true">
        <ItemTemplate Context="item">@(item == _newTagCandidate ? $"Add new tag “{item}”" : item)</ItemTemplate>
      </MudAutocomplete>
      ```
    - `SearchAsync(string? value, CancellationToken token)`:
      - Returns `Task<IEnumerable<string>>`. The XML confirms the `CancellationToken` overload of `SearchFunc`. If the compiler rejects the method group, check the delegate type and adapt.
      - Compute `var typed = value?.Trim() ?? ""`. Get suggestions from `await SuggestTags(typed, Tags, 10, token)`; `Tags` is the exclude list. (Note: the `SuggestTags` delegate signature above has no `limit` parameter; the page-level wrapper supplies the limit of 10. Keep the delegate as declared and drop the `10` from this call.)
      - Set `_newTagCandidate` to `typed` if it is non-empty, contains more than commas/whitespace, and doesn't equal any suggestion or any `Tags` entry under OrdinalIgnoreCase. Otherwise set it to `null`.
      - Return the suggestions followed by `_newTagCandidate` when non-null.
      - Catch `OperationCanceledException` and return empty.
    - `OnSelectedAsync(string? value)`:
      - If the value is blank, return.
      - Otherwise `var next = TagList.AddTags(Tags, TagList.ParseInput(value))`, then `await TagsChanged.InvokeAsync(next)`.
      - Reset the input with `_selected = null; await _autocomplete.ClearAsync(); await _autocomplete.FocusAsync();`.
      - If `ClearAsync` inside `ValueChanged` proves flaky (text not cleared, or the same item unselectable twice), fall back to the repo's documented pattern: bump `_inputVersion` (the `@key`) to rebuild the input, then focus it in `OnAfterRenderAsync` behind a `_refocus` flag. That mirrors ChecklistPanel's refocus.
    - `RemoveAsync(string tag)` invokes `TagsChanged.InvokeAsync(TagList.RemoveTag(Tags, tag))`.
    - Add a short caption under the input: "Enter or Tab adds a tag · × removes one" (`MudText Typo="Typo.caption" Color="Color.Secondary"`).
    - Leave `@using JNotes.Core.Notes` to `_Imports.razor` if it is already there; otherwise add it to the component. Check `src/JNotes.Web/Components/_Imports.razor`.
11. **Edit `NoteEditor.razor`.**
    - Replace lines 34-36 with `<div class="mt-2"><TagInput Tags="_note.Tags" TagsChanged="OnTagsChanged" SuggestTags="SuggestTagsAsync" /></div>`.
    - Remove `_tagsText` and `ParseTags()`.
    - Add `private void OnTagsChanged(IReadOnlyList<string> tags) { _note!.Tags = tags; OnMetadataEdited(); }`.
    - Change `OnMetadataEdited` to drop the `_note!.Tags = ParseTags();` line and its comment. Tags are now pushed in directly, so the "keep in sync" intent still holds; move that comment to `OnTagsChanged`.
    - Change `HasDraftContent` to use `_note.Tags.Count > 0` instead of `_tagsText`.
    - In `OnParametersSetAsync`, remove both `_tagsText` assignments. The draft already sets `Tags = []`.
    - In `SaveNewAsync`, pass `_note.Tags` instead of `ParseTags()`.
    - Add `private Task<IReadOnlyList<string>> SuggestTagsAsync(string? term, IReadOnlyCollection<string> exclude, CancellationToken ct) => NoteService.SuggestTagsAsync(term, exclude, 10, ct);`. `NoteService` is already page-injected, so the gotcha is satisfied.
    - Autosave: an add or remove triggers the metadata debouncer, which saves with `NoteSaveScope.Metadata`. That reindexes and updates `NoteIndexUserTags` in the same transaction. Drafts don't autosave.
12. **CSS:** in `src/JNotes.Web/wwwroot/css/note-editor.css`, add a native-nested `.jn-tag-input { display: flex; flex-direction: column; gap: 0.25rem; & .jn-tags:empty { display: none; } }`. No new file is needed. Chips use the palette variables already in place.
13. **No change** to `NoteListView`/`NoteList`: the MudSelect is kept, and `GetDistinctTagsAsync` now reads the new table transparently.
14. Run `dotnet build JNotes.slnx` and `dotnet format JNotes.slnx`.

## Tests to add or update
All tests go in `tests/JNotes.Tests/`.

**Unit: `Notes/TagListTests.cs`** (pure, no database):
- `ParseInput` splits on commas, trims, and drops blanks (`" a, ,b ,"` gives `["a","b"]`). Null gives empty.
- `AddTags` skips case-insensitive duplicates of existing tags and within the same batch, and preserves order and the original spelling.
- `RemoveTag` removes by exact ordinal match only.

**Integration: new `Integration/TagSuggestionTests.cs`** (`IAsyncLifetime`, `CoreServicesFixture`, same shape as `NoteIndexRepositoryTests`):
1. Cross-user isolation: A creates notes tagged "alpha" and "shared"; B creates "alphabet" and "shared". Via `NoteService` with `ActAsUserA`, `SuggestTagsAsync("alp", [])` gives exactly `["alpha"]`; with `ActAsUserB` it gives `["alphabet"]`. Blank-term results per user contain only that user's tags. At repository level, `SuggestTagsAsync(UserBId, "", [], 50)` never contains "alpha", and B's "shared" count is 1, not 2 (check through a DB read of `NoteIndexUserTags`).
2. Cross-user delete no-op: `IndexRepository.DeleteAsync(UserBId, aNoteId)` leaves A's note and suggestions intact.
3. Sync on create: after `CreateAsync` with ["work"], "work" is suggested. A second note with "work" makes "work" rank above a tag used once with the same prefix.
4. Sync on metadata update: change a note's tags from ["old"] to ["new"] via `UpdateAsync`. "new" is suggested and "old" is gone. If "old" is also on another note, it survives with count 1 (read the row through `IDbContextFactory<JNotesDbContext>` from `_fixture.Scope.ServiceProvider`).
5. ContentOnly trade-off: `AutosaveAsync(note with changed Tags, NoteSaveScope.ContentOnly)` does not change suggestions. After `Task.Delay(1100)` and `Reconciler.RescanAsync(UserAId)`, it does.
6. Trash, restore, purge: `TrashAsync` removes a tag used only by that note. `RestoreAsync` brings it back. Trash then `PurgeAsync` leaves it absent, and suggestions for other tags are unchanged.
7. Rebuild from disk (external add): a note written via `FileStore.WriteAsync(UserAId, note with Tags=["external"])`, then `RescanAsync`, makes "external" suggested. A file deleted via `FileStore.DeleteAsync`, then rescan, removes its only-use tag.
8. Rebuild after truncate with unchanged files: create notes, run `TRUNCATE "NoteIndexUserTags"` via a context from the factory (`ExecuteSqlRawAsync`), confirm suggestions are empty, then `RescanAsync` with `result.Updated == 0` restores the suggestions.
9. Case and collation: tags "Work" (note 1) and "work" (note 2) give two distinct suggestions for term "WOR". `GetDistinctTagsAsync` returns both, ordinal-sorted (`["Work","work"]`). `exclude: ["WORK"]` removes both. The exact tag filter `ListAsync(new NoteSearchQuery(Tag: "Work"))` still returns only note 1, so there is no regression.
10. Ranking: tags "homework" (used 3×) and "work" (1×) with term "work" give `["work","homework"]`, because prefix beats count. An empty term orders by count and respects `limit`.
11. Wildcard escaping: tags "100%", "1000", "snake_case", "snakeXcase". "100%" gives only "100%"; "snake_" gives only "snake_case".
12. A duplicate tag within one note counts once: tags ["x","x"] give `NoteCount == 1`.

**Update existing tests:** run the existing `GetDistinctTagsAsync` tests in `NoteIndexRepositoryTests`/`NoteServiceTests`; they should pass unchanged. Tests that call `IndexRepository.UpsertAsync` directly now also populate the new table, which is expected.

Commands:
- `dotnet test JNotes.slnx`, which needs Postgres at `localhost:5432` with jnotes/jnotes and CREATEDB.
- Single class: `dotnet test tests/JNotes.Tests --filter "FullyQualifiedName~TagSuggestionTests"`.

## Manual verification (for the tester)
Prerequisites:
- PostgreSQL on `localhost:5432`, db/user/password `jnotes`. Use the existing throwaway container, or `docker run --rm -p 5432:5432 -e POSTGRES_USER=jnotes -e POSTGRES_PASSWORD=jnotes -e POSTGRES_DB=jnotes postgres:18-alpine`.
- Then `dotnet build JNotes.slnx`, then `dotnet run --project src/JNotes.Web`, and open the printed URL. The migration applies on startup.
- Two users are needed: an Admin and a Member created in Settings → User management.

1. **Upgrade backfill.** Use an existing DB that already has tagged notes. After startup, open an existing note → Edit and click into the tag box: previously used tags appear before you type anything.
2. **Edit an existing note** (`/notes/{id}/edit`).
   - Existing tags show as chips.
   - Type "wo": after about 250 ms, matching tags appear. Prefix matches come first, and tags already on the note are not listed, even with different case.
   - Press Enter: the highlighted suggestion becomes a chip and the input clears and keeps focus.
   - The status shows "Saving…" and then "All changes saved".
3. **Free-text new tag.** Type "brandnewtag": the list shows "Add new tag “brandnewtag”". Select it (arrow keys plus Enter, or click) and it becomes a chip. Open another note's editor and type "brand": "brandnewtag" is now suggested.
4. **Comma input.** Type "a1, b2" and select "Add new tag “a1, b2”": two chips appear, "a1" and "b2".
5. **Remove.** Click × on a chip: it disappears and autosaves. If the tag was used on no other note, it no longer appears in suggestions after reopening an editor, or in the `/` Tag filter dropdown.
6. **Duplicate guard.** On a note with "work", typing "WORK" offers no "Add new" item and no "work" suggestion.
7. **New-note draft.**
   - Sidebar → New text note. Add tags via suggestions and free text.
   - Click Discard: the confirm dialog appears because tags count as draft content.
   - Repeat, enter a title and click Save: the view `/notes/{id}` shows the chips.
   - Repeat for New checklist.
8. **Trash and restore.** Delete a note whose tag is unique, and the tag disappears from suggestions and the `/` Tag filter. Restore it from `/trash` and the tag is back.
9. **Per-user isolation.** Sign out, sign in as the Member, create a note tagged "memberonly". Sign back in as the Admin: "memberonly" is never suggested and is not in the `/` Tag filter, and the Member doesn't see the Admin's tags either.
10. **External edit and rescan.** Add `tags: [handedit]` to a `.md` under `src/JNotes.Web/App_Data/notes/{userId}/`, then click Settings → Index → Rescan Now: "handedit" is suggested.
11. **Rebuild.** Run `psql -h localhost -U jnotes -d jnotes -c 'TRUNCATE "NoteIndexUserTags"'`, then Rescan Now (or restart): suggestions return.
12. **Regression.** The `/` Tag filter still filters exactly. Search by tag text in the sidebar still works. Autosave of body text is unaffected. Toggle the theme to check chip and autocomplete contrast in both themes.
13. **Narrow viewport** (below 960px): the tag input and popover don't overflow horizontally.

## Risks and edge cases
- `ClearAsync` or `FocusAsync` inside `ValueChanged` may be re-entrant or flaky in MudBlazor 9. Step 10 gives the `@key` rebuild fallback.
- Enter with the popover highlighting the first suggestion means a user who wants a brand-new tag that is a prefix of an existing one must arrow down to "Add new". This is a deliberate trade-off; see Open questions.
- Text typed but not selected is not a tag. Clicking Save on a draft with pending text in the box drops it. Mitigated by the caption; could be added later (commit on blur).
- Concurrency: two tabs updating notes with a shared tag. `ON CONFLICT` avoids PK failures, and a momentarily stale count is healed by the next rescan.
- Extra SQL per `UpsertAsync`/`DeleteAsync` (one read plus two statements). Negligible, but it lengthens each reconciler per-note transaction slightly on first index of large collections.
- EF translation of the conditional `ILIKE` ordering is UNVERIFIED. An in-memory fallback is specified.
- `ToLower()` in SQL vs `ToLowerInvariant()` in C# can differ for exotic Unicode. The only effect is a rare non-exclusion, which is harmless because the UI's `TagList.AddTags` still dedups.
- Deleted users' `NoteIndexUserTags` rows linger, like their `NoteIndexEntries`. User ids are never reused, so nothing leaks.
- The migration backfill runs once over the whole `NoteIndexTags` table, which is fine for this app's scale.

## Open questions
- Should Enter default to the top suggestion or to the literal typed text? Recommended default: top suggestion first, with "Add new …" last. This matches common tag inputs and promotes spelling consistency.
- Should the `/` list Tag filter become an autocomplete? Recommended default: no. Keep the `MudSelect`, since it's an exact filter and now reads the new table.
- Should the `CLAUDE.md` "Storage and indexing" section document `NoteIndexUserTags`? Recommended default: yes, as one bullet, but only if the user approves editing CLAUDE.md. The executor should propose the text in its report rather than edit the file unasked.

## Out of scope
- `CLAUDE.md`, the README, the Dockerfile, docker-compose, and `BannedSymbols.txt`.
- Converting the NavMenu search box or the `NoteListView` Tag `MudSelect` to autocomplete.
- Tag rename/merge tools, tag management UI, and case normalization of existing tags in files.
- Changing `NoteIndexTags` schema or collation, the search semantics (`SearchAsync`), or the ContentOnly/Metadata autosave rules.
- Suggestions from trashed notes, cross-user or shared tags, and bUnit component tests.
- Any new HTTP endpoint. Everything rides the circuit through `INoteService`.
