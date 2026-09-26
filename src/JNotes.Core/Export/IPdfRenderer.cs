namespace JNotes.Core.Export;

/// <summary>A sibling file sent beside <c>index.html</c>. The caller owns and disposes <see cref="Content"/>.</summary>
public sealed record PdfRenderAsset(string FileName, Stream Content);

/// <param name="IndexHtml">The self-contained document.</param>
/// <param name="FooterHtml">Optional page footer document.</param>
/// <param name="Assets">Flat sibling files the document references by bare name.</param>
/// <param name="WaitForExpression">Optional JS expression Chromium must see as true before printing.</param>
public sealed record PdfRenderRequest(
    string IndexHtml,
    string? FooterHtml,
    IReadOnlyList<PdfRenderAsset> Assets,
    string? WaitForExpression);

/// <summary>Turns an HTML document plus its assets into a PDF.</summary>
public interface IPdfRenderer
{
    /// <exception cref="PdfRenderingException">The PDF service was unreachable, timed out, or returned an error.</exception>
    Task<byte[]> RenderHtmlAsync(PdfRenderRequest request, CancellationToken cancellationToken = default);
}
