using JNotes.Core.Export;

namespace JNotes.Tests.TestDoubles;

/// <summary>Serves a few known bytes for every path in <see cref="PdfExportAssets"/>, and nothing else.</summary>
internal sealed class StubPdfExportAssetSource : IPdfExportAssetSource
{
    public static readonly byte[] Bytes = [1, 2, 3, 4];

    public Stream? Open(string webRootRelativePath) =>
        webRootRelativePath == PdfExportAssets.MermaidScript || PdfExportAssets.Fonts.Contains(webRootRelativePath)
            ? new MemoryStream(Bytes)
            : null;
}
