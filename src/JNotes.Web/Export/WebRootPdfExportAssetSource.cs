using JNotes.Core.Export;

namespace JNotes.Web.Export;

/// <summary>
/// Reads the PDF's fixed static assets (mermaid, Roboto) from Web's <c>wwwroot</c> — the Web half of
/// <see cref="IPdfExportAssetSource"/> (same Core-interface, Web-implementation split as <c>ICurrentUserContext</c>).
/// </summary>
internal sealed class WebRootPdfExportAssetSource(IWebHostEnvironment env) : IPdfExportAssetSource
{
    // Only the fixed PdfExportAssets paths ever reach this, never user input, and
    // PhysicalFileProvider already refuses paths that escape the web root ("..").
    public Stream? Open(string webRootRelativePath)
    {
        var file = env.WebRootFileProvider.GetFileInfo(webRootRelativePath);
        return file.Exists && !file.IsDirectory ? file.CreateReadStream() : null;
    }
}
