using System.Text.Json;
using System.Text.RegularExpressions;

namespace JNotes.Core.Tagging;

/// <summary>
/// Pulls the raw tag strings out of the model's reply. Pure — no normalisation (that's
/// <see cref="AutoTagNormalizer"/>). Tolerates a markdown-fenced reply and a bare array, because an
/// older Ollama ignores the JSON-schema <c>format</c> and models sometimes fence their JSON anyway.
/// </summary>
public static partial class AutoTagResponseParser
{
    private const string UnexpectedResponse = "The tagging model returned an unexpected response.";

    [GeneratedRegex(@"^```(?:json)?\s*(?<json>.*?)\s*```$", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex FenceRegex();

    public static IReadOnlyList<string> Parse(string? content)
    {
        var text = content?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            throw new AutoTagException(UnexpectedResponse);
        }

        if (FenceRegex().Match(text) is { Success: true } fence)
        {
            text = fence.Groups["json"].Value;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("tags", out var tags)
                && tags.ValueKind == JsonValueKind.Array)
            {
                return Strings(tags);
            }

            if (root.ValueKind == JsonValueKind.Array)
            {
                return Strings(root);
            }
        }
        catch (JsonException ex)
        {
            throw new AutoTagException(UnexpectedResponse, ex);
        }

        throw new AutoTagException(UnexpectedResponse);
    }

    private static List<string> Strings(JsonElement array) =>
        array.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToList();
}
