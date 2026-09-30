# Plan: Per-note sharing (Read / Edit) between users

Files stay in the owner's folder, and access is enforced in Core.

## Goal

An owner can share one note with any other user on the instance, choosing Read or Edit for each recipient. Recipients see the note in All Notes, in search and on a new "Shared with Me" page, with a "Shared by …" chip. Edit recipients can change the body only (text, or checklist items and toggles). Only the owner can share, rename, retag, trash, restore or purge. The note file never moves or gets copied. A new source-of-truth table, `NoteShares`, holds the grants, and every access check is made in Core on every call, so revoking or changing a permission takes effect on the recipient's next action.

## Requirements (confirmed with the user)

- The note is stored only under the original owner's folder (`{NotesFolder}/{ownerId}/{noteId}.md`). Sharing never copies it.
- Per-recipient permission: **Read** or **Edit**.
- Recipients are picked from all user accounts on the instance; any user can share their own notes. No admin toggle.
- **Edit = body only** (text body, checklist add/toggle). Not title, not tags, not re-share, not delete.
- Only the owner can delete (trash / restore / purge) or manage shares.
- Recipient can **Leave** a share.
- Owner trash hides the note for recipients; restore brings shares back; purge deletes share rows.
- Shared notes appear in All Notes + search **and** on a new "Shared with Me" sidebar page.
- Card label: recipients see "Shared by <name> · View only / Can edit"; owners see "Shared · N".

## Context found

- **Rules (CLAUDE.md):** Web only talks to Core interfaces (BannedSymbols bans `File`/`Directory`/`DbContext`/Npgsql, `src/JNotes.Web/BannedSymbols.txt`). All Core DI lives in `AddJNotesCore` (`src/JNotes.Core/ServiceCollectionExtensions.cs`). Services that only serve the current user use an implicit `ICurrentUserContext`; system callers pass an explicit `userId`. Routed pages resolve `INoteService` in their first lifecycle method, and child components inject nothing (they get delegates). Writes go file first, then index. `NoteIndex*` tables are a derived cache; `NoteAutoTagAttempts` is the precedent for a non-derived table (`src/JNotes.Core/Data/NoteAutoTagAttempt.cs`, FK to Users with cascade, no FK to the index). Migrations use Core as the startup project. Tests use a real PostgreSQL. No bUnit.
- **`src/JNotes.Core/Notes/NoteService.cs`:** every method uses `_currentUser.UserId` as the folder. `GetAsync` → `_fileStore.ReadAsync(me, id)`. `AutosaveAsync` → `WriteAsync(me, …)`, which **creates the file if it is missing**. The checklist methods go through `ReadChecklistNoteAsync` then `AutosaveAsync(ContentOnly)`. `TrashAsync`/`PurgeAsync` are own-folder only, so already a no-op for someone else's id. `IndexAsync` hard-codes `UserId = me` and `FilePath = $"{userId}/{note.Id}.md"`.
- **`src/JNotes.Core/Storage/INoteFileStore.cs` / `FileSystemNoteFileStore.cs`:** `ReadRawAsync` exists. `ReplaceIfUnchangedAsync(userId, noteId, expectedContent, replacement)` is a compare-and-swap holding `FileShare.None`, returns `false` if the file is missing or changed. `ResolveFolderAsync` calls `Directory.CreateDirectory`. There is no `Exists` method.
- **`src/JNotes.Core/Indexing/PostgresNoteIndexRepository.cs`:** `SearchAsync` filters `e.UserId == userId` and projects to an anonymous type before `new NoteSummary(...)` (line 76 is the only construction site). Raw SQL follows the `ExecuteSqlAsync($"""…ON CONFLICT…""")` pattern.
- **`NoteSummary.cs`** is a positional record. **`NoteSearchQuery.cs`** is `record NoteSearchQuery(string? SearchText = null, string? Tag = null, NoteType? Type = null)`.
- **`JNotesDbContext.cs`:** entity config style (`HasConversion<string>()`, `HasOne<UserAccount>().WithMany().HasForeignKey().OnDelete(Cascade)`). Only migration so far: `Data/Migrations/20260927025445_InitialCreate`.
- **Users:** `EfUserRepository.DeleteUserAsync` hard-deletes the row, so cascades apply. `UserManagementService.DeleteUserAsync` archives the folder first, then deletes the row. `UserAccount` has `FirstName`/`LastName`/`Username`. MainLayout display name is `FirstName ?? Username`. `UserInitials` is the pure-static helper to mirror.
- **Images:** `ImageService` (implicit user) sits over `IImageStore.OpenReadAsync(userId, name)` / `SaveAsync(userId, stream)`. `Program.cs` maps `GET /images/{fileName}` to the caller's own folder with `private, max-age=31536000, immutable`. `ImageLinks` (`RelativePrefix`, `ServedPrefix`, `ToStoredFileName`) and `ImageFormat.IsStoredFileName` / `FindStoredFileNames` handle names. `MarkdownRenderer` uses a static pipeline with `ImageLinkMarkdownExtension`. `PdfMarkdownRenderer` already uses the parse → walk `LinkInline` → `document.ToHtml(Pipeline)` pattern (Markdig 1.3.2).
- **Export:** `NoteExportService` calls `notes.GetAsync(noteId)` then `images.OpenAsync(imageName)`, which reads the current user's folder. `Export/NotePdfEndpoints.cs` maps `NoteNotFoundException` to 404.
- **`IImageCleanupService.CleanUpUserAsync(userId, minAge)`:** "mentioned" = mentioned by any `.md` in the owner's folder or trash. Shared notes stay in the owner's folder, so an image a recipient uploads into the owner's folder and links from the owner's note is kept.
- **Web:** `Pages/NoteView.razor` (`OnParametersSetAsync` → `GetAsync`; Edit/Export/Delete; `ChecklistPanel` with `Disabled`/`ReadOnly`/`OnItemChecked`). `Pages/NoteEditor.razor` (`SaveNowAsync` catches everything into `_saveError`; `DisposeAsync` flushes Metadata scope; `TagInput` has **no Disabled parameter**). `Notes/TextNoteEditor.razor` injects `IImageService` and calls `ImageService.SaveAsync(stream)`, renders `<MarkdownPreview Markdown=… />`. `Notes/MarkdownPreview.razor` injects `IMarkdownRenderer`. `Notes/NoteListItem.razor` (chips), `NoteGroupList.razor`, `NoteListView.razor`, `Pages/NoteList.razor`. `Layout/NavMenu.razor`: All Notes, `MudDivider`, Trash. Inline `MudDialog Visible=… VisibleChanged=… Options=…` pattern in `Pages/Settings.razor:256-311`. `_Imports.razor` lists Core namespaces; add `JNotes.Core.Sharing`.
- **Tests:** `tests/JNotes.Tests/Integration/CoreServicesFixture.cs` (`ActAsUserA/B`, `Scope`, users are Members `user-a`/`user-b`), `TestDoubles/TestCurrentUserContext.cs`, `RecordingPdfRenderer`. All existing `AutosaveAsync`/`UpdateAsync` tests autosave notes that exist (verified by grep). `NoteServiceTests.cs:400-403` asserts cross-user `NoteNotFoundException`, and `NoteExportServiceTests.cs:85-87` asserts the same for export; both must keep passing.
- **Versions:** MudBlazor 9.10.0. Check component parameter names against `~/.nuget/packages/mudblazor/9.10.0/lib/net10.0/MudBlazor.xml`.

## Steps

### A. Decisions and rationale

1. **Grants live in a new source-of-truth table, `NoteShares`**, not `NoteIndex*`. It can't be rebuilt from files. Rejected: storing grants in front matter (puts other users' ids in portable files, needs a rescan to see changes, lets a hand edit grant access).
2. **Access is resolved in Core on every call, nothing cached.** `NoteService.ResolveAccessAsync(noteId)`:
   - (a) If the note file exists live in the caller's own folder → `Owner`.
   - (b) Otherwise, if a `NoteShares` row exists for `(noteId, me)` → owner's id with `Edit` or `Read`.
   - (c) Otherwise → `NoteNotFoundException`.

   Own-first means an owner's notes cost only a stat, no DB query. Another user's unshared id falls to (c), a 404, so the own-folder guarantee holds. Note ids are globally unique already (`NoteIndexEntries.Id` is the PK).
3. **Recipients never write through `WriteAsync`.** Every write by an Edit recipient is a compare-and-swap merge on the owner's file: `ReadRawAsync(owner)` → deserialize → take **only `Body`** from the caller (keep the file's Title/Tags/Type/CreatedAt, set `UpdatedAt = now`) → `ReplaceIfUnchangedAsync`, retried up to 3 times. So:
   - (i) a trashed, archived or purged note is never recreated;
   - (ii) metadata changes from an Edit user are never persisted (Core throws `NoteAccessDeniedException` only for a Type change; differing Title/Tags are ignored, not thrown on — see Open questions);
   - (iii) the owner's concurrent rename and the auto-tagger's tags survive a recipient's save.

   Rejected: throwing on any Title/Tags difference — a concurrent owner rename would make every recipient autosave fail until reload.
4. **Owner autosave now requires the live file to exist** (resolver step a). Before, an autosave after the note was trashed in another tab silently recreated a live copy next to the trash copy. Now it throws `NoteNotFoundException`. Deliberate behaviour change; it also stops a recipient whose share was revoked from forking the note into their own folder.
5. **Trash, Restore and Purge stay own-folder only**, unchanged (already no-ops for non-owners). Shares are hidden while trashed with no extra state: the index row is gone so listing drops it, and the live file is gone so reads 404. `RestoreAsync` reindexes and the shares reappear. `PurgeAsync` also deletes the share rows.
6. **Listing and search:** one SQL query over `NoteIndexEntries`: `e.UserId == me OR EXISTS(NoteShares …)`, plus a `Scope` for "Shared with Me". Each summary carries the owner's display name and permission (incoming) or a share count (outgoing). Tag suggestions and distinct tags stay own-only, because recipients can't edit tags.
7. **Images:** a new note-scoped route, `GET /notes/{noteId:guid}/images/{fileName}`, used only when the viewer is not the owner. Owners keep `/images/{fileName}` unchanged.
   - The route checks access to the note, then serves from the **owner's** folder.
   - **Read** viewers additionally need the note's raw text to mention the file name.
   - Owner and Edit skip the mention check. An Edit user can put any name into the body anyway, and skipping it avoids a broken image right after a paste, before the debounced autosave.
   - **Uploads by Edit recipients go to the owner's folder.** Rejected: recipient's folder (owner couldn't see the image and the recipient's cleanup would delete it). Also rejected: disabling paste (surprising, no safety gain).
8. **PDF export:** any access level may export (export is a read). Images come through the same access-checked `OpenImageAsync`.
9. **Service split:** extend `INoteService` for note reads and writes (needs the resolver). Add a new implicit-user `INoteSharingService` for managing grants: list, add, change, remove, leave, recipient search. Both registered in `AddJNotesCore`.

### B. Data model and migration

10. **New `src/JNotes.Core/Sharing/NoteSharePermission.cs`:** `public enum NoteSharePermission { Read, Edit }`, stored as the enum's name.
11. **New `src/JNotes.Core/Data/NoteShare.cs`:** `required Guid NoteId`, `required Guid SharedWithUserId`, `required Guid OwnerUserId`, `required NoteSharePermission Permission`, `required DateTimeOffset CreatedAt`. Doc comment: NOT derived (like `NoteAutoTagAttempt`); no FK to `NoteIndexEntries` because the index is truncatable.
12. **`JNotesDbContext.cs`:**
    - Add `public DbSet<NoteShare> NoteShares => Set<NoteShare>();` with a doc comment.
    - In `OnModelCreating`:
      - `entity.ToTable("NoteShares", t => t.HasCheckConstraint("CK_NoteShares_NotSelf", "\"OwnerUserId\" <> \"SharedWithUserId\""))`
      - `HasKey(e => new { e.NoteId, e.SharedWithUserId })` (resolver lookup; guarantees one owner per recipient).
      - `HasIndex(e => new { e.OwnerUserId, e.NoteId })` for the owner's list, purge and count.
      - `HasIndex(e => e.SharedWithUserId)` for incoming shares.
      - `Property(e => e.Permission).HasConversion<string>()`.
      - Two FKs: `HasOne<UserAccount>().WithMany().HasForeignKey(e => e.OwnerUserId).OnDelete(DeleteBehavior.Cascade)` and the same for `SharedWithUserId`. PostgreSQL allows multiple cascade paths.
13. **Migration:** `dotnet tool restore`, then `dotnet ef migrations add AddNoteShares --project src/JNotes.Core --startup-project src/JNotes.Core -o Data/Migrations`. Review the generated `Up` for the table, PK, 2 indexes, 2 FKs with cascade, and the check constraint. `MigrateAsync` at startup applies it. **Checkpoint:** `dotnet build JNotes.slnx`.

### C. Core changes

14. **New types in `src/JNotes.Core/Sharing/`** (namespace `JNotes.Core.Sharing`):
    - `NoteAccessLevel { Owner, Edit, Read }`.
    - `sealed record NoteAccess(NoteAccessLevel Level, Guid OwnerUserId, string? OwnerDisplayName)` with `bool IsOwner => Level == NoteAccessLevel.Owner;` and `bool CanEditBody => Level != NoteAccessLevel.Read;`.
    - `sealed record NoteWithAccess(Models.Note Note, NoteAccess Access)`.
    - `sealed record NoteShareGrant(Guid OwnerUserId, NoteSharePermission Permission, string OwnerDisplayName)`.
    - `sealed record NoteShareEntry(Guid UserId, string Username, string DisplayName, NoteSharePermission Permission, DateTimeOffset CreatedAt)`.
    - `sealed record ShareRecipientCandidate(Guid UserId, string Username, string DisplayName)`.
    - `sealed record SharedNoteInfo(Guid OwnerUserId, string OwnerDisplayName, NoteSharePermission Permission)`.
    - `sealed class NoteAccessDeniedException(Guid noteId, string message) : Exception(message)` exposing `NoteId`.
15. **New `src/JNotes.Core/Users/UserDisplayName.cs`** (pure static, like `UserInitials`): `public static string Format(string? firstName, string? lastName, string username)`. Returns the trimmed "First Last" if either part is non-blank, otherwise `username`. Used for "Shared by" and in the share dialog.
16. **`INoteFileStore` + `FileSystemNoteFileStore`:** add `Task<bool> ExistsAsync(Guid userId, Guid noteId, CancellationToken ct = default)` — the live note file exists (does not look in trash). Implement with `File.Exists(await ResolvePathAsync(...))`.
17. **New `src/JNotes.Core/Sharing/INoteShareRepository.cs` + `PostgresNoteShareRepository.cs`:** singleton over `IDbContextFactory<JNotesDbContext>`, like `PostgresAutoTagStateRepository`, explicit ids.
    - `Task<NoteShareGrant?> FindAsync(Guid noteId, Guid sharedWithUserId, CancellationToken ct = default)`: join `Users` on `OwnerUserId`, format with `UserDisplayName`.
    - `Task<IReadOnlyList<NoteShareEntry>> ListForNoteAsync(Guid ownerUserId, Guid noteId, CancellationToken ct = default)`: join `Users` on `SharedWithUserId`, ordered by display name.
    - `Task UpsertAsync(Guid ownerUserId, Guid noteId, Guid sharedWithUserId, NoteSharePermission permission, DateTimeOffset now, CancellationToken ct = default)`: raw `ExecuteSqlAsync` `INSERT … ON CONFLICT ("NoteId","SharedWithUserId") DO UPDATE SET "Permission" = EXCLUDED."Permission" WHERE "NoteShares"."OwnerUserId" = EXCLUDED."OwnerUserId"`. Pass `permission.ToString()`.
    - `Task<bool> DeleteAsync(Guid ownerUserId, Guid noteId, Guid sharedWithUserId, CancellationToken ct = default)`.
    - `Task<bool> DeleteForRecipientAsync(Guid noteId, Guid sharedWithUserId, CancellationToken ct = default)` (Leave).
    - `Task DeleteAllForNoteAsync(Guid ownerUserId, Guid noteId, CancellationToken ct = default)` (Purge).
    - `Task<IReadOnlyList<ShareRecipientCandidate>> SearchCandidatesAsync(Guid ownerUserId, Guid noteId, string? term, int limit, CancellationToken ct = default)`: every user except the owner and existing recipients of the note. `ILIKE` over Username/FirstName/LastName using `SqlLikePattern.ContainsPattern` / `SqlLikePattern.EscapeString`. **Never match on or return Email.** Order by Username, clamp limit to 1..20.
18. **`NoteSearchQuery.cs`:** add `public enum NoteListScope { All, SharedWithMe }` (in `Notes/`) and a 4th positional parameter `NoteListScope Scope = NoteListScope.All`. Existing call sites use named or no arguments, so they still compile.
19. **`NoteSummary.cs`:** keep the positional constructor and add init-only properties so the single construction site stays simple: `public Guid OwnerUserId { get; init; }`, `public SharedNoteInfo? SharedWithMe { get; init; }` (null for own notes), `public int OutgoingShareCount { get; init; }`.
20. **`PostgresNoteIndexRepository.SearchAsync`:** replace `Where(e => e.UserId == userId)` with a scope switch:
    - `All`: `e.UserId == userId || db.NoteShares.Any(s => s.NoteId == e.Id && s.OwnerUserId == e.UserId && s.SharedWithUserId == userId)`.
    - `SharedWithMe`: only the `Any(...)` part.

    Requiring `s.OwnerUserId == e.UserId` means a stale or foreign row can never expose another folder's entry. Extend the anonymous projection with:
    - `e.UserId`
    - `OutgoingShareCount = e.UserId == userId ? db.NoteShares.Count(s => s.OwnerUserId == userId && s.NoteId == e.Id) : 0`
    - `Permission = db.NoteShares.Where(s => s.NoteId == e.Id && s.SharedWithUserId == userId).Select(s => (NoteSharePermission?)s.Permission).FirstOrDefault()`
    - `Owner = db.Users.Where(u => u.Id == e.UserId && e.UserId != userId).Select(u => new { u.Username, u.FirstName, u.LastName }).FirstOrDefault()`

    Map these to the new init properties in memory using `UserDisplayName.Format`. Type, Tag, text search and ordering unchanged. Leave `GetDistinctTagsAsync` and `SuggestTagsAsync` own-only, with a comment saying so. Update the `INoteIndexRepository.SearchAsync` doc.
21. **`INoteService` additions** (update XML docs of `GetAsync` and `AutosaveAsync` to describe owner-or-shared):
    - `Task<NoteWithAccess> GetWithAccessAsync(Guid id, CancellationToken ct = default);`
    - `Task<ImageContent?> OpenImageAsync(Guid noteId, string fileName, CancellationToken ct = default);` — null for an invalid or unmentioned name. Throws `NoteNotFoundException` when there's no access.
    - `Task<string> SaveImageAsync(Guid noteId, Stream content, CancellationToken ct = default);` — stores in the owner's folder. Throws `NoteAccessDeniedException` for Read.
22. **`NoteService.cs`:**
    - Constructor adds `INoteShareRepository shares`, `IImageStore imageStore`, `INoteMarkdownSerializer serializer` (all already registered).
    - `private async Task<NoteAccess> ResolveAccessAsync(Guid noteId, CancellationToken ct)` per decision 2. Owner case sets `OwnerDisplayName = null`.
    - `private static void RequireBodyEdit(NoteAccess a, Guid id)` throws `NoteAccessDeniedException(id, "This note is shared with you read-only.")`.
    - `GetAsync` resolves, then `_fileStore.ReadAsync(access.OwnerUserId, id)`. `GetWithAccessAsync` does the same and returns both.
    - `AutosaveAsync(note, scope)`:
      - Resolve and require body edit.
      - Owner: existing path, unchanged apart from passing the owner id into `WriteAndIndexAsync`/`IndexAsync`.
      - Edit: `var merged = await ReplaceBodyAsync(access.OwnerUserId, note.Id, current => { if (current.Type != note.Type) throw new NoteAccessDeniedException(...); current.Body = note.Body; })`. If `scope == Metadata`, also `IndexAsync(ownerId, merged, await GetLastWriteTimeUtcAsync(ownerId, id))`. Return `merged` (carries the owner's current Title/Tags; the editor adopts it).
    - `private async Task<Note> ReplaceBodyAsync(Guid ownerId, Guid noteId, Action<Note> apply, CancellationToken ct)`: loop 3 times over `raw = ReadRawAsync` (propagates `NoteNotFoundException`), `current = _serializer.Deserialize(raw)`, `apply(current)`, `current.UpdatedAt = DateTimeOffset.UtcNow`, `if (await _fileStore.ReplaceIfUnchangedAsync(ownerId, noteId, raw, current, ct)) return current;`. After 3 failures, throw `IOException("The note changed while saving — try again.")`.
    - Checklist methods: `ReadChecklistNoteAsync` resolves and requires body edit. Owner: existing flow. Edit: `ReplaceBodyAsync` applying the same `ChecklistMarkdownParser` call, with the Checklist type guard inside `apply`.
    - Change `WriteAndIndexAsync` and `IndexAsync` to take `Guid ownerId`. `IndexAsync` sets `UserId = ownerId` and `FilePath = $"{ownerId}/{note.Id}.md"`. `CreateAsync` and `RestoreAsync` pass `me`.
    - `TrashAsync`/`ListTrashAsync`/`RestoreAsync`: unchanged, own-folder. Add a comment that a recipient's id is a natural no-op.
    - `PurgeAsync`: after `_fileStore.PurgeAsync(me, id)`, call `await _shares.DeleteAllForNoteAsync(me, id)`. Order is file, then rows; an orphan row from a crash is invisible and harmless.
    - `OpenImageAsync`: return null unless `ImageFormat.IsStoredFileName(fileName)`; resolve access; if Level is Read: `raw = ReadRawAsync(owner, id)` and return null unless `ImageFormat.FindStoredFileNames(raw).Contains(fileName, StringComparer.OrdinalIgnoreCase)`; return `_imageStore.OpenReadAsync(owner, fileName)`.
    - `SaveImageAsync`: resolve, require body edit, then `_imageStore.SaveAsync(owner, content)`.
23. **New `src/JNotes.Core/Sharing/INoteSharingService.cs` + `NoteSharingService.cs`** (scoped, implicit `ICurrentUserContext`; injects `INoteFileStore`, `INoteShareRepository`, `IUserRepository`). Private `RequireOwnerAsync(noteId)`: if `!ExistsAsync(me, noteId)`, throw `NoteAccessDeniedException("Only the owner can share this note.")` when a share row for `(noteId, me)` exists, otherwise `NoteNotFoundException`. Methods:
    - `Task<IReadOnlyList<NoteShareEntry>> ListSharesAsync(Guid noteId, CancellationToken ct = default)` — owner only.
    - `Task<IReadOnlyList<ShareRecipientCandidate>> SearchRecipientsAsync(Guid noteId, string? term, int limit = 10, CancellationToken ct = default)` — owner only.
    - `Task ShareAsync(Guid noteId, Guid recipientUserId, NoteSharePermission permission, CancellationToken ct = default)` — owner only. Recipient must differ from `me` (`InvalidOperationException("You can't share a note with yourself.")`) and must exist via `IUserRepository.GetUserByIdAsync` (`InvalidOperationException("That user no longer exists.")`). Upserts, so it is also used to change a permission.
    - `Task UnshareAsync(Guid noteId, Guid recipientUserId, CancellationToken ct = default)` — owner only.
    - `Task LeaveAsync(Guid noteId, CancellationToken ct = default)` — `DeleteForRecipientAsync(noteId, me)` with no existence check, so leaving works even while the note is trashed.
24. **`ServiceCollectionExtensions.AddJNotesCore`:** `services.AddSingleton<INoteShareRepository, PostgresNoteShareRepository>();` with comment "not derived — see NoteShare", and `services.AddScoped<INoteSharingService, NoteSharingService>();`. Add `INoteSharingService` to the class doc's list of interfaces Web consumes.
25. **`src/JNotes.Core/Images/ImageLinks.cs`:** add `public static string NoteScopedPrefix(Guid noteId) => $"/notes/{noteId}/{FolderName}/";` and `public static string? ToNoteScopedServedUrl(string markdownUrl, Guid noteId) => ToStoredFileName(markdownUrl) is { } f ? NoteScopedPrefix(noteId) + f : null;`.
26. **`IMarkdownRenderer` + `MarkdownRenderer`:** add the overload `string RenderToHtml(string markdown, Guid noteScopedImagesFor)`:
    - Add `private static readonly MarkdownPipeline BasePipeline = CreateBaseBuilder().Build();`.
    - Parse `Markdown.Parse(markdown, BasePipeline)`.
    - For each `document.Descendants<LinkInline>()` where `IsImage` and `ToNoteScopedServedUrl(...)` is non-null, rewrite `Url`.
    - Return `document.ToHtml(BasePipeline)` (same API `PdfMarkdownRenderer` uses).

    The existing single-argument overload is unchanged.
27. **`NoteExportService`:** replace `images.OpenAsync(imageName, ct)` with `notes.OpenImageAsync(noteId, imageName, ct)` and drop the `IImageService` constructor parameter. Update the comment: `GetAsync` now resolves owner-or-shared, and any access level may export.
28. **`UserManagementService`:** no code change. Share rows cascade when either user's row is deleted. Add a comment in `DeleteUserAsync`: archive happens first, and the cascade removes the user's shares both as owner and as recipient. Update the `IUserRepository.DeleteUserAsync` doc to mention the NoteShares cascade.

**Checkpoint:** `dotnet build JNotes.slnx`, then `dotnet test` (existing tests must still pass).

### D. Web changes

29. **`Components/_Imports.razor`:** add `@using JNotes.Core.Sharing`.
30. **`Program.cs`:** after the `/images/{fileName}` mapping, add `app.MapGet("/notes/{noteId:guid}/images/{fileName}", async (Guid noteId, string fileName, INoteService notes, HttpContext context, CancellationToken ct) => …).RequireAuthorization();`.
    - Catch `NoteNotFoundException` → `Results.NotFound()`. Null result → `NotFound`.
    - Headers: `CacheControl = "private, no-cache"` (so a revoked share takes effect; decision 7), `XContentTypeOptions = "nosniff"`.
    - Return `Results.Stream(image.Stream, image.ContentType, entityTag: new EntityTagHeaderValue($"\"{fileName}\""))` so a revalidation is a cheap 304 after the access check. **UNVERIFIED:** the `entityTag` parameter name on `Results.Stream`; if it doesn't match, drop the ETag.
    - Runs in a normal request scope, so the `ICurrentUserContext` gotcha doesn't apply. The comment should mirror the `/images` one.
31. **`Components/Notes/MarkdownPreview.razor`:** add `[Parameter] public Guid? ImagesNoteId { get; set; }`. In `OnParametersSet`: `_html = ImagesNoteId is { } id ? MarkdownRenderer.RenderToHtml(Markdown, id) : MarkdownRenderer.RenderToHtml(Markdown);`.
32. **`Components/Notes/TextNoteEditor.razor`:**
    - Add `[Parameter] public Guid? ImagesNoteId { get; set; }` and pass it to `MarkdownPreview`.
    - Add `[Parameter] public Func<Stream, CancellationToken, Task<string>>? SaveImage { get; set; }`. In `UploadAndInsertAsync`: `var storedFileName = SaveImage is null ? await ImageService.SaveAsync(stream) : await SaveImage(stream, CancellationToken.None);`.
    - Add `NoteAccessDeniedException or NoteNotFoundException` to the caught exceptions and show `ex.Message` in `_imageError`.
33. **`Components/Notes/NoteListItem.razor`:** after the title's type chip:
    - `@if (Note.SharedWithMe is { } s)`: `<MudChip T="string" Size="Size.Small" Variant="Variant.Outlined" Color="Color.Info" Icon="@Icons.Material.Outlined.People">Shared by @s.OwnerDisplayName · @(s.Permission == NoteSharePermission.Edit ? "Can edit" : "View only")</MudChip>`.
    - `else if (Note.OutgoingShareCount > 0)`: `<MudChip … Icon="@Icons.Material.Outlined.People">Shared · @Note.OutgoingShareCount</MudChip>`.
    - Check that the `Icon` parameter and icon name exist in MudBlazor.xml. Put any spacing in `wwwroot/css/note-list.css` (native nesting), not inline.
34. **New `Components/Notes/NoteSharePanel.razor`** (injects nothing; the content of the share dialog). Parameters:
    - `[Parameter, EditorRequired] IReadOnlyList<NoteShareEntry> Shares`
    - `[Parameter, EditorRequired] Func<string?, CancellationToken, Task<IReadOnlyList<ShareRecipientCandidate>>> SearchRecipients`
    - `[Parameter] EventCallback<(Guid UserId, NoteSharePermission Permission)> OnShare`
    - `[Parameter] EventCallback<(Guid UserId, NoteSharePermission Permission)> OnPermissionChanged`
    - `[Parameter] EventCallback<Guid> OnRemove`
    - `[Parameter] bool Disabled`
    - `[Parameter] int Version` — the page bumps it after every mutation and uses it in each row's `@key` (MudSelect keeps its own state after a refused change; same gotcha as `_listVersion`).

    UI:
    - Add row: `MudAutocomplete<ShareRecipientCandidate>` (`SearchFunc`, `ToStringFunc = c => $"{c.DisplayName} ({c.Username})"`, `DebounceInterval="250"`, `MinCharacters="0"`; verify names in MudBlazor.xml), a `MudSelect<NoteSharePermission>` defaulting to Read, and an "Add" `MudButton`.
    - Below it, one row per share: name and username, a `MudSelect` for the permission, a remove `MudIconButton`.
    - Empty state: "Not shared with anyone yet."
35. **`Components/Pages/NoteView.razor`:** also `@inject INoteSharingService SharingService`.
    - `OnParametersSetAsync`: `var r = await NoteService.GetWithAccessAsync(Id); _note = r.Note; _access = r.Access;`. If `_access.IsOwner`, also `_shares = await SharingService.ListSharesAsync(Id)`.
    - Buttons:
      - **Owner:** Edit, **Share** (opens dialog; shows "Shared with N" when N > 0), Export PDF, Delete.
      - **Edit recipient:** Edit, Export PDF, **Leave**.
      - **Read recipient:** Export PDF, **Leave**.
    - In `.jn-meta`, recipients see a chip "Shared by @_access.OwnerDisplayName · Can edit/View only".
    - Checklist: `ReadOnly="@(!_access.CanEditBody)"` on `ChecklistPanel`.
    - Text: `<MarkdownPreview Markdown="@_note.Body" ImagesNoteId="@(_access.IsOwner ? null : _note.Id)" />`.
    - Leave: confirm with `DialogService.ShowMessageBoxAsync("Leave \"{name}\"?", "You'll lose access to this note. The owner can share it with you again.", yesText: "Leave", cancelText: "Cancel")`, then `SharingService.LeaveAsync(Id)`, then `NavigateTo("/shared")`.
    - Share dialog: an inline `<MudDialog Visible="_shareOpen" VisibleChanged=… Options=…>` (Settings pattern), rendered **only inside `@if (_access.IsOwner)`** so a recipient's circuit never gets its handler ids. Contains `NoteSharePanel`. Handlers call `SharingService.ShareAsync`/`UnshareAsync`, reload `_shares`, bump `_sharesVersion`, and show errors (`InvalidOperationException`, `NoteAccessDeniedException`, `NoteNotFoundException`) in a `MudAlert` inside the dialog. `SearchRecipients` is `(term, ct) => SharingService.SearchRecipientsAsync(Id, term, 10, ct)`.
    - In `MutateChecklistAsync`, handle `NoteNotFoundException` and `NoteAccessDeniedException` with "Your access to this note has changed — reload the page."
36. **`Components/Pages/NoteEditor.razor`** (existing notes only; drafts unchanged):
    - `OnParametersSetAsync`: `GetWithAccessAsync`. If `Access.Level == Read`, `NavigationManager.NavigateTo($"/notes/{Id}", replace: true)` and return. Keep `_access`.
    - When `!_access.IsOwner`, render the title as `MudText Typo.h6` and tags as outlined chips (same markup as NoteView) instead of `MudTextField`/`TagInput`. `TagInput` has no Disabled parameter, and not rendering it means `OnMetadataEdited` can't fire. Add a caption: "Shared by X — you can edit the body; only the owner can change the title or tags."
    - Hide the Delete button when not the owner.
    - Pass to `TextNoteEditor`: `ImagesNoteId="@(_access.IsOwner ? null : _note.Id)"` and `SaveImage="@(_access.IsOwner ? null : (s, ct) => NoteService.SaveImageAsync(_note.Id, s, ct))"`.
    - `SaveNowAsync`: add `catch (Exception ex) when (ex is NoteNotFoundException or NoteAccessDeniedException)` before the generic catch. Set `_accessLost = true`, set `_saveError = "This note is no longer available to you (unshared, made read-only, or deleted). Your latest changes were NOT saved — copy them before leaving."`, and cancel both debouncers.
    - `OnContentEdited` and `OnMetadataEdited` do nothing when `_accessLost`. `DisposeAsync` skips the flush when `_accessLost`.
    - After a successful save, `_note = await NoteService.AutosaveAsync(...)` already adopts the owner's current title and tags for recipients.
37. **New `Components/Pages/SharedWithMe.razor`:** `@page "/shared"`, `@attribute [Authorize]`, `@inject INoteService NoteService`, `@inject IAppTimeZoneProvider TimeZoneProvider`. `<PageTitle>Shared with Me</PageTitle>`, a heading, and `<NoteGroupList Groups="_groups" EmptyText="Nothing has been shared with you yet." />`. In `OnInitializedAsync` (first lifecycle method, per the gotcha): `ListAsync(new NoteSearchQuery(Scope: NoteListScope.SharedWithMe))`, then `NoteGrouping.GroupByRelativeDate(notes, DateTimeOffset.UtcNow, TimeZoneProvider.TimeZone)`. Thin routed page owns the call; components inject nothing. (Verify `NoteGroupList` supports an `EmptyText` parameter; add one if not.)
38. **`Components/Layout/NavMenu.razor`:** after All Notes, add `<MudNavLink Href="/shared" Icon="@Icons.Material.Outlined.People">Shared with Me</MudNavLink>`, before the `MudDivider`. Order: Search, New Note, All Notes, Shared with Me, divider, Trash.

**Checkpoint:** `dotnet build JNotes.slnx`, `dotnet format JNotes.slnx`, `dotnet format JNotes.slnx --verify-no-changes`.

### E. Deletion and trash semantics (for docs and tests)

- **Owner trashes a note:** the file moves and the index row is deleted. Recipients lose it from lists and search; reads, image requests and PDF export return 404. Share rows are kept. Recipient writes fail with `NoteNotFoundException` (CAS finds no file) and never recreate the file.
- **Owner restores it:** reindexed; recipients see it again with the same permissions.
- **Owner purges it:** file deleted, then `DeleteAllForNoteAsync` removes the share rows.
- **Recipient calls Trash/Restore/Purge on the id:** a no-op (own folder only). The UI never offers these.
- **Owner's account is deleted:** folder archived, then row deleted, which cascade-deletes the owner's `NoteShares`. The owner's orphaned `NoteIndexEntries` rows remain (existing behaviour) but are no longer reachable through any share.
- **Recipient's account is deleted:** the cascade removes their grants; the owners' share counts drop.
- **File removed outside the app:** the reconciler drops the index row and the share row lingers invisibly. If the file comes back, the share applies again. Consistent with "shares persist".

### F. Security considerations

- All checks are in Core: `ResolveAccessAsync` for note operations and `RequireOwnerAsync` for share management. UI gating (buttons, `@if (_access.IsOwner)` around the dialog) is secondary.
- Own-folder guarantee preserved: an unshared foreign id fails both own-folder existence and share lookup, so it is a `NoteNotFoundException`. The list query additionally requires `s.OwnerUserId == e.UserId`.
- No `userId` in any URL. The note-scoped image route is re-authorised on every request (`no-cache` + ETag), and names still pass through `ImageFormat.IsStoredFileName` (the path-traversal guard).
- The recipient picker exposes username and first/last name of every account to every signed-in user. This is by requirement; document it in the README privacy note. Email is never returned or searched.
- Uploads still ride the circuit, so no new CSRF surface. The note-image route is a GET that only reads.
- **Revocation / revalidation:** permissions are not in cookie claims and not cached. Every Core call re-reads `NoteShares`, so an unshare, downgrade or leave takes effect on the recipient's next action, with the editor showing the "no longer available" error. Role changes are irrelevant to sharing. A deleted user is handled by the existing `CookiePrincipalValidator` and `JNotesRevalidatingAuthenticationStateProvider`, and their grants cascade away.

### G. Docs updates (same change)

39. **CLAUDE.md:**
    - **Architecture:** add `INoteSharingService` to the list of interfaces Web consumes. Add `NoteShares` to the "non-derived" statement next to `NoteAutoTagAttempts`.
    - **New section "## Note sharing"** covering: the model (`NoteShares`, PK `(NoteId, SharedWithUserId)`, both FKs cascade, check constraint, why not front matter); access resolution order (own live file → share row → 404), re-resolved on every call; the permission matrix; Edit writes are a body-only CAS merge through `ReplaceIfUnchangedAsync` (Title/Tags/Type from disk; Type change throws) and never create files; owner autosave now requires the live file (behaviour change); trash/restore/purge/user-deletion semantics (section E); listing and search via the `OR EXISTS` join, `NoteListScope`, the `NoteSummary` init properties; tag suggestions stay own-only; `ContentOnly` staleness applies to recipients' edits too; last-writer-wins between owner and recipient; auto-tagger runs per owner and a recipient's merge keeps auto-tags.
    - **Images:** note-scoped route, Read mention check, Owner/Edit skip and why, uploads by Edit recipients go to the owner's folder, why cleanup is unaffected.
    - **PDF export:** any access level; images via `OpenImageAsync`.
    - **Sidebar:** new order; "Shared with Me" is a plain `MudNavLink`.
    - **Notes UI:** NoteView buttons per level, Leave, owner-only inline share dialog with `NoteSharePanel` (injects nothing, `Version` `@key`), NoteEditor recipient mode, `_accessLost`.
    - **Tests:** the `NoteSharingTests` fixture helpers.
40. **README.md:**
    - **Features:** add a "Note sharing (Read/Edit)" bullet.
    - **New "## Sharing notes" section** (after Authentication): how to share, what each permission allows, Leave, trash/purge behaviour, that files stay in the owner's folder (on-disk layout unchanged; grants are in PostgreSQL and lost with `down -v`), and a privacy note that all users can see each other's usernames and names in the picker.
    - **Note file format:** no change; state that sharing adds nothing to front matter.

## Tests to add or update

All in `tests/JNotes.Tests`. They need the standing `jnotes-debug-db` at `localhost:5432`; do not recreate or stop it.

**Unit tests**
- New `Users/UserDisplayNameTests`: first and last; first only; last only; blanks/whitespace fall back to username; trimming.
- `Images/ImageLinksTests`: `NoteScopedPrefix` format; `ToNoteScopedServedUrl` rewrites only `images/<valid name>` and returns null for external URLs, `../`, uppercase or invalid names.
- `Serialization/MarkdownRendererTests`: note-scoped overload emits `/notes/{id}/images/<name>`; external images untouched; mermaid `</pre><script>` escaping regression still holds on the new overload; single-argument overload still emits `/images/<name>`.

**Fixture changes**
- `CoreServicesFixture`: add `public INoteSharingService NoteSharing`, `public INoteShareRepository ShareRepository`, `public IUserManagementService UserManagement` (resolved from `_scope`). Add `public async Task<Guid> CreateUserAsync(string username, UserRole role = UserRole.Member)` and `public void ActAs(Guid userId)`.

**New `Integration/NoteSharingTests`** (integration, one test per bullet)
1. An unshared foreign note is still not found as B for `GetAsync`, `GetWithAccessAsync`, `OpenImageAsync` and `AutosaveAsync`. No `{B}/{id}.md` is created.
2. Read share: `GetWithAccessAsync` returns Level Read, the owner id and "user-a" as display name.
3. A Read recipient's `AutosaveAsync` (both scopes), `SetChecklistItemCheckedAsync`, `AddChecklistItemAsync` and `SaveImageAsync` throw `NoteAccessDeniedException`; the owner's file bytes are unchanged.
4. An Edit recipient's ContentOnly autosave writes A's file, creates nothing under B, and leaves A's index row (Body, UpdatedAt) unchanged.
5. An Edit recipient's Metadata autosave with changed Title and Tags: disk keeps A's title and tags, body is updated, returned note has A's title, a search as A finds the new body text, and B's `SuggestTagsAsync` stays empty.
6. An Edit recipient changing the Type throws `NoteAccessDeniedException`.
7. An Edit recipient can toggle and add checklist items; the file has the checked item sunk to the bottom.
8. A recipient calling `TrashAsync`, `RestoreAsync` or `PurgeAsync` on A's id is a no-op: A's file and index row still exist.
9. A recipient calling `ShareAsync`, `ListSharesAsync` or `SearchRecipientsAsync` gets `NoteAccessDeniedException`.
10. Sharing with yourself or an unknown user id throws `InvalidOperationException`.
11. Sharing twice updates the permission and leaves a single row.
12. `UnshareAsync` takes effect on B's next call: `GetAsync` not found, `AutosaveAsync` not found, no file under B.
13. `LeaveAsync` removes only B's row: C still has access and A is unaffected.
14. Trash hides the note from B's `ListAsync` and `GetAsync`. B's autosave then throws not found and does not recreate `{A}/{id}.md`. Restore brings it back with the same permission. Purge empties `ShareRepository.ListForNoteAsync`.
15. `ListAsync(All)` as B merges B's own notes and A's shared note, ordered by `UpdatedAt` descending across owners, with `SharedWithMe` filled in. `ListAsync` as A shows `OutgoingShareCount == 2` after sharing with B and C, and `SharedWithMe == null`.
16. `ListAsync(Scope: SharedWithMe)` returns only incoming notes.
17. Search as B matches the shared note by title, by body (after a Metadata save) and by tag. A tag filter works on shared notes.
18. Deleting recipient B through `UserManagement.DeleteUserAsync(B, A)` removes B's shares, and A's count drops.
19. Deleting owner D (created in the test) removes D's shares, and D's note disappears from B's list.
20. `OpenImageAsync`: owner and Edit recipient get the image; a Read recipient gets it only if the note mentions it (an image in A's folder not mentioned → null); invalid name → null; no access → `NoteNotFoundException`.
21. `SaveImageAsync` by an Edit recipient stores under `{A}/images/`. After the body references it, `IImageCleanupService.CleanUpUserAsync(A, TimeSpan.Zero)` keeps it and `CleanUpUserAsync(B, …)` never sees it.
22. `NoteExportService.ExportPdfAsync` as a Read recipient succeeds and includes the image asset from A's folder (`RecordingPdfRenderer`). An unshared note is still `NoteNotFoundException` (existing test kept).
23. Owner `AutosaveAsync` after `TrashAsync` throws `NoteNotFoundException` and does not recreate a live file (the behaviour change).
24. CAS conflict: hold `{A}/{id}.md` open with `FileShare.None` in the test; an Edit recipient's autosave throws `IOException` and the file is unchanged.

**New `Integration/NoteShareRepositoryTests`**
- The check constraint rejects a self-share (raw insert through the repository with owner == recipient throws `PostgresException`).
- Both FKs cascade.
- `SearchCandidatesAsync` excludes the owner and existing recipients, escapes `%`/`_`, and never matches on email (a user whose only match is their email isn't returned).

No Razor component tests (no bUnit); UI is verified manually below.

## Manual verification (for the tester)

Build and test from the repo root:
```
dotnet build JNotes.slnx
dotnet format JNotes.slnx --verify-no-changes
dotnet test JNotes.slnx
dotnet test tests/JNotes.Tests --filter "FullyQualifiedName~NoteSharing"
```

Run against a **throwaway** database and folder. Never use the `jnotes` DB or `App_Data/notes`. Leave Ollama unset. PowerShell, no launch profile (a launch profile's env would override these):
```
$env:ASPNETCORE_ENVIRONMENT="Development"; $env:ASPNETCORE_URLS="http://localhost:5299"
$env:POSTGRES_HOST="localhost"; $env:POSTGRES_PORT="5432"; $env:POSTGRES_DB="jnotes_sharing_manual"
$env:POSTGRES_USER="jnotes"; $env:POSTGRES_PASSWORD="jnotes"
$env:NOTES_PATH="C:\Users\Joel\AppData\Local\Temp\claude\C--Data-Code-JNotes\c5b13d3c-59f0-4e51-a637-1d30b63de510\scratchpad\share-notes"
$env:DATA_PROTECTION_KEYS_PATH="C:\Users\Joel\AppData\Local\Temp\claude\C--Data-Code-JNotes\c5b13d3c-59f0-4e51-a637-1d30b63de510\scratchpad\share-keys"
$env:OLLAMA_URL=""; $env:GOTENBERG_URL="http://localhost:3000"   # uses the standing jnotes-debug-pdf if running; else set ""
dotnet run --project src/JNotes.Web --no-launch-profile
```
Afterwards, drop `jnotes_sharing_manual` if you like, but never touch the containers.

**Setup:** open `http://localhost:5299` and go through /setup to create admin `alice`. Under Administration, add Members `bob` and `carol` (with passwords) and Admin `dave`. Use a normal window for Alice and a private window for Bob.

1. **Alice creates notes.** Create text note "Plan" with tag `alpha`, some body text, and a pasted screenshot. Create checklist "Groceries" with 3 items. Expected: both appear under All Notes with no share chip.
2. **Alice shares with Read.** Open "Plan" → Share. Type "bo" and expect `bob (bob)` suggested, alice not listed. Add as View only. Expected: dialog lists bob; header shows "Shared with 1"; the All Notes card shows "Shared · 1".
3. **Bob as Read recipient.**
   - Sidebar order: Search, New Note, All Notes, Shared with Me, divider, Trash.
   - All Notes and Shared with Me both show "Plan" with "Shared by alice · View only", grouped under Today.
   - Searching "Plan" and "alpha" finds it.
   - Opening it shows the image. In devtools the request is `/notes/{id}/images/<name>.png` with 200.
   - No Edit, Delete or Share button; Leave and Export PDF are shown.
   - Visiting `/notes/{id}/edit` redirects to the view.
   - Directly opening `/images/<name>.png` returns 404.
4. **Read checklist.** Alice shares "Groceries" with Bob as View only. Expected: Bob's checkboxes can't be toggled.
5. **Upgrade to Edit.**
   - Alice changes both shares to Can edit; Bob reloads. The Edit button appears.
   - In the editor, title and tags are read-only text and there is no Delete button.
   - Bob types body text and sees "All changes saved". Alice reloads and sees the text.
   - Bob pastes an image. It renders in his preview, and the file appears under `share-notes\<alice id>\images\` (not Bob's folder).
   - Bob toggles a checklist item in the view; Alice sees it moved to the bottom after reloading.
6. **Revoke while editing.** With Bob's editor open, Alice removes Bob in the Share dialog. Bob types. Expected: the "no longer available … NOT saved" error appears. Nothing is written under `share-notes\<bob id>\`, and Alice's file doesn't contain the new text.
7. **Leave.** Alice re-shares "Plan" with Bob (Read) and with carol. Bob clicks Leave and confirms. Expected: he lands on /shared and "Plan" is gone. Alice's card shows "Shared · 1"; Carol still sees it.
8. **Trash, restore, purge.**
   - Alice deletes "Plan". Carol's list no longer shows it, and Carol's open URL gives "couldn't be found".
   - Alice restores it from Trash. Carol sees it again, still View only.
   - Alice deletes it and purges it from Trash. It is gone for Carol.
9. **User deletion.**
   - Alice shares "Groceries" with Carol, then deletes Carol in Administration. Expected: no error, and Alice's share dialog no longer lists Carol.
   - Dave signs in, creates "Dave note" and shares it with Bob. Alice deletes Dave. Expected: Bob's lists no longer show "Dave note".
10. **Cross-user isolation.** Bob opens `/notes/<id of an unshared Alice note>`: "couldn't be found". `/notes/<that id>/images/<an Alice image>` returns 404. `/notes/<that id>/pdf` returns 404.
11. **PDF (only if Gotenberg is running).** Bob, as a recipient, clicks Export PDF on a shared note containing an image. The PDF opens in a new tab and includes the image.

## Risks and edge cases

- **Last-writer-wins between owner and recipient.** Owner and recipient editing the same body in open editors overwrite each other; the owner's `WriteAsync` sends its whole in-memory note. Same as two tabs today. The CAS only closes the read-to-write window for recipient writes.
- A recipient's CAS write can get an `IOException` while the owner's autosave or the auto-tagger holds the file. The user sees "Autosave failed" and the next keystroke retries. The auto-tagger records nothing and retries on its next pass.
- The owner autosave behaviour change (no resurrection of a trashed note) could surprise anyone relying on it. Covered by a test and documented.
- `FileSystemNoteFileStore.ResolveFolderAsync` creates `{ownerId}/` as a side effect. If a recipient reads in the brief window between an owner's folder being archived and the account row being deleted, an empty owner folder could be recreated. Harmless; mention in docs.
- The `OR EXISTS` filter may stop PostgreSQL using the `(UserId, UpdatedAt)` index for large collections. If profiling shows a problem, rewrite as `Concat` (UNION ALL) of the own and shared queries.
- For a recipient, `ContentOnly` staleness means edits to a shared note don't reorder it in either user's list and aren't searchable until a Metadata save or rescan.
- Stale images: a Read viewer's browser may still hold an image fetched before revocation. `no-cache` limits this to what was already displayed.
- MudBlazor 9 API names (`ToStringFunc`, `MudChip Icon`, `Icons.Material.Outlined.People`) must be checked in MudBlazor.xml. The `entityTag` parameter on `Results.Stream` is unverified.

## Open questions (defaults chosen; executor proceeds with these)

- **Edit recipient sends a changed Title or Tags?** Default: ignore them and take the values from disk; throw only on a Type change. Throwing would break recipients' autosave whenever the owner renames the note concurrently. Switch to throwing only if the user insists.
- **Nav label casing:** "Shared with Me" (title case with lowercase preposition).
- **Read recipients can export PDF:** yes (it is a read).

## Out of scope

- Sharing folders or tags, public or anonymous links, groups, or an admin toggle to disable sharing.
- Notifications, "Shared by me" pages, share expiry, real-time presence or locking.
- Any change to the note file format, `NoteIndexReconciler`, `ImageCleanupService` logic, `NoteAutoTagger`, the Dockerfile or docker-compose.
- `TagInput`, the Trash page, and the auth or revalidation wiring.
- Do not touch or recreate the `jnotes-debug-*` containers, and do not use the `jnotes` database or `App_Data/notes` for manual runs.
