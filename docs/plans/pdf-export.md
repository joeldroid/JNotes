# Plan: Optional "Export PDF" for a single note, rendered by a Gotenberg sidecar

## Goal

`NoteView` (`/notes/{id}`) gets an **Export PDF** button. One click opens a new tab showing a light-themed PDF of the note: title, tags, type, last-updated time, then the body. Text notes include images and mermaid diagrams. Checklist notes show a static list of checkboxes. The PDF is always light, whatever theme the user has chosen. The feature is off unless `GOTENBERG_URL` is set. When it is unset, the button is not rendered and the `/notes/{id}/pdf` route is not mapped at all, so it returns the normal 404.

## Decisions (with rationale and rejected alternatives)

1. **Gotenberg Chromium HTML route (`POST /forms/chromium/convert/html`) with a self-contained document built server-side.** JNotes sends `index.html` plus flat sibling files: the images, the Roboto `.woff2` files, `mermaid.min.js` (only when needed) and `footer.html`. Per Gotenberg's docs, sibling files are referenced by bare file name (`<img src="abc….png">`).
   - *Rejected: the URL route pointing at an app page.* Gotenberg has no auth cookie, so this would need an anonymous or token page, which is a new auth surface. It would also need an app base URL that Gotenberg can reach (another env var). Our pages are `InteractiveServer` and would need a SignalR circuit inside headless Chromium. They would also render in the user's theme (dark by default), and `/images/*` would be 401.
2. **Images are sent as files, not `data:` URIs.** The PDF markdown pipeline rewrites `images/<stored name>` to `<stored name>`, and the export service streams each file from the current user's folder through `IImageService.OpenAsync`. That call is already per-user and validated by `ImageFormat.IsStoredFileName`. This avoids base64 inflation. **Any other image URL (external, `data:`, odd paths) becomes a plain link** (`LinkInline.IsImage = false`), so Chromium never fetches anything user-controlled. That closes the SSRF path through ordinary markdown.
3. **Mermaid diagrams are rendered inside Gotenberg's Chromium.** When the note has a mermaid block, the vendored `mermaid.min.js` is attached. An inline script renders every `pre.mermaid` with `theme: 'default'` (light) and `securityLevel: 'strict'`, the same parse-then-render approach as `mermaid-preview.js`, then sets `window.jnotesPdfReady = true`. Gotenberg waits on `waitForExpression=window.jnotesPdfReady === true`, and the script also awaits `document.fonts.ready`. An invalid diagram falls back to its source with a "Diagram error" caption, the same as in the app.
   - *Rejected: passing SVGs from the user's browser.* That would need JS interop from `NoteView`, SVGs larger than the 32 KB interop message limit, SVGs already rendered in the user's dark theme, and an export tied to a live circuit.
4. **The mermaid script and fonts are Web static assets, so Core reads them through a new interface, `IPdfExportAssetSource`.** It is declared in Core and implemented in Web (`IWebHostEnvironment.WebRootFileProvider`), the same "interface in Core, implementation plus registration in Web" pattern as `ICurrentUserContext`. `WebRootFileProvider` is not a banned symbol; `System.IO.File`/`Directory` are never used. The Gotenberg image does **not** ship Roboto (checked against its Dockerfile: it has Noto, Liberation, DejaVu and Carlito/Caladea), so embedding the three vendored `.woff2` files keeps the PDF looking like the app. A missing asset is skipped: fonts fall back, and diagrams show as source.
5. **The Gotenberg HTTP client lives in Core** (`GotenbergPdfRenderer`, a typed `HttpClient` through `IHttpClientFactory`). `HttpClient` is a BCL type, not a hosting-layer dependency, so it doesn't fall under the Web-implementation rule, and keeping it in Core means `JNotes.Tests` can test it with a fake `HttpMessageHandler`. Core gains a `Microsoft.Extensions.Http` package reference.
6. **Delivery is a minimal-API `GET /notes/{id:guid}/pdf`** in Web (`RequireAuthorization`). The button is a `MudButton Href=… Target="_blank"`. Blazor's link interception ignores anchors with a non-`_self` `target`, so the browser does a real HTTP GET with the auth cookie. The response is `Content-Disposition: inline; filename=…; filename*=…`, so it opens in the browser's PDF viewer in the new tab, the viewer's Save offers the title-based file name, and mobile browsers download it.
   - The `ICurrentUserContext` gotcha does not apply: the endpoint runs in a normal HTTP request scope where `HttpContext` is always present, and `NoteExportService` gets the current user through `INoteService`/`IImageService` in that scope. `NoteView` itself resolves nothing new that needs a user; it only reads `IOptions<PdfExportOptions>`.
   - `CookiePrincipalValidator` also runs on this request, so a deleted user's cookie is rejected.
   - *Rejected: a JS-interop blob download through `DotNetStreamReference`.* It works around the 32 KB limit, but adds a JS module and buffers the whole PDF through the circuit. A `window.open` after an `await` is popup-blocked, and the result is no simpler for the user.
7. **Security at the endpoint:**
   - **Ownership:** `INoteService.GetAsync` reads only the caller's folder, so another user's id throws `NoteNotFoundException` and the endpoint returns 404 through the existing `/not-found` status page.
   - **No SSRF from the app:** the Gotenberg URL comes only from admin configuration. Chromium is also restricted in compose with `--chromium-allow-list=^file:///tmp/.*` as defence in depth, because mermaid's DOMPurify can still pass an `<img>` inside a label.
   - **Filename:** sanitised in `PdfFileName`, then `ContentDispositionHeaderValue.SetHttpFileName` builds the ASCII fallback plus the RFC 5987 `filename*`.
   - **Timeouts:** `HttpClient.Timeout` is 60 s, longer than Gotenberg's own 30 s `API_TIMEOUT`, so Gotenberg usually answers 503 first.
   - **Gotenberg down, timed out, or returning an error:** Core throws `PdfRenderingException`. The endpoint returns **503 with a small self-contained HTML page** ("Couldn't create the PDF …"). It has a body, so `UseStatusCodePagesWithReExecute` does not replace it with "Not found". The details go to the log as a warning, not to the user.
8. **Config:** a single env var, **`GOTENBERG_URL`** (e.g. `http://gotenberg:3000`), read in `AddJNotesCore` into `JNotes.Core.Export.PdfExportOptions`, like `OidcOptions`. `IsEnabled` requires an absolute `http`/`https` URL. A set-but-invalid value leaves the feature off and logs a startup warning. Because the endpoint is mapped only when enabled and `NoteView` renders the button only when enabled, "unset" means "not visible and not reachable".
9. **Compose:** a `gotenberg` service under a `pdf` **profile**, with no published port, the `curl`-based healthcheck (curl ships in the Gotenberg image), and no `depends_on` from `jnotes`. The app service always passes `GOTENBERG_URL=${GOTENBERG_URL:-}`, which is empty (and so disabled) by default. Enabling it takes two `.env` lines: `COMPOSE_PROFILES=pdf` and `GOTENBERG_URL=http://gotenberg:3000`.
10. **Layout:** A4 (8.27 × 11.7 in), margins 0.6 in (0.8 in at the bottom for the footer), `printBackground=true`, and a "Page X of Y" footer (Gotenberg `footer.html` with the `pageNumber`/`totalPages` classes). Code wraps (`pre-wrap`) instead of scrolling. Headings avoid breaking after themselves. Rows, images, diagrams, blockquotes and short code blocks avoid breaking inside. Table headers repeat. Images are capped at 100% width and 24 cm height.
11. **Checklist in the PDF matches `ChecklistPanel`:** items only (prose lines are not shown, same as the panel), grouped by category heading, with CSS-drawn boxes. Checked boxes are filled with a ✓ and their text is struck through and muted.

## Context found

- `C:\Data\Code\JNotes\src\JNotes.Web\Components\Pages\NoteView.razor`: routed `[Authorize]` page. It injects `INoteService` and `IAppTimeZoneProvider`, loads in `OnParametersSetAsync`, and has the Edit/Delete `MudButton`s in `<MudStack Row="true" Spacing="2" Class="flex-shrink-0">`. The meta line uses `TimeZoneProvider.ToAppTimeZone(_note.UpdatedAt).DateTime.ToString("g")`.
- `C:\Data\Code\JNotes\src\JNotes.Web\Components\Notes\MarkdownPreview.razor`: `IMarkdownRenderer.RenderToHtml`, and imports `mermaid-preview.js` only if the HTML contains `<pre class="mermaid">`.
- `C:\Data\Code\JNotes\src\JNotes.Core\Serialization\MarkdownRenderer.cs`: a static pipeline of `UsePipeTables().DisableHtml().Use<MermaidMarkdownExtension>().Use<ImageLinkMarkdownExtension>()`.
- `C:\Data\Code\JNotes\src\JNotes.Core\Serialization\MermaidCodeBlockRenderer.cs`: emits `<pre class="mermaid">` with the source HTML-escaped.
- `C:\Data\Code\JNotes\src\JNotes.Core\Images\ImageLinks.cs` (`RelativePrefix`, `ToServedUrl`) and `ImageFormat.IsStoredFileName`.
- `C:\Data\Code\JNotes\src\JNotes.Core\Images\IImageService.cs`: `OpenAsync(fileName)` returns `ImageContent(Stream, ContentType)`, or null, for the current user.
- `C:\Data\Code\JNotes\src\JNotes.Core\Serialization\ChecklistMarkdownParser.cs`: `ParseSections(body)` returns `ChecklistSection(int? HeadingLineIndex, string? Title, IReadOnlyList<ChecklistItem> Items)`, where `ChecklistItem(LineIndex, Text, IsChecked)`.
- `C:\Data\Code\JNotes\src\JNotes.Web\wwwroot\js\mermaid-preview.js`: the parse, render and remove-temp-`d<id>`-element pattern to mirror.
- `C:\Data\Code\JNotes\src\JNotes.Web\wwwroot\css\markdown-preview.css` and `mermaid.css`: the look to mirror. `fonts.css` plus `wwwroot/lib/roboto/roboto-latin-{400,500,700}-normal.woff2`. Mermaid is at `wwwroot/lib/mermaid/dist/mermaid.min.js`.
- `C:\Data\Code\JNotes\src\JNotes.Web\Theme\JNotesTheme.cs`: light primary is `#2f6feb`; the other light colours are MudBlazor defaults.
- `C:\Data\Code\JNotes\src\JNotes.Core\ServiceCollectionExtensions.cs`: flat-key `services.Configure<OidcOptions>(…)` pattern. `C:\Data\Code\JNotes\src\JNotes.Core\Users\OidcOptions.cs` has an `IsConfigured` pattern. `Login.razor` injects `IOptions<OidcOptions>` with a local `@using Microsoft.Extensions.Options`.
- `C:\Data\Code\JNotes\src\JNotes.Web\Program.cs`: registers `ICurrentUserContext`. Minimal APIs are `/images/{fileName}` (`RequireAuthorization`, header style) and `/logout`. `UseStatusCodePagesWithReExecute("/not-found")` is in place. OIDC wiring is decided at startup from config.
- `C:\Data\Code\JNotes\src\JNotes.Web\BannedSymbols.txt`: bans `System.IO.File`/`Directory`, `DbContext`, `NpgsqlConnection`/`NpgsqlDataSource`.
- Core csproj references Markdig 1.3.2 and `Microsoft.Extensions.*` 10.0.12. It has **no** `Microsoft.Extensions.Http`.
- Markdig 1.3.2 API was checked against its XML docs: `LinkInline.IsImage` has a getter and setter; `Markdown.Parse(string, MarkdownPipeline, MarkdownParserContext)`, `Markdown.ToHtml(MarkdownDocument, MarkdownPipeline)` and `ContainerInline.AppendChild` exist.
- MudBlazor 9.10 `MudBaseButton` has `Href`, `Target` and `Rel`, checked in `MudBlazor.xml`.
- Tests: `C:\Data\Code\JNotes\tests\JNotes.Tests\Integration\CoreServicesFixture.cs` builds `AddJNotesCore` and swaps test doubles after it. `TestDoubles/` holds the stubs. xUnit, no bUnit.
- `C:\Data\Code\JNotes\docker-compose.yml`: heavily commented, OIDC vars commented as opt-in, postgres healthcheck style. The Dockerfile needs **no change**: the optional OIDC vars aren't declared there either.
- Gotenberg facts (from gotenberg.dev and its Dockerfile):
  - Route `/forms/chromium/convert/html`.
  - All files go in multipart field `files`: `index.html` is required, `footer.html` is optional, and assets are flat.
  - Form fields: `paperWidth`, `paperHeight`, `marginTop`/`Bottom`/`Left`/`Right` (inches by default), `printBackground`, `waitForExpression`.
  - Errors: 400, 409 or 503 (timeout).
  - Flags: `--api-timeout` (default 30 s), `--chromium-allow-list`/`--chromium-deny-list` (default deny `^file:(?!//\/tmp/).*`).
  - Health endpoint `/health`. `curl` is installed. Non-root user. Port 3000. No Roboto.

## Files to create or modify

**Create (Core)**
- `C:\Data\Code\JNotes\src\JNotes.Core\Export\PdfExportOptions.cs`
- `C:\Data\Code\JNotes\src\JNotes.Core\Export\IPdfExportAssetSource.cs` (plus a static `PdfExportAssets` with the path constants)
- `C:\Data\Code\JNotes\src\JNotes.Core\Export\IPdfRenderer.cs` (plus the `PdfRenderRequest` and `PdfRenderAsset` records)
- `C:\Data\Code\JNotes\src\JNotes.Core\Export\PdfRenderingException.cs`
- `C:\Data\Code\JNotes\src\JNotes.Core\Export\GotenbergPdfRenderer.cs`
- `C:\Data\Code\JNotes\src\JNotes.Core\Export\PdfFileName.cs`
- `C:\Data\Code\JNotes\src\JNotes.Core\Export\NotePdfDocumentBuilder.cs` (plus the `NotePdfDocument` record)
- `C:\Data\Code\JNotes\src\JNotes.Core\Export\note-pdf.css` (embedded resource)
- `C:\Data\Code\JNotes\src\JNotes.Core\Export\INoteExportService.cs` (plus the `NotePdf` record)
- `C:\Data\Code\JNotes\src\JNotes.Core\Export\NoteExportService.cs`
- `C:\Data\Code\JNotes\src\JNotes.Core\Serialization\PdfMarkdownRenderer.cs`

**Modify (Core)**
- `C:\Data\Code\JNotes\src\JNotes.Core\JNotes.Core.csproj`
- `C:\Data\Code\JNotes\src\JNotes.Core\ServiceCollectionExtensions.cs`
- `C:\Data\Code\JNotes\src\JNotes.Core\Serialization\MarkdownRenderer.cs`
- `C:\Data\Code\JNotes\src\JNotes.Core\Serialization\MermaidCodeBlockRenderer.cs` (add a public const)
- `C:\Data\Code\JNotes\src\JNotes.Core\Images\ImageLinks.cs`

**Create (Web)**
- `C:\Data\Code\JNotes\src\JNotes.Web\Export\WebRootPdfExportAssetSource.cs`
- `C:\Data\Code\JNotes\src\JNotes.Web\Export\NotePdfEndpoints.cs`

**Modify (Web)**
- `C:\Data\Code\JNotes\src\JNotes.Web\Program.cs`
- `C:\Data\Code\JNotes\src\JNotes.Web\Components\Pages\NoteView.razor`
- `C:\Data\Code\JNotes\src\JNotes.Web\Components\_Imports.razor`

**Tests**
- Create:
  - `tests\JNotes.Tests\Export\PdfFileNameTests.cs`
  - `tests\JNotes.Tests\Export\PdfExportOptionsTests.cs`
  - `tests\JNotes.Tests\Export\NotePdfDocumentBuilderTests.cs`
  - `tests\JNotes.Tests\Export\GotenbergPdfRendererTests.cs`
  - `tests\JNotes.Tests\Serialization\PdfMarkdownRendererTests.cs`
  - `tests\JNotes.Tests\Integration\NoteExportServiceTests.cs`
  - `tests\JNotes.Tests\TestDoubles\RecordingPdfRenderer.cs`
  - `tests\JNotes.Tests\TestDoubles\StubPdfExportAssetSource.cs`
- Modify:
  - `tests\JNotes.Tests\Integration\CoreServicesFixture.cs`
  - `tests\JNotes.Tests\Images\ImageLinksTests.cs`

**Infra and docs:** `C:\Data\Code\JNotes\docker-compose.yml`, `C:\Data\Code\JNotes\README.md`, `C:\Data\Code\JNotes\CLAUDE.md`. The Dockerfile and `appsettings.json` are **not** changed: optional vars like `OIDC_*` aren't declared in either.

## Steps

### Phase 1: pure Core pieces (the solution still builds with nothing wired)

1. **`JNotes.Core.csproj`**
   - Add `<PackageReference Include="Microsoft.Extensions.Http" Version="10.0.12" />`, matching the other `Microsoft.Extensions.*` references. If restore can't find 10.0.12, use the newest 10.0.x.
   - Add `<ItemGroup><EmbeddedResource Include="Export\note-pdf.css" LogicalName="JNotes.Core.Export.note-pdf.css" /></ItemGroup>`.

2. **`ImageLinks.cs`**
   - Add `public static string? ToStoredFileName(string markdownUrl)`: the file name after `RelativePrefix` if `ImageFormat.IsStoredFileName` accepts it, else null.
   - Re-implement `ToServedUrl` as `ToStoredFileName(url) is { } f ? ServedPrefix + f : null`. Behaviour must be unchanged; the existing `ImageLinksTests` and `MarkdownRendererTests` must stay green.

3. **`MermaidCodeBlockRenderer.cs`**: add `public const string OpeningTag = "<pre class=\"mermaid\">";` and use it in `Write`. Leave `MarkdownPreview.razor`'s private const alone.

4. **`MarkdownRenderer.cs`**
   - Extract `internal static MarkdownPipelineBuilder CreateBaseBuilder() => new MarkdownPipelineBuilder().UsePipeTables().DisableHtml().Use<MermaidMarkdownExtension>();`.
   - `Pipeline = CreateBaseBuilder().Use<ImageLinkMarkdownExtension>().Build();`.
   - Comment: the PDF pipeline shares this base so the two can't drift.

5. **`Serialization/PdfMarkdownRenderer.cs`**: a public static class.
   - `private static readonly MarkdownPipeline Pipeline = MarkdownRenderer.CreateBaseBuilder().Build();` (no `ImageLinkMarkdownExtension`).
   - `public sealed record PdfMarkdownResult(string Html, IReadOnlyList<string> ImageFileNames);`
   - `public static PdfMarkdownResult Render(string markdown)`:
     - `var doc = Markdown.Parse(markdown, Pipeline);`
     - For each `LinkInline` in `doc.Descendants<LinkInline>().ToList()` where `IsImage`:
       - If `ImageLinks.ToStoredFileName(link.Url ?? "")` returns a name, set `link.Url = name` and add it to a distinct, ordered list.
       - Otherwise set `link.IsImage = false`. If `link.FirstChild is null`, call `link.AppendChild(new LiteralInline(link.Url ?? ""))` so an empty alt still shows something. `LiteralInline(string)` constructor is UNVERIFIED; if it doesn't compile, use `new LiteralInline { Content = new StringSlice(url) }`.
     - `Html = doc.ToHtml(Pipeline)`.
   - Doc comment: nothing user-controlled may make Chromium fetch, so only JNotes-stored images stay images, and they are rewritten to the bare file name Gotenberg serves beside `index.html`.

6. **`Export/PdfExportOptions.cs`**
   ```csharp
   public sealed class PdfExportOptions
   {
       public string? GotenbergUrl { get; set; }                  // GOTENBERG_URL
       public Uri? GotenbergBaseUri => ParseBaseUri(GotenbergUrl);
       public bool IsEnabled => GotenbergBaseUri is not null;
       public static Uri? ParseBaseUri(string? value);            // blank -> null; Uri.TryCreate(trimmed, Absolute) and Scheme http/https else null; ensure AbsoluteUri ends with "/"
   }
   ```
   Doc comment in the style of `OidcOptions`: lives in Core because both `NoteView` and `Program.cs` need the same answer.

7. **`Export/PdfFileName.cs`**: `public static string FromTitle(string? title)`.
   1. Replace each of `\ / : * ? " < > |` and every `char.IsControl` character with a space.
   2. Collapse whitespace runs to a single space.
   3. Trim spaces and dots from both ends.
   4. If longer than 100 characters, cut to 100; if the last char is a high surrogate, drop it; trim again.
   5. If empty, use `"Untitled note"`.
   6. Return `stem + ".pdf"`.

8. **`Export/IPdfExportAssetSource.cs`**
   ```csharp
   /// Static web assets (web-root-relative) the PDF needs. Declared in Core, implemented in JNotes.Web (like ICurrentUserContext) — the files live in Web's wwwroot.
   public interface IPdfExportAssetSource { Stream? Open(string webRootRelativePath); }   // null if missing
   public static class PdfExportAssets
   {
       public const string MermaidScript = "lib/mermaid/dist/mermaid.min.js";
       public static IReadOnlyList<string> Fonts { get; } =
           ["lib/roboto/roboto-latin-400-normal.woff2", "lib/roboto/roboto-latin-500-normal.woff2", "lib/roboto/roboto-latin-700-normal.woff2"];
       public static string FileNameOf(string path) => Path.GetFileName(path); // flat name inside the Gotenberg request
   }
   ```

9. **`Export/IPdfRenderer.cs`**, **`PdfRenderingException.cs`**
   ```csharp
   public sealed record PdfRenderAsset(string FileName, Stream Content);  // caller owns/disposes Content
   public sealed record PdfRenderRequest(string IndexHtml, string? FooterHtml, IReadOnlyList<PdfRenderAsset> Assets, string? WaitForExpression);
   public interface IPdfRenderer { Task<byte[]> RenderHtmlAsync(PdfRenderRequest request, CancellationToken cancellationToken = default); }
   public sealed class PdfRenderingException(string message, Exception? inner = null) : Exception(message, inner);
   ```

10. **`Export/note-pdf.css`**: a hand-written, self-contained light stylesheet. Use no `--mud-*` variables and no nesting; plain CSS keeps it robust in any Chromium version.
    - Three `@font-face` rules for `"Roboto"` at 400, 500 and 700, with `src: url("roboto-latin-400-normal.woff2") format("woff2")` and so on (flat names).
    - `:root { color-scheme: light; }`.
    - `html { -webkit-print-color-adjust: exact; print-color-adjust: exact; }`.
    - `body`: margin 0, background `#fff`, colour `#424242`, `font-family: Roboto, "Noto Sans", "Liberation Sans", Arial, sans-serif`, 11pt, line-height 1.55, `overflow-wrap: anywhere`.
    - Header:
      - `.note-title`: 20pt, weight 500, margin `0 0 6pt`.
      - `.note-tags .tag`: inline-block pill, 1px border `rgba(0,0,0,.12)`, radius 999px, padding `1pt 7pt`, 8.5pt, margin `0 4pt 4pt 0`.
      - `.note-meta`: 9pt, colour `rgba(0,0,0,.54)`, bottom border `rgba(0,0,0,.12)`, padding-bottom 8pt, margin-bottom 14pt.
    - `.markdown` rules mirroring `markdown-preview.css`, using literal colours. Primary is `#2f6feb`; lines `rgba(0,0,0,.12)`; code background `#f5f5f5`; secondary text `rgba(0,0,0,.54)`; mono stack `ui-monospace, SFMono-Regular, Menlo, Consolas, "Liberation Mono", "DejaVu Sans Mono", monospace`.
    - `pre`: `white-space: pre-wrap` (no scrolling in print).
    - Page-break rules:
      - `h1–h6 { break-after: avoid; }`
      - `pre, blockquote, img, tr, pre.mermaid, .checklist li { break-inside: avoid; }`
      - `thead { display: table-header-group; }`
      - `img { max-width: 100%; max-height: 24cm; height: auto; object-fit: contain; }`
    - Mermaid:
      - `pre.mermaid`: transparent background, padding 0.
      - `pre.mermaid.mermaid-rendered`: centred.
      - `pre.mermaid.mermaid-rendered svg { max-width: 100%; height: auto; }`
      - `pre.mermaid:not(.mermaid-rendered)`: monospace, `pre-wrap`, secondary colour.
      - `.mermaid-error`: left border `3px solid #ff9800`, plus `::before { content: "Diagram error — showing the source."; display: block; font-family: Roboto, sans-serif; color: #ff9800; }`.
    - Checklist:
      - `.checklist h2`: 9pt, uppercase, letter-spacing .08em, secondary colour, margin `14pt 0 4pt`.
      - `.checklist ul`: `list-style: none; padding: 0; margin: 0`.
      - `.checklist li`: `display: flex; gap: 8pt; align-items: baseline; padding: 2pt 0`.
      - `.box`: 10pt square, `1.5px solid #757575`, radius 2px, flex none.
      - `li.checked .box`: background and border `#2f6feb`.
      - `li.checked .box::after`: `content: "✓"; color: #fff; font-size: 8pt; line-height: 10pt; display: block; text-align: center`.
      - `li.checked .text`: line-through, secondary colour.
      - `.empty`: secondary colour, italic.

11. **`Export/NotePdfDocumentBuilder.cs`**: pure static.
    - `public sealed record NotePdfDocument(string Html, IReadOnlyList<string> ImageFileNames, bool HasMermaid);`
    - `public const string ReadyExpression = "window.jnotesPdfReady === true";`
    - `public const string MermaidScriptFileName = "mermaid.min.js";`
    - `public static string FooterHtml { get; }`: a full `<html><head><style>…</style></head><body>` document. The body is one `<div>` with `width:100%; text-align:center; font-family: sans-serif; font-size: 8pt; color: #757575;` containing `Page <span class="pageNumber"></span> of <span class="totalPages"></span>`. Chromium footers need an explicit font-size and can't load external resources.
    - The stylesheet is loaded once from the manifest resource `"JNotes.Core.Export.note-pdf.css"` (static `Lazy<string>`); throw `InvalidOperationException` if it's missing.
    - `public static NotePdfDocument Build(Note note, string lastUpdatedText)` emits, HTML-encoding every user string with `WebUtility.HtmlEncode`:
      ```html
      <!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><title>{title}</title><style>{css}</style></head>
      <body><article class="note">
        <header><h1 class="note-title">{title or "Untitled note"}</h1>
          [<div class="note-tags"><span class="tag">{tag}</span>…</div> if any]
          <div class="note-meta">{Text|Checklist} · {lastUpdatedText}</div></header>
        <main class="note-body …">{body}</main></article>
        [<script src="mermaid.min.js"></script> only if HasMermaid]
        <script>{ReadyScript}</script></body></html>
      ```
    - **Text note body:** `PdfMarkdownRenderer.Render(note.Body)` inside `<main class="note-body markdown">`. `ImageFileNames` comes from that result. `HasMermaid = html.Contains(MermaidCodeBlockRenderer.OpeningTag, Ordinal)`.
    - **Checklist body:** `ChecklistMarkdownParser.ParseSections(note.Body)` inside `<main class="note-body checklist">`. No images, `HasMermaid = false`.
      - For each section:
        - Skip an untitled section with 0 items.
        - A titled section emits `<h2>{Title}</h2>`, then a `<ul>` of items, or `<p class="empty">No items.</p>` when it is empty.
      - An item is `<li class="checked?"><span class="box"></span><span class="text">{Text}</span></li>`.
      - If nothing was emitted at all, emit `<p class="empty">This checklist has no items.</p>`.
    - **`ReadyScript`**, always included (a C# raw string const):
      ```js
      (async () => {
        try {
          await document.fonts.ready;
          if (window.mermaid) {
            mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', theme: 'default' });
            let n = 0;
            for (const el of document.querySelectorAll('pre.mermaid')) {
              const source = el.textContent ?? '';
              if (source.trim() === '') continue;
              const id = 'jnotes-mermaid-' + (n++);
              try {
                if (!(await mermaid.parse(source, { suppressErrors: true }))) { el.classList.add('mermaid-error'); continue; }
                const { svg } = await mermaid.render(id, source);
                el.innerHTML = svg; el.classList.add('mermaid-rendered');
              } catch { el.classList.add('mermaid-error'); }
              finally { document.getElementById('d' + id)?.remove(); }
            }
          }
        } finally { window.jnotesPdfReady = true; }
      })();
      ```

12. **`Export/GotenbergPdfRenderer.cs`**: `public sealed class GotenbergPdfRenderer(HttpClient http, ILogger<GotenbergPdfRenderer> logger) : IPdfRenderer`.
    - `public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);`
    - Throw `InvalidOperationException("PDF export is not configured (GOTENBERG_URL).")` if `http.BaseAddress is null`.
    - Build a `MultipartFormDataContent`:
      - `new StringContent(IndexHtml, UTF8, "text/html")` as `("files", "index.html")`.
      - The footer, if not null, as `("files", "footer.html")`.
      - Each asset as `new StreamContent(asset.Content)` under `("files", asset.FileName)`.
      - String fields: `paperWidth=8.27`, `paperHeight=11.7`, `marginTop=0.6`, `marginBottom=0.8`, `marginLeft=0.6`, `marginRight=0.6`, `printBackground=true`, plus `waitForExpression` when set. Use literal invariant strings.
    - `PostAsync("forms/chromium/convert/html", form, ct)`, relative to BaseAddress, which always ends in `/`.
    - On non-success: read up to 500 chars of the body, `LogWarning` the status, the `Gotenberg-Trace` header and the snippet, then throw `PdfRenderingException($"The PDF service returned {(int)status}.")`.
    - On success: `ReadAsByteArrayAsync(ct)`.
    - `catch (HttpRequestException ex)`: log a warning, then throw `PdfRenderingException("The PDF service couldn't be reached.", ex)`.
    - `catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)`: log, then throw `PdfRenderingException("The PDF service timed out.", ex)`.
    - Caller cancellation propagates unwrapped.

13. **`Export/INoteExportService.cs`**, **`NoteExportService.cs`**
    ```csharp
    public sealed record NotePdf(string FileName, byte[] Content);
    /// Implicit current user (via INoteService/IImageService), like NoteService — a page/endpoint can't export another user's note.
    public interface INoteExportService { Task<NotePdf> ExportPdfAsync(Guid noteId, CancellationToken cancellationToken = default); }
    ```
    The implementation's constructor takes `INoteService`, `IImageService`, `IPdfRenderer`, `IPdfExportAssetSource`, `IAppTimeZoneProvider` and `IOptions<PdfExportOptions>`. `ExportPdfAsync`:
    1. If `!IsEnabled`, throw `InvalidOperationException`.
    2. `var note = await _notes.GetAsync(noteId, ct)`. `NoteNotFoundException` propagates; that is the ownership check.
    3. `lastUpdated = $"Last updated {_tz.ToAppTimeZone(note.UpdatedAt).DateTime.ToString("g")} ({_tz.TimeZone.Id})"`: same format as `NoteView`, plus the zone id so the PDF stands alone.
    4. `var doc = NotePdfDocumentBuilder.Build(note, lastUpdated)`.
    5. Collect assets into a `List<PdfRenderAsset>`, disposing every stream in `finally`:
       - each font from `PdfExportAssets.Fonts` that `Open` returns non-null, named `FileNameOf(path)`;
       - if `doc.HasMermaid`, `PdfExportAssets.MermaidScript` as `NotePdfDocumentBuilder.MermaidScriptFileName`;
       - each `doc.ImageFileNames` through `_images.OpenAsync(name, ct)`, skipping null (a missing image just renders broken, as it does in the app).
    6. `bytes = await _renderer.RenderHtmlAsync(new(doc.Html, NotePdfDocumentBuilder.FooterHtml, assets, NotePdfDocumentBuilder.ReadyExpression), ct)`.
    7. Return `new NotePdf(PdfFileName.FromTitle(note.Title), bytes)`.

14. **`ServiceCollectionExtensions.cs`**: after the OIDC block:
    ```csharp
    // Optional PDF export — same flat-env-var pattern; unset/invalid GOTENBERG_URL means off (PdfExportOptions.IsEnabled).
    services.Configure<PdfExportOptions>(o => o.GotenbergUrl = configuration["GOTENBERG_URL"] ?? o.GotenbergUrl);
    services.AddHttpClient<IPdfRenderer, GotenbergPdfRenderer>((sp, client) =>
    {
        if (sp.GetRequiredService<IOptions<PdfExportOptions>>().Value.GotenbergBaseUri is { } baseUri) client.BaseAddress = baseUri;
        client.Timeout = GotenbergPdfRenderer.Timeout;
    });
    services.AddScoped<INoteExportService, NoteExportService>();
    // IPdfExportAssetSource is intentionally NOT registered here — like ICurrentUserContext, JNotes.Web does (its files are Web's wwwroot).
    ```
    Also update the class doc comment's "one deliberate exception" sentence to name both interfaces.

### Phase 2: Web wiring (builds and runs; feature off by default)

15. **`Web/Export/WebRootPdfExportAssetSource.cs`**: `internal sealed class WebRootPdfExportAssetSource(IWebHostEnvironment env) : IPdfExportAssetSource`.
    - `Open`: `var f = env.WebRootFileProvider.GetFileInfo(path); return f.Exists && !f.IsDirectory ? f.CreateReadStream() : null;`
    - Comment: only fixed `PdfExportAssets` paths reach this, never user input, and `PhysicalFileProvider` already refuses `..`.

16. **`Web/Export/NotePdfEndpoints.cs`**: `internal static class NotePdfEndpoints` with `public static IEndpointRouteBuilder MapNotePdfExport(this IEndpointRouteBuilder app)`.
    - `app.MapGet("/notes/{id:guid}/pdf", async (Guid id, INoteExportService export, HttpContext context, CancellationToken ct) => …).RequireAuthorization();`
    - Handler:
      - `catch (NoteNotFoundException)` returns `Results.NotFound()` (rendered by the `/not-found` status page).
      - `catch (PdfRenderingException)` returns `Results.Content(UnavailablePage(id), "text/html; charset=utf-8", statusCode: StatusCodes.Status503ServiceUnavailable)`.
      - On success:
        ```csharp
        var cd = new ContentDispositionHeaderValue("inline"); cd.SetHttpFileName(pdf.FileName);  // Microsoft.Net.Http.Headers
        context.Response.Headers.ContentDisposition = cd.ToString();
        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.Bytes(pdf.Content, "application/pdf");
        ```
    - `UnavailablePage(Guid id)`: a tiny self-contained light HTML page with inline style (the app CSS is not linked). Heading "Couldn't create the PDF". Text: "The PDF service isn't reachable right now. Close this tab and try again in a moment — if it keeps happening, ask your administrator to check the Gotenberg container." Include `<a href="/notes/{id}">Back to the note</a>`. The only interpolated value is the Guid.
    - Comment: the endpoint (not an `@onclick`) exists because only a real HTTP response can hand the browser a file, and the anchor has `target="_blank"` so Blazor's router doesn't intercept it.

17. **`Program.cs`**
    - Add usings `JNotes.Core.Export`, `JNotes.Web.Export`, `Microsoft.Extensions.Options`.
    - After `AddScoped<ICurrentUserContext, …>`: `builder.Services.AddSingleton<IPdfExportAssetSource, WebRootPdfExportAssetSource>();`, commented that this is the same Core-interface, Web-implementation split. Register it **always**: Development's `ValidateOnBuild` would otherwise fail on `NoteExportService`.
    - After the `/images` endpoint:
      ```csharp
      // Optional PDF export: mapped only when GOTENBERG_URL is valid, so with the feature off the route simply 404s like any unknown URL.
      var pdfExport = app.Services.GetRequiredService<IOptions<PdfExportOptions>>().Value;
      if (pdfExport.IsEnabled) app.MapNotePdfExport();
      else if (!string.IsNullOrWhiteSpace(pdfExport.GotenbergUrl))
          app.Logger.LogWarning("GOTENBERG_URL is set but isn't an absolute http(s) URL — PDF export is disabled.");
      ```
      Never log the URL itself; it may contain credentials.

18. **`_Imports.razor`**: add `@using JNotes.Core.Export`.

19. **`NoteView.razor`**
    - Add `@using Microsoft.Extensions.Options` and `@inject IOptions<PdfExportOptions> PdfExportOptions`.
    - In the button `MudStack`, add `Wrap="Wrap.Wrap"` so three buttons wrap on phones.
    - Between Edit and Delete:
      ```razor
      @if (PdfExportOptions.Value.IsEnabled)
      {
          <MudButton Href="@($"/notes/{_note.Id}/pdf")" Target="_blank" Rel="noopener" Variant="Variant.Outlined"
                     StartIcon="@Icons.Material.Outlined.PictureAsPdf" title="Export as PDF (opens in a new tab)">Export PDF</MudButton>
      }
      ```
      `Icons.Material.Outlined.PictureAsPdf` is UNVERIFIED; if it doesn't compile, use `Icons.Material.Filled.PictureAsPdf`.
    - Leave everything else untouched. `NoteService` is still resolved in `OnParametersSetAsync`, and `IOptions` needs no user context.
    - Comment: the PDF reflects the file at click time, so checklist changes made on this page are included.

### Phase 3: compose and docs (see the next sections)

## Docker/compose changes

In `C:\Data\Code\JNotes\docker-compose.yml`, add to the `jnotes` `environment:` list, after the OIDC block:

```yaml
      # Optional PDF export (an "Export PDF" button on each note). Off unless GOTENBERG_URL is set. To turn
      # it on, add BOTH lines to a .env file:  COMPOSE_PROFILES=pdf  (starts the gotenberg service below)
      # and  GOTENBERG_URL=http://gotenberg:3000  (tells the app where it is). See README.md's "PDF export".
      - GOTENBERG_URL=${GOTENBERG_URL:-}
```

Add a new service after `postgres` (before `volumes:`):

```yaml
  gotenberg:
    # Chromium-based HTML -> PDF converter, used only by the optional PDF export. Behind the "pdf"
    # profile so a plain `docker compose up` never starts it. No `ports:` on purpose: only the jnotes
    # service (same compose network) talks to it, and Gotenberg has no authentication of its own.
    image: gotenberg/gotenberg:8
    profiles: ["pdf"]
    command:
      - gotenberg
      # JNotes sends everything the page needs (HTML, images, fonts, mermaid) inside the request, so
      # Chromium never needs the network: only the request's own temp files may load. Stops a note
      # (e.g. an <img> in a mermaid label) from making Gotenberg fetch URLs on your internal network.
      - "--chromium-allow-list=^file:///tmp/.*"
    healthcheck:
      # curl ships in the image for exactly this.
      test: ["CMD", "curl", "--silent", "--fail", "http://localhost:3000/health"]
      interval: 30s
      timeout: 5s
      retries: 3
    restart: unless-stopped
```

- There is deliberately **no `depends_on`** from `jnotes`: a depends_on pointing at a profile that isn't enabled breaks `docker compose up`, and the app only calls Gotenberg on click anyway.
- Fonts: Gotenberg ships Noto, Liberation and DejaVu but not Roboto, which is why the export attaches the vendored Roboto.
- The Dockerfile is unchanged.

## Docs changes

**README.md**

- *Features*, new bullet after "Checklist notes":
  `- **PDF export (optional)** — an **Export PDF** button on a note opens it as a light-themed A4 PDF (images, diagrams and checklists included), rendered by a [Gotenberg](https://gotenberg.dev) container. Off unless \`GOTENBERG_URL\` is set.`
- *Project layout* paragraph: add `INoteExportService` to the list of Core interfaces Web consumes.
- *Configuration* table, new row after `OIDC_DISPLAY_NAME`:
  `| \`GOTENBERG_URL\` | unset | Base URL of a [Gotenberg 8](https://gotenberg.dev) service (e.g. \`http://gotenberg:3000\`) used to render note PDFs. Leave unset to disable PDF export entirely — no button, and \`/notes/{id}/pdf\` returns 404. Must be an absolute http(s) URL; anything else logs a warning and leaves it off. |`
- *Running with Docker*, a new subsection "PDF export (optional)":
  > The `gotenberg` service in `docker-compose.yml` sits behind a compose profile, so it only runs if you ask for it. To enable PDF export, create a `.env` file next to `docker-compose.yml` with:
  > ```
  > COMPOSE_PROFILES=pdf
  > GOTENBERG_URL=http://gotenberg:3000
  > ```
  > then `docker compose up -d --build`. Gotenberg isn't published to the host; only JNotes talks to it. It runs a headless Chromium, so budget a few hundred MB of RAM. If it's stopped, the button still shows but the new tab explains the PDF service isn't reachable. The PDF is always light-themed, whatever your theme. Mermaid diagrams are rendered in the PDF. Images from other websites appear as links rather than being downloaded.
  >
  > Running locally instead: `docker run --rm -p 3000:3000 gotenberg/gotenberg:8`, then start JNotes with `GOTENBERG_URL=http://localhost:3000`.

**CLAUDE.md**

- *Architecture* paragraph 2: add `INoteExportService` to the list of Core interfaces.
- *Architecture* paragraph 3: change "All Core DI wiring is in … **except** `ICurrentUserContext`" to "**except** `ICurrentUserContext` and `IPdfExportAssetSource`". Add: "(the latter's files, mermaid and Roboto, are Web's `wwwroot`, read through `IWebHostEnvironment.WebRootFileProvider`)".
- *Tests*, append to the fixture paragraph: "`CoreServicesFixture` also swaps in `RecordingPdfRenderer`/`StubPdfExportAssetSource` and enables `PdfExportOptions`, so `NoteExportService` runs without Gotenberg."
- New section `## PDF export (optional)`, placed between "Checklists" and "Tests":

> **Off unless `GOTENBERG_URL` is set** (`PdfExportOptions`, read in `AddJNotesCore` like `OIDC_*`; `IsEnabled` needs an absolute http(s) URL). Both halves check it: `NoteView` renders the **Export PDF** button only when enabled, and `Program.cs` maps `GET /notes/{id:guid}/pdf` only when enabled. With it off, the route is simply unmapped (404), not a handler that refuses.
>
> **A plain anchor to a minimal-API endpoint, not an `@onclick`.** Only a real HTTP response can hand the browser a file. The button is `Href` + `Target="_blank"`: Blazor's router ignores anchors with a non-`_self` target, so the browser does a real GET with the cookie. The endpoint (`Export/NotePdfEndpoints.cs`) returns `Content-Disposition: inline` with a title-derived name (`PdfFileName`), so the PDF opens in the new tab and "Save" proposes that name. It resolves `INoteExportService` in an ordinary request scope, where `HttpContext` always exists, so the `ICurrentUserContext` gotcha doesn't apply. The ownership check is the usual one: `INoteService.GetAsync` only reads the caller's folder, so another user's id is a 404. If Gotenberg is down or errors, `GotenbergPdfRenderer` throws `PdfRenderingException` and the endpoint returns **503 with its own small HTML page**. It needs a body, or `UseStatusCodePagesWithReExecute` would show "Not found".
>
> **Gotenberg's HTML route with a self-contained document, never its URL route.** Gotenberg has no cookie, and our pages are interactive circuits in the user's (dark) theme. `NotePdfDocumentBuilder` (pure static) builds `index.html` with an inline light stylesheet (`Export/note-pdf.css`, an embedded resource with literal colours, no `--mud-*` variables). Everything the page loads is sent as a flat sibling file in the same multipart request: stored images (read through `IImageService`, so per-user), the vendored Roboto `.woff2` files (the Gotenberg image has no Roboto), and `mermaid.min.js` only when a diagram exists.
>
> **Nothing user-controlled may make Chromium fetch.** `PdfMarkdownRenderer` shares `MarkdownRenderer.CreateBaseBuilder()` (tables, `DisableHtml`, mermaid) but not `ImageLinkMarkdownExtension`. It rewrites only `images/<stored name>` to the bare file name, and turns every other image (external URL, `data:`, odd path) into a plain link. As a second layer, compose starts Gotenberg with `--chromium-allow-list=^file:///tmp/.*`, because mermaid's sanitizer still allows an `<img>` inside a label.
>
> **Mermaid renders inside Gotenberg's Chromium**, light theme, `securityLevel: 'strict'`, with the same parse-then-render and error fallback as `mermaid-preview.js`. An inline script sets `window.jnotesPdfReady` once fonts and diagrams are done, and Gotenberg's `waitForExpression` waits for it. If the flag never flips, Gotenberg's own 30 s timeout answers 503. Checklists are static HTML mirroring `ChecklistPanel`: items only, grouped by heading, with CSS-drawn boxes.
>
> Compose: the `gotenberg` service sits behind the `pdf` profile, with no published port, and `jnotes` has no `depends_on` on it (that would break a plain `up` with the profile off). The app always gets `GOTENBERG_URL=${GOTENBERG_URL:-}`, which is empty (off) unless `.env` sets it.

## Tests to add or update (`tests/JNotes.Tests`, xUnit)

**Unit tests (no PostgreSQL)**

- `Export/PdfFileNameTests`:
  - `Q3 / Plan: "draft" <v2>` gives `Q3 Plan draft v2.pdf`.
  - Null, empty or whitespace gives `Untitled note.pdf`.
  - `"..."` gives `Untitled note.pdf`.
  - CR/LF and tab become spaces.
  - Unicode (`Café 日本`) is kept.
  - A 300-char title gives a stem of ≤ 100 chars.
  - A surrogate pair at the cut isn't split (the result is valid UTF-16).
- `Export/PdfExportOptionsTests`:
  - `[Theory]` over `ParseBaseUri`: null, `""` and `"  "` give null; `"gotenberg:3000"`, `"ftp://x"` and `"not a url"` give null; `"http://gotenberg:3000"` gives `http://gotenberg:3000/`; `"https://h/prefix"` gives `https://h/prefix/`.
  - `IsEnabled` follows.
  - `AddJNotesCore` with `new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["GOTENBERG_URL"]="http://g:3000"}).Build()`, plus `services.AddLogging()`, yields `IOptions<PdfExportOptions>.Value.IsEnabled == true`. Without the key it is false.
- `Serialization/PdfMarkdownRendererTests`:
  - A stored image gives `src="<name>"` and appears in `ImageFileNames`.
  - The same image twice is listed once.
  - `https://example.com/x.png`, `images/../<name>`, `images/other.png` and `data:image/png;base64,AAAA` give no `<img` in the HTML, an `<a href=` instead, and are not listed.
  - An empty-alt external image still produces visible link text.
  - A mermaid fence gives `<pre class="mermaid">`.
  - `<script>` is still encoded.
  - Tables render.
- `Images/ImageLinksTests`: add `ToStoredFileName` cases (valid, traversal, wrong prefix, external). The existing `ToServedUrl` tests remain unchanged.
- `Export/NotePdfDocumentBuilderTests`:
  - A title `<b>x</b>` is encoded, both in `<title>` and in the `<h1>`.
  - An empty title gives "Untitled note".
  - Tags are rendered and encoded.
  - The `lastUpdatedText` appears.
  - The type label is "Text"/"Checklist".
  - The CSS is inlined: contains `color-scheme: light` and `roboto-latin-400-normal.woff2`, which proves the embedded resource loads.
  - The ready script is always present.
  - A mermaid note gives `HasMermaid` and `<script src="mermaid.min.js">`; a plain note has neither.
  - A text note with an image passes the names through.
  - Checklist cases:
    - headings become `<h2>`;
    - a checked item gets `class="checked"`;
    - item text is encoded (`- [ ] <i>x</i>`);
    - an untitled empty section is skipped when categories exist;
    - an empty category shows "No items.";
    - an empty body shows "This checklist has no items.";
    - prose lines are not included.
  - `FooterHtml` contains `pageNumber` and `totalPages`.
- `Export/GotenbergPdfRendererTests`: a hand-written `FakeHandler : HttpMessageHandler` (a private nested class). It records the request and, **inside `SendAsync`**, enumerates `(MultipartFormDataContent)request.Content`. For each part it reads `Headers.ContentDisposition.Name/FileName` (`Trim('"')`) and the string body.
  - Success: a POST to `http://gotenberg.test:3000/forms/chromium/convert/html`; `files` parts are `index.html`, `footer.html` and the asset names with their bytes; fields `paperWidth=8.27`, `paperHeight=11.7`, `printBackground=true`, `waitForExpression=window.jnotesPdfReady === true`; returns the response bytes.
  - No footer or wait expression: those parts are absent.
  - A 503 or 400 response throws `PdfRenderingException`.
  - The handler throwing `HttpRequestException` throws `PdfRenderingException`.
  - Timeout: `HttpClient.Timeout = 100ms` with a handler that delays 5 s using the token throws `PdfRenderingException`.
  - A pre-cancelled caller token throws `OperationCanceledException`, not wrapped.
  - A null `BaseAddress` throws `InvalidOperationException`.
  - Construct with `new HttpClient(handler) { BaseAddress = new("http://gotenberg.test:3000/") }` and `NullLogger<GotenbergPdfRenderer>.Instance`.

**Integration tests (real PostgreSQL through `CoreServicesFixture`)**

- **Fixture changes:**
  - After `AddJNotesCore`: `services.Configure<PdfExportOptions>(o => o.GotenbergUrl = "http://gotenberg.test:3000")`.
  - Register `var pdfRenderer = new RecordingPdfRenderer(); services.AddSingleton<IPdfRenderer>(pdfRenderer);` (the last registration wins over the typed client) and `services.AddSingleton<IPdfExportAssetSource, StubPdfExportAssetSource>()`.
  - Expose `public RecordingPdfRenderer PdfRenderer` and `public INoteExportService NoteExportService => _scope.ServiceProvider.GetRequiredService<INoteExportService>();`.
  - Pass the renderer through the private constructor like the other fields.
- **`TestDoubles/RecordingPdfRenderer`:** stores `LastRequest`, `CallCount`, and `AssetBytes` (a `Dictionary<string, byte[]>`). It **copies each asset stream to a byte array during the call**, because the service disposes the streams afterwards. It returns `"%PDF-1.7 test"u8.ToArray()`.
- **`TestDoubles/StubPdfExportAssetSource`:** returns a `MemoryStream` of a few known bytes for any path in `PdfExportAssets`, else null.
- **`Integration/NoteExportServiceTests`** (`IAsyncLifetime`, like `ImageServiceTests`):
  - Exporting your own text note: one render call; the HTML contains the title; the file name comes from the title; the bytes are passed through; the three font assets are present; `mermaid.min.js` is absent.
  - An image saved through `IImageService` and referenced as `images/<name>` is attached with the right length. A referenced-but-missing stored name is skipped without throwing.
  - A mermaid note attaches `mermaid.min.js`.
  - A checklist note renders items.
  - `ActAsUserB()` exporting user A's note throws `NoteNotFoundException`, and `CallCount` stays 0.
  - A missing id throws `NoteNotFoundException`.

No Razor or endpoint tests: the project has no bUnit or WebApplicationFactory by design, so the endpoint and button are verified manually.

## Manual verification (for the tester)

**Prerequisites** (PowerShell on the dev box):
```powershell
docker run -d --name jnotes-pg -p 5432:5432 -e POSTGRES_USER=jnotes -e POSTGRES_PASSWORD=jnotes -e POSTGRES_DB=jnotes postgres:18-alpine   # if not already running
dotnet build JNotes.slnx
dotnet format JNotes.slnx --verify-no-changes
dotnet test JNotes.slnx
```
All must pass, including the new `Export/*` and `NoteExportServiceTests`.

**Seed data** (needs a signed-in admin, "A"). In the app, create:
- (a) a text note titled `Q3 / Plan: "draft" <v2>` with tags `work`, `Été`, containing:
  - headings, a bulleted list, a blockquote, a 60-line fenced code block with one very long line, and a 40-row pipe table;
  - a pasted image and `![ext](https://upload.wikimedia.org/wikipedia/commons/4/47/PNG_transparency_demonstration_1.png)`;
  - a valid ```` ```mermaid ```` `flowchart LR` block, and an invalid mermaid block (`flowchart ???`);
- (b) a checklist with an uncategorized item, two `##` categories, some checked items, and one empty category.

In Settings, make sure the theme is **dark**. Create a second Member user "B" (Settings → Administration → Add user).

1. **Flag off.** Run `Remove-Item Env:GOTENBERG_URL -ErrorAction SilentlyContinue; dotnet run --project src/JNotes.Web`. Open `/notes/{id-of-a}`.
   - Expected: only Edit and Delete, no Export PDF.
   - Browse to `/notes/{id-of-a}/pdf`: the app's Not found page, and DevTools Network shows 404.
2. **Flag on, local Gotenberg.**
   ```powershell
   docker run --rm -d --name gotenberg -p 3000:3000 gotenberg/gotenberg:8 gotenberg "--chromium-allow-list=^file:///tmp/.*"
   $env:GOTENBERG_URL = "http://localhost:3000"; dotnet run --project src/JNotes.Web
   ```
   Also try `GOTENBERG_URL=gotenberg:3000` once: the button is absent and the startup log shows the "isn't an absolute http(s) URL" warning.
3. **Button.** On note (a), Export PDF appears between Edit and Delete. At a narrow width (DevTools, 375 px) the buttons wrap without overflow.
4. **Click on text note (a).**
   - A new tab opens and shows the PDF inline; the original tab is still on the note (no navigation, no circuit error).
   - The viewer's download/save proposes `Q3 Plan draft v2.pdf`.
   - The PDF is **light** (white background, dark text) although the app is dark.
   - Title, tags as pills (`Été` is correct), and "Text · Last updated … (UTC)".
   - A4 pages with a "Page X of Y" footer.
   - The long code line wraps; table headers repeat on the following page; the image shows; the external image appears as a link labelled "ext", not the picture.
   - The valid mermaid diagram renders in light colours; the invalid one shows its source under "Diagram error — showing the source."
   - Optional: `Select-String -Path .\Downloads\*.pdf -Pattern Roboto -SimpleMatch` finds a match (Roboto embedded).
5. **Checklist (b).** Category headings; checked items have a filled box with ✓ and struck-through text; the empty category shows "No items."; there is no add-item UI. Then tick an item on the page, export again, and confirm the PDF reflects it.
6. **Gotenberg down.** Run `docker stop gotenberg`, then click Export PDF. The new tab shows "Couldn't create the PDF" with a "Back to the note" link. DevTools shows 503, and the app console has a warning. Restart it with the command from step 2 and confirm it works again.
7. **Another user's note.** Sign in as B in a private window and open `/notes/{id-of-a}/pdf`: the Not found page (404). `docker logs gotenberg` shows no new conversion.
8. **Unauthenticated.** In a fresh private window, open `/notes/{id-of-a}/pdf`: redirect to `/login?ReturnUrl=%2Fnotes%2F…%2Fpdf`. Sign in as A: the PDF is served.
9. **Docker Compose.**
   - `docker compose up -d --build` with no `.env` changes: `docker compose ps` lists only `jnotes` and `postgres`, and no button appears.
   - Add `.env` with `COMPOSE_PROFILES=pdf` and `GOTENBERG_URL=http://gotenberg:3000`, then `docker compose up -d --build`. `gotenberg` shows `healthy` in `docker compose ps`, and no host port is published.
   - Repeat step 4. The rendered mermaid diagram and Roboto are what prove the published `wwwroot` assets are found.
   - `docker compose stop gotenberg`, then step 6's friendly page.
10. **SSRF layer (optional).** Add a mermaid label `A["<img src='http://jnotes:8080/login'>"]` to a note and export. The PDF still renders, and `docker compose logs jnotes` shows no request to `/login` from Gotenberg's IP.

## Risks and edge cases

- **The `--chromium-allow-list` regex** could block something Chromium needs (it should only apply to intercepted network requests; `data:`/`about:` aren't). Steps 4 and 9 exercise it. If conversions fail with 400 while the flag is present, the tester reports it; removing the flag is the fallback, with the app-side image rewriting still in place.
- **Memory:** the PDF and attached images are buffered as `byte[]` (images up to 10 MB each). This is acceptable for a self-hosted app. Streaming the Gotenberg response is a later optimisation.
- **No rate limiting:** each export launches Chromium work, so one user can load Gotenberg. A cross-site GET can trigger an export, but the PDF is only returned to the victim's own browser.
- **Floating `gotenberg:8` tag:** form-field names are stable in 8.x, but new minors may change defaults.
- A note edited in the editor within the last debounce window exports its last saved state (file-based). This is expected.
- `ContentOnly` staleness doesn't matter: the export reads the file, not the index.
- A corrupted note (`NoteParseException`) propagates to the normal error page, the same as `NoteView`.
- UNVERIFIED at compile time: `Icons.Material.Outlined.PictureAsPdf`, `LiteralInline(string)` constructor, `ContentDispositionHeaderValue.SetHttpFileName`, and `Microsoft.Extensions.Http` 10.0.12 availability. Fallbacks are given in the relevant steps; `SetHttpFileName` is used by ASP.NET's own `FileResult` and should exist.
- Gotenberg with Basic Auth (`--api-enable-basic-auth`) is not supported by this client. With the compose setup the service is unreachable from outside, so it isn't needed.

## Open questions

None. Judgment calls made:
- inline in a new tab rather than a forced download;
- 503 HTML page for failures;
- external images become links;
- mermaid is rendered in the PDF;
- only checklist items are exported, matching the panel;
- a single `GOTENBERG_URL` env var with no timeout variable.

## Out of scope

- Batch or multi-note export, and exporting from the editor, list or trash.
- PDF of the note's raw markdown.
- Any change to `MarkdownPreview`, `mermaid-preview.js`, the app CSS, `ChecklistPanel`, the note file format, EF Core or migrations (none needed).
- The Dockerfile.
- Rate limiting, Gotenberg authentication, and a configurable timeout.
- bUnit or WebApplicationFactory test infrastructure.
- Do not register `IPdfExportAssetSource` in Core. Do not touch `BannedSymbols.txt`.

Sources consulted: [Gotenberg: Convert HTML to PDF](https://gotenberg.dev/docs/convert-with-chromium/convert-html-to-pdf), [Gotenberg: Configuration](https://gotenberg.dev/docs/configuration), [Gotenberg Dockerfile](https://raw.githubusercontent.com/gotenberg/gotenberg/main/build/Dockerfile).