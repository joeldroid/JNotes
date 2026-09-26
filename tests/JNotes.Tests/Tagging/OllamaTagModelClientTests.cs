using System.Net;
using System.Text;
using System.Text.Json;
using JNotes.Core.Tagging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JNotes.Tests.Tagging;

public class OllamaTagModelClientTests
{
    private const string OkBody = """{"model":"phi4","message":{"role":"assistant","content":"{\"tags\":[\"alpha\",\"beta\"]}"},"done":true}""";

    private static readonly AutoTagPrompt Prompt = new("system text", "user text");

    private static OllamaTagModelClient Create(HttpMessageHandler handler, TimeSpan? timeout = null, bool withBaseAddress = true)
    {
        var client = new HttpClient(handler);
        if (withBaseAddress)
        {
            client.BaseAddress = new Uri("http://ollama.test:11434/");
        }

        if (timeout is { } t)
        {
            client.Timeout = t;
        }

        return new OllamaTagModelClient(
            client,
            Options.Create(new AutoTagOptions { Enabled = true, OllamaUrl = "http://ollama.test:11434" }),
            NullLogger<OllamaTagModelClient>.Instance);
    }

    [Fact]
    public async Task Posts_a_non_streaming_chat_request_with_a_schema_and_returns_the_tags()
    {
        var handler = new FakeHandler { Body = OkBody };

        var tags = await Create(handler).GenerateTagsAsync(Prompt);

        Assert.Equal(["alpha", "beta"], tags);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("http://ollama.test:11434/api/chat", handler.Uri);

        using var json = JsonDocument.Parse(handler.RequestJson!);
        var root = json.RootElement;
        Assert.Equal("phi4", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal("object", root.GetProperty("format").GetProperty("type").GetString());
        Assert.Equal(8192, root.GetProperty("options").GetProperty("num_ctx").GetInt32());

        var messages = root.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(2, messages.Count);
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("system text", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("user text", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task A_missing_model_gives_a_pull_hint()
    {
        var handler = new FakeHandler
        {
            Status = HttpStatusCode.NotFound,
            Body = """{"error":"model \"phi4\" not found, try pulling it first"}""",
        };

        var ex = await Assert.ThrowsAsync<AutoTagException>(() => Create(handler).GenerateTagsAsync(Prompt));

        Assert.Contains("ollama pull phi4", ex.Message);
    }

    [Fact]
    public async Task A_server_error_throws_AutoTagException()
    {
        var handler = new FakeHandler { Status = HttpStatusCode.InternalServerError, Body = """{"error":"boom"}""" };

        var ex = await Assert.ThrowsAsync<AutoTagException>(() => Create(handler).GenerateTagsAsync(Prompt));

        Assert.Contains("500", ex.Message);
    }

    [Fact]
    public async Task A_connection_failure_throws_AutoTagException()
    {
        var handler = new FakeHandler { Throw = new HttpRequestException("refused") };

        await Assert.ThrowsAsync<AutoTagException>(() => Create(handler).GenerateTagsAsync(Prompt));
    }

    [Fact]
    public async Task A_timeout_throws_AutoTagException()
    {
        var handler = new FakeHandler { Delay = TimeSpan.FromSeconds(5), Body = OkBody };

        await Assert.ThrowsAsync<AutoTagException>(() =>
            Create(handler, timeout: TimeSpan.FromMilliseconds(100)).GenerateTagsAsync(Prompt));
    }

    [Fact]
    public async Task A_cancelled_caller_token_propagates_unwrapped()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create(new FakeHandler { Body = OkBody }).GenerateTagsAsync(Prompt, cts.Token));
    }

    [Fact]
    public async Task A_missing_base_address_throws_InvalidOperationException()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create(new FakeHandler(), withBaseAddress: false).GenerateTagsAsync(Prompt));
    }

    [Theory]
    [InlineData("""{"message":{"role":"assistant","content":"sorry, I can't help with that"}}""")]
    [InlineData("""{"message":{"role":"assistant","content":"{\"foo\":1}"}}""")]
    [InlineData("""{"done":true}""")]
    [InlineData("this is not json")]
    public async Task An_unusable_reply_throws_AutoTagException(string body)
    {
        var handler = new FakeHandler { Body = body };

        await Assert.ThrowsAsync<AutoTagException>(() => Create(handler).GenerateTagsAsync(Prompt));
    }

    [Fact]
    public async Task A_200_carrying_an_error_field_throws_AutoTagException()
    {
        var handler = new FakeHandler { Body = """{"error":"x"}""" };

        await Assert.ThrowsAsync<AutoTagException>(() => Create(handler).GenerateTagsAsync(Prompt));
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        public string Body { get; init; } = "";

        public Exception? Throw { get; init; }

        public TimeSpan? Delay { get; init; }

        public HttpMethod? Method { get; private set; }

        public string? Uri { get; private set; }

        public string? RequestJson { get; private set; }

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

            // Read the body here: the client disposes the request once the call returns.
            RequestJson = await request.Content!.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
        }
    }
}
