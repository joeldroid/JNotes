using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JNotes.Core.Tagging;

/// <summary>
/// Calls Ollama's non-streaming <c>POST /api/chat</c> with a JSON-schema <c>format</c>, so the reply is
/// (on a current Ollama) always <c>{"tags": [...]}</c>. A typed <see cref="HttpClient"/> whose
/// <see cref="HttpClient.BaseAddress"/> and timeout come from <see cref="AutoTagOptions"/>. Its caller is
/// the background tagger, which reports state changes itself, so everything here logs at Debug — and
/// never the prompt, the note content, the URL or an exception object (its message can carry host:port),
/// only the model name and status codes. Failures are classified with <see cref="AutoTagFailureKind"/>.
/// </summary>
public sealed class OllamaTagModelClient(
    HttpClient http,
    IOptions<AutoTagOptions> options,
    ILogger<OllamaTagModelClient> logger) : ITagModelClient
{
    private const string ChatRoute = "api/chat";

    private const string TagsSchema =
        """{"type":"object","properties":{"tags":{"type":"array","items":{"type":"string"},"maxItems":8}},"required":["tags"]}""";

    private const string UnexpectedResponse = "The tagging model returned an unexpected response.";

    public async Task<IReadOnlyList<string>> GenerateTagsAsync(AutoTagPrompt prompt, CancellationToken cancellationToken = default)
    {
        if (http.BaseAddress is null)
        {
            throw new InvalidOperationException("Auto-tagging is not configured (OLLAMA_URL).");
        }

        var model = options.Value.Model;
        var request = new ChatRequest(
            model,
            [new ChatMessage("system", prompt.SystemMessage), new ChatMessage("user", prompt.UserMessage)],
            Stream: false,
            Format: JsonNode.Parse(TagsSchema),
            // An explicit num_ctx, because Ollama's default context may silently cut a long prompt.
            Options: new ChatOptions(Temperature: 0.2, NumCtx: 8192, NumPredict: 256));

        try
        {
            using var response = await http.PostAsJsonAsync(ChatRoute, request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorAsync(response, cancellationToken);
                logger.LogDebug(
                    "Ollama returned {StatusCode} for model {Model}: {Error}", (int)response.StatusCode, model, error);

                // Ollama answers 404 with "model '…' not found" when it hasn't been pulled.
                if (response.StatusCode == HttpStatusCode.NotFound
                    && error.Contains("model", StringComparison.OrdinalIgnoreCase))
                {
                    throw new AutoTagException(
                        $"The tagging model '{model}' isn't available on the Ollama server (run `ollama pull {model}`).",
                        AutoTagFailureKind.Unavailable);
                }

                // 5xx (incl. 503 "overloaded") and 429 are the server's state, not the note's; another 4xx means this request was refused.
                var kind = (int)response.StatusCode >= 500 || (int)response.StatusCode == 429
                    ? AutoTagFailureKind.Unavailable
                    : AutoTagFailureKind.BadResponse;
                throw new AutoTagException($"The tagging service returned {(int)response.StatusCode}.", kind);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var chat = JsonSerializer.Deserialize<ChatResponse>(json);
            if (chat?.Error is { Length: > 0 } modelError)
            {
                logger.LogDebug("Ollama reported an error for model {Model}: {Error}", model, Snippet(modelError));
                throw new AutoTagException("The tagging service returned an error.", AutoTagFailureKind.BadResponse);
            }

            return AutoTagResponseParser.Parse(chat?.Message?.Content);
        }
        catch (JsonException ex)
        {
            logger.LogDebug("Ollama's reply for model {Model} wasn't valid JSON.", model);
            throw new AutoTagException(UnexpectedResponse, AutoTagFailureKind.BadResponse, ex);
        }
        catch (HttpRequestException ex)
        {
            // Not the exception itself: its message includes host:port.
            logger.LogDebug("Ollama couldn't be reached ({Error}).", ex.HttpRequestError);
            throw new AutoTagException("The tagging service couldn't be reached.", AutoTagFailureKind.Unavailable, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Ollama timed out.");
            throw new AutoTagException("The tagging service timed out.", AutoTagFailureKind.Timeout, ex);
        }
    }

    // The "error" field of Ollama's {"error": "..."} body when present, else the raw body — capped either way.
    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            try
            {
                if (JsonSerializer.Deserialize<ChatResponse>(body)?.Error is { Length: > 0 } error)
                {
                    return Snippet(error);
                }
            }
            catch (JsonException)
            {
                // Not JSON — fall through to the raw body.
            }

            return Snippet(body);
        }
        catch (HttpRequestException)
        {
            return "";
        }
    }

    private static string Snippet(string text) => text.Length > 500 ? text[..500] : text;

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] ChatMessage[] Messages,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("format")] JsonNode? Format,
        [property: JsonPropertyName("options")] ChatOptions Options);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatOptions(
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("num_ctx")] int NumCtx,
        [property: JsonPropertyName("num_predict")] int NumPredict);

    private sealed record ChatResponse(
        [property: JsonPropertyName("message")] ResponseMessage? Message,
        [property: JsonPropertyName("error")] string? Error);

    private sealed record ResponseMessage([property: JsonPropertyName("content")] string? Content);
}
