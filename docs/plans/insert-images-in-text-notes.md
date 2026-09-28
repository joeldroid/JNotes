# Plan: Insert images into text notes (JNotes)

**Status:** Implemented and verified in a browser (button, paste, oversize/non-image errors, view page, `/images` headers, checklist editor has no Image button); 275 tests pass. Docs updated. Not yet committed.
**Scope:** `JNotes.Core` (image format/store/service, markdown pipeline), `JNotes.Web` (endpoint, toolbar, text editor, paste JS, CSS), tests, docs.
**Author context:** planned against commit `2687047` (`main`). Saved as `docs/plans/insert-images-in-text-notes.md`, matching the existing plans in this folder.

---

## Context
Text notes are markdown files at `{NotesFolder}/{userId}/{noteId}.md`, but there's no way to put an
image in one. Add an **Image** button and **paste-from-clipboard** to the *text-note* editor. Each
image is uploaded to `{NotesFolder}/{userId}/images/{file}` and a link is inserted at the cursor.

Decided with the user:
- Link is **relative**: `![name](images/<file>)`. Images sit next to the note `.md` files, so the raw
  folder still renders in Obsidian/VS Code/GitHub. The in-app renderer rewrites it to an
  authenticated URL for display.
- Insert via toolbar button **and** paste. **No drag-and-drop.**
- Only standard web-renderable formats: **PNG, JPEG, GIF, WebP**. SVG is deliberately excluded (a
  directly opened SVG can run script). Easy to widen later.
- Text notes only: the button/paste live in `TextNoteEditor`/`MarkdownToolbar`; checklist editor and
  view are untouched.

## Design
**Core (new, no ASP.NET dependency)**
- `Images/ImageFormat` (pure static, like `UserValidation`): `Detect(header bytes)` → extension +
  content type by **magic bytes** (never trust the client's name/MIME); `IsStoredFileName(name)`
  matches `^[0-9a-f]{32}\.(png|jpg|gif|webp)$` (also the path-traversal guard);
  `ImageLinks` helper holds the `images/` prefix and builds `![alt](images/file)` (alt = original
  filename stem, with `[ ] ( )` and line breaks stripped), so the inserter and renderer share one
  definition.
- `IImageStore` / `FileSystemImageStore` (`Storage/`), **explicit `Guid userId`** like
  `INoteFileStore`: `SaveAsync(userId, stream)` writes `{guid:N}.{ext}` under `{userId}/images/`
  (creates the folder; reads at most `MaxBytes`+1 → `ImageTooLargeException`; unrecognised header →
  `UnsupportedImageException`); `OpenReadAsync(userId, fileName)` → stream + content type, or `null`
  if the name is invalid/missing. `MaxBytes` = 10 MB (const).
- `IImageService` / `ImageService`: **implicit** `ICurrentUserContext` (same pattern as
  `NoteService`), wraps the store. `SaveAsync(stream)` returns the stored file name (as built, it takes no
  original name — the editor builds the link text itself via `ImageLinks.ToMarkdown`);
  `OpenAsync(fileName)`. Register both in `AddJNotesCore`.
- `MarkdownRenderer`: new `ImageLinkMarkdownExtension` (modelled on `MermaidMarkdownExtension`;
  `DocumentProcessed` walk over `LinkInline { IsImage: true }`). A URL that is exactly
  `images/<valid stored name>` becomes `/images/<name>`; anything else (external `https://…`,
  `images/../x`) is left untouched. Applies wherever `MarkdownPreview` renders (editor pane + view).
- Nothing else changes: `ListIdsAsync` is non-recursive `*.md`, so the reconciler ignores `images/`;
  `ArchiveUserFolderAsync` moves images with the rest of a deleted user's folder.

**Web**
- `Program.cs`: `app.MapGet("/images/{fileName}", …).RequireAuthorization()` → `IImageService.OpenAsync`;
  404 when null. Because the lookup is under the *caller's own* folder, another user's file name is a
  404 (per-user isolation without a userId in the URL). Headers: `Content-Type` from detection,
  `X-Content-Type-Options: nosniff`, `Cache-Control: private, max-age=31536000, immutable` (names are
  never reused). No filesystem API used in Web; the stream comes from Core (BannedSymbols stays happy).
- Upload goes **over the circuit** (no HTTP upload endpoint, so no antiforgery/CSRF surface):
  - **Button**: `MarkdownToolbar` gets an "Image" button, a `<label class="btn …">` wrapping a
    visually-hidden `InputFile` (`accept` = the four MIME types; `@key` bumped after each upload so
    re-picking the same file fires again). It raises `OnImageSelected(IBrowserFile)`; the toolbar
    holds no upload logic.
  - **Paste**: new `wwwroot/js/image-paste.js` `attach(textarea, dotNetRef)` listens for `paste`; if
    the clipboard has an `image/*` file it `preventDefault()`s (plain-text paste untouched) and calls
    `dotNetRef.invokeMethodAsync('OnImagePastedAsync', DotNet.createJSStreamReference(file), file.name)`
    (a stream reference, because a screenshot exceeds the 32 KB SignalR interop message limit).
    Returns a detach function.
  - `TextNoteEditor` owns both entry points and funnels them into one private
    `UploadAndInsertAsync(Stream, name)`: `ImageService.SaveAsync` → `insertAtCursor(textarea,
    ImageLinks.ToMarkdown(...))` via the existing `markdown-toolbar.js` (fires a real `input` event, so
    Body/autosave need no new plumbing). It attaches the paste listener in
    `OnAfterRenderAsync(firstRender)` using its **own** `_textareaRef` (avoids the parent-`@ref`
    gotcha in CLAUDE.md), detaches in `DisposeAsync` (catching `JSDisconnectedException`). While
    uploading it disables the button ("Uploading…"); errors (too large / unsupported type) show in a
    dismissible `alert alert-danger` above the editor. Insertion happens at the cursor position at
    *completion* time, same as the other toolbar buttons.
  - `ICurrentUserContext` gotcha: `TextNoteEditor` only renders under `NoteEditor`, which already
    resolves `NoteService` in `OnInitializedAsync`, so the scoped user context is built correctly
    before `ImageService` is first resolved.

## Files
- New: `src/JNotes.Core/Images/{ImageFormat,ImageLinks,IImageService,ImageService}.cs` (+ the two
  exceptions), `src/JNotes.Core/Storage/{IImageStore,FileSystemImageStore}.cs`,
  `src/JNotes.Core/Serialization/ImageLinkMarkdownExtension.cs`, `src/JNotes.Web/wwwroot/js/image-paste.js`
- Edit: `ServiceCollectionExtensions.cs` (DI), `MarkdownRenderer.cs` (add extension),
  `Program.cs` (endpoint), `Components/Notes/{MarkdownToolbar,TextNoteEditor}.razor`
- Docs: `CLAUDE.md` (Web's Core touchpoints list gains `IImageService`; new "Images" note under Notes
  UI/Storage: relative link + rewrite, magic-byte validation, SVG excluded, over-circuit upload,
  orphans), `README.md` (feature + `{userId}/images/` in the folder layout).

## Out of scope / known limitations
- **Orphaned images**: deleting a note or a link never deletes the image (no reference tracking; an
  image may also be copy-pasted into several notes). A later "unused images" cleanup could address it.
- No resizing/compression, no drag-and-drop, no SVG/AVIF/BMP.
- Uploads ride SignalR; fine at a 10 MB cap, but an HTTP endpoint would be the move if the cap grows.

## Verification
- **Unit/integration (xUnit, real Redis per existing fixture)**:
  - `ImageFormat`: detects each of the 4 formats from real header bytes; rejects text, SVG, empty and
    truncated input; `IsStoredFileName` rejects `../x.png`, wrong extension, wrong length, uppercase.
  - `ImageService` via `CoreServicesFixture`: save → file exists at `{userId}/images/{guid}.png`;
    user B `OpenAsync(userA's name)` → `null` (isolation); oversize → `ImageTooLargeException`;
    non-image → `UnsupportedImageException`; two saves get distinct names.
  - `MarkdownRendererTests`: `![a](images/<valid>.png)` → `src="/images/<valid>.png"`;
    `images/../x.png`, `https://…/x.png` and a non-image link are unchanged.
  - `dotnet test JNotes.slnx`, `dotnet format JNotes.slnx --verify-no-changes`.
- **Manual, in a browser** (no Razor component tests exist): `dotnet run --project src/JNotes.Web`,
  open a *text* note's editor. (1) Image button → choose a PNG → link inserted at the cursor, preview
  shows it, file is under `App_Data/.../{userId}/images/`. (2) Same file twice. (3) Paste a screenshot
  → inserted; paste plain text → normal. (4) Pick a `.txt`/`.svg` and an >10 MB file → error alert, no
  file written. (5) Reload the note view → image still renders (`/images/...` 200, cache headers).
  (6) Signed out or as another user → `/images/<name>` is 401/redirect / 404. (7) Checklist editor:
  no Image button. (8) Open the raw `.md` in another editor: `images/<file>` resolves.
