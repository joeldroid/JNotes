using JNotes.Core.Export;

namespace JNotes.Tests.TestDoubles;

/// <summary>
/// Stands in for <see cref="IPdfRenderer"/> (no Gotenberg in tests) and records what it was asked to render.
/// Copies each asset stream to bytes during the call, because <c>NoteExportService</c> disposes them afterwards.
/// </summary>
internal sealed class RecordingPdfRenderer : IPdfRenderer
{
    public static readonly byte[] Output = "%PDF-1.7 test"u8.ToArray();

    public PdfRenderRequest? LastRequest { get; private set; }

    public int CallCount { get; private set; }

    public Dictionary<string, byte[]> AssetBytes { get; } = [];

    public async Task<byte[]> RenderHtmlAsync(PdfRenderRequest request, CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastRequest = request;
        AssetBytes.Clear();
        foreach (var asset in request.Assets)
        {
            using var copy = new MemoryStream();
            await asset.Content.CopyToAsync(copy, cancellationToken);
            AssetBytes[asset.FileName] = copy.ToArray();
        }

        return Output;
    }
}
