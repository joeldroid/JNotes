using System.Text;
using Microsoft.Extensions.Logging;

namespace JNotes.Core.Export;

/// <summary>
/// Renders through Gotenberg's Chromium HTML route: <c>index.html</c> plus flat sibling files in one
/// multipart request (all under the form field <c>files</c>). A typed <see cref="HttpClient"/> whose
/// <see cref="HttpClient.BaseAddress"/> comes from <see cref="PdfExportOptions"/>.
/// </summary>
public sealed class GotenbergPdfRenderer(HttpClient http, ILogger<GotenbergPdfRenderer> logger) : IPdfRenderer
{
    /// <summary>Longer than Gotenberg's own 30 s API timeout, so Gotenberg normally answers 503 first.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private const string ConvertRoute = "forms/chromium/convert/html";

    public async Task<byte[]> RenderHtmlAsync(PdfRenderRequest request, CancellationToken cancellationToken = default)
    {
        if (http.BaseAddress is null)
        {
            throw new InvalidOperationException("PDF export is not configured (GOTENBERG_URL).");
        }

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(request.IndexHtml, Encoding.UTF8, "text/html"), "files", "index.html");
        if (request.FooterHtml is not null)
        {
            form.Add(new StringContent(request.FooterHtml, Encoding.UTF8, "text/html"), "files", "footer.html");
        }

        foreach (var asset in request.Assets)
        {
            form.Add(new StreamContent(asset.Content), "files", asset.FileName);
        }

        // A4 in inches (Gotenberg's default unit); the bottom margin leaves room for the footer.
        form.Add(new StringContent("8.27"), "paperWidth");
        form.Add(new StringContent("11.7"), "paperHeight");
        form.Add(new StringContent("0.6"), "marginTop");
        form.Add(new StringContent("0.8"), "marginBottom");
        form.Add(new StringContent("0.6"), "marginLeft");
        form.Add(new StringContent("0.6"), "marginRight");
        form.Add(new StringContent("true"), "printBackground");
        if (request.WaitForExpression is not null)
        {
            form.Add(new StringContent(request.WaitForExpression), "waitForExpression");
        }

        try
        {
            using var response = await http.PostAsync(ConvertRoute, form, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var snippet = await ReadSnippetAsync(response, cancellationToken);
                var trace = response.Headers.TryGetValues("Gotenberg-Trace", out var values) ? string.Join(",", values) : "";
                logger.LogWarning(
                    "Gotenberg returned {StatusCode} (trace {Trace}): {Body}", (int)response.StatusCode, trace, snippet);
                throw new PdfRenderingException($"The PDF service returned {(int)response.StatusCode}.");
            }

            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Gotenberg couldn't be reached.");
            throw new PdfRenderingException("The PDF service couldn't be reached.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Gotenberg timed out.");
            throw new PdfRenderingException("The PDF service timed out.", ex);
        }
    }

    private static async Task<string> ReadSnippetAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return body.Length > 500 ? body[..500] : body;
        }
        catch (HttpRequestException)
        {
            return "";
        }
    }
}
