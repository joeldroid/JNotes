using JNotes.Core.Notes;
using JNotes.Core.Time;
using Microsoft.Extensions.Options;

namespace JNotes.Core.Export;

/// <inheritdoc cref="INoteExportService"/>
public sealed class NoteExportService(
    INoteService notes,
    IPdfRenderer renderer,
    IPdfExportAssetSource assetSource,
    IAppTimeZoneProvider timeZone,
    IOptions<PdfExportOptions> options) : INoteExportService
{
    public async Task<NotePdf> ExportPdfAsync(Guid noteId, CancellationToken cancellationToken = default)
    {
        if (!options.Value.IsEnabled)
        {
            throw new InvalidOperationException("PDF export is not enabled (GOTENBERG_URL).");
        }

        // GetAsync resolves owner-or-shared: an id the caller neither owns nor has been given is a
        // NoteNotFoundException, which is the access check. Any access level (Read included) may export — it's a read.
        var note = await notes.GetAsync(noteId, cancellationToken);

        // Same format as NoteView, plus the zone id so the PDF stands alone.
        var lastUpdated =
            $"Last updated {timeZone.ToAppTimeZone(note.UpdatedAt).DateTime.ToString("g")} ({timeZone.TimeZone.Id})";
        var document = NotePdfDocumentBuilder.Build(note, lastUpdated);

        var assets = new List<PdfRenderAsset>();
        try
        {
            foreach (var font in PdfExportAssets.Fonts)
            {
                if (assetSource.Open(font) is { } stream)
                {
                    assets.Add(new PdfRenderAsset(PdfExportAssets.FileNameOf(font), stream));
                }
            }

            if (document.HasMermaid && assetSource.Open(PdfExportAssets.MermaidScript) is { } mermaid)
            {
                assets.Add(new PdfRenderAsset(NotePdfDocumentBuilder.MermaidScriptFileName, mermaid));
            }

            foreach (var imageName in document.ImageFileNames)
            {
                // A missing image just renders broken, as it does in the app.
                // Through the note, so a recipient's images come from the owner's folder under the same access rules as the app.
                if (await notes.OpenImageAsync(noteId, imageName, cancellationToken) is { } image)
                {
                    assets.Add(new PdfRenderAsset(imageName, image.Stream));
                }
            }

            var bytes = await renderer.RenderHtmlAsync(
                new PdfRenderRequest(
                    document.Html,
                    NotePdfDocumentBuilder.FooterHtml,
                    assets,
                    NotePdfDocumentBuilder.ReadyExpression),
                cancellationToken);

            return new NotePdf(PdfFileName.FromTitle(note.Title), bytes);
        }
        finally
        {
            foreach (var asset in assets)
            {
                await asset.Content.DisposeAsync();
            }
        }
    }
}
