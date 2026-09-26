using JNotes.Core.Images;
using JNotes.Core.Notes;
using JNotes.Core.Time;
using Microsoft.Extensions.Options;

namespace JNotes.Core.Export;

/// <inheritdoc cref="INoteExportService"/>
public sealed class NoteExportService(
    INoteService notes,
    IImageService images,
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

        // GetAsync reads only the current user's folder: another user's id is a NoteNotFoundException,
        // which is the ownership check.
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
                if (await images.OpenAsync(imageName, cancellationToken) is { } image)
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
