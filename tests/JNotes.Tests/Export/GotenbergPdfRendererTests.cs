using System.Net;
using System.Text;
using JNotes.Core.Export;
using Microsoft.Extensions.Logging.Abstractions;

namespace JNotes.Tests.Export;

public class GotenbergPdfRendererTests
{
    private static readonly byte[] PdfBytes = "%PDF-1.7 fake"u8.ToArray();

    private static GotenbergPdfRenderer Create(HttpMessageHandler handler, TimeSpan? timeout = null, bool withBaseAddress = true)
    {
        var client = new HttpClient(handler);
        if (withBaseAddress)
        {
            client.BaseAddress = new Uri("http://gotenberg.test:3000/");
        }

        if (timeout is { } t)
        {
            client.Timeout = t;
        }

        return new GotenbergPdfRenderer(client, NullLogger<GotenbergPdfRenderer>.Instance);
    }

    private static PdfRenderRequest Request(string? footer = "<footer/>", string? wait = NotePdfDocumentBuilder.ReadyExpression, params PdfRenderAsset[] assets) =>
        new("<html>doc</html>", footer, assets, wait);

    [Fact]
    public async Task Posts_a_multipart_request_to_the_chromium_html_route_and_returns_the_pdf()
    {
        var handler = new FakeHandler();
        var renderer = Create(handler);
        var asset = new PdfRenderAsset("font.woff2", new MemoryStream([9, 8, 7]));

        var bytes = await renderer.RenderHtmlAsync(Request(assets: asset));

        Assert.Equal(PdfBytes, bytes);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("http://gotenberg.test:3000/forms/chromium/convert/html", handler.Uri);

        var files = handler.Parts.Where(p => p.Name == "files").ToList();
        Assert.Equal(["index.html", "footer.html", "font.woff2"], files.Select(f => f.FileName));
        Assert.Equal("<html>doc</html>", Encoding.UTF8.GetString(files[0].Content));
        Assert.Equal("<footer/>", Encoding.UTF8.GetString(files[1].Content));
        Assert.Equal(new byte[] { 9, 8, 7 }, files[2].Content);

        Assert.Equal("8.27", Field(handler, "paperWidth"));
        Assert.Equal("11.7", Field(handler, "paperHeight"));
        Assert.Equal("true", Field(handler, "printBackground"));
        Assert.Equal("window.jnotesPdfReady === true", Field(handler, "waitForExpression"));
    }

    [Fact]
    public async Task Omits_the_footer_and_wait_expression_when_not_given()
    {
        var handler = new FakeHandler();

        await Create(handler).RenderHtmlAsync(Request(footer: null, wait: null));

        Assert.DoesNotContain(handler.Parts, p => p.FileName == "footer.html");
        Assert.DoesNotContain(handler.Parts, p => p.Name == "waitForExpression");
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task A_non_success_response_throws_PdfRenderingException(HttpStatusCode status)
    {
        var handler = new FakeHandler { Status = status };

        await Assert.ThrowsAsync<PdfRenderingException>(() => Create(handler).RenderHtmlAsync(Request()));
    }

    [Fact]
    public async Task A_connection_failure_throws_PdfRenderingException()
    {
        var handler = new FakeHandler { Throw = new HttpRequestException("refused") };

        await Assert.ThrowsAsync<PdfRenderingException>(() => Create(handler).RenderHtmlAsync(Request()));
    }

    [Fact]
    public async Task A_timeout_throws_PdfRenderingException()
    {
        var handler = new FakeHandler { Delay = TimeSpan.FromSeconds(5) };

        await Assert.ThrowsAsync<PdfRenderingException>(() =>
            Create(handler, timeout: TimeSpan.FromMilliseconds(100)).RenderHtmlAsync(Request()));
    }

    [Fact]
    public async Task A_cancelled_caller_token_propagates_unwrapped()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create(new FakeHandler()).RenderHtmlAsync(Request(), cts.Token));
    }

    [Fact]
    public async Task A_missing_base_address_throws_InvalidOperationException()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create(new FakeHandler(), withBaseAddress: false).RenderHtmlAsync(Request()));
    }

    private static string? Field(FakeHandler handler, string name) =>
        handler.Parts.SingleOrDefault(p => p.Name == name) is { } part ? Encoding.UTF8.GetString(part.Content) : null;

    private sealed record Part(string Name, string? FileName, byte[] Content);

    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        public Exception? Throw { get; init; }

        public TimeSpan? Delay { get; init; }

        public HttpMethod? Method { get; private set; }

        public string? Uri { get; private set; }

        public List<Part> Parts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); // like a real handler
            Method = request.Method;
            Uri = request.RequestUri?.ToString();

            if (Throw is not null)
            {
                throw Throw;
            }

            if (Delay is { } delay)
            {
                await Task.Delay(delay, cancellationToken);
            }

            // Read the multipart body here: the renderer disposes the content once the call returns.
            foreach (var part in (MultipartFormDataContent)request.Content!)
            {
                Parts.Add(new Part(
                    part.Headers.ContentDisposition!.Name!.Trim('"'),
                    part.Headers.ContentDisposition.FileName?.Trim('"'),
                    await part.ReadAsByteArrayAsync(cancellationToken)));
            }

            return new HttpResponseMessage(Status) { Content = new ByteArrayContent(PdfBytes) };
        }
    }
}
