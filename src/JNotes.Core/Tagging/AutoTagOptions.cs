using JNotes.Core.Export;

namespace JNotes.Core.Tagging;

/// <summary>
/// Populated from the flat "AUTO_TAG_ENABLED"/"OLLAMA_URL"/"OLLAMA_MODEL"/"OLLAMA_TIMEOUT_SECONDS"
/// configuration keys — same env-var-style pattern as <see cref="PdfExportOptions"/> (see
/// <see cref="ServiceCollectionExtensions.AddJNotesCore"/>).
/// </summary>
/// <remarks>
/// Lives in Core because both <c>NoteEditor</c> (to decide whether to show the "Auto-tag" button) and
/// <c>Program.cs</c> (startup log) need the same answer to "is auto-tagging enabled". Note text is sent
/// to <see cref="OllamaUrl"/>, so never log it.
/// </remarks>
public sealed class AutoTagOptions
{
    public const string DefaultModel = "phi4";

    /// <summary>A 14B model on CPU, or a cold 9 GB model load, can exceed a minute.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    private const int MinTimeoutSeconds = 10;
    private const int MaxTimeoutSeconds = 600;

    /// <summary>The <c>AUTO_TAG_ENABLED</c> flag alone — see <see cref="IsEnabled"/> for the effective answer.</summary>
    public bool Enabled { get; set; }

    public string? OllamaUrl { get; set; }

    public string Model { get; set; } = DefaultModel;

    public TimeSpan Timeout { get; set; } = DefaultTimeout;

    /// <summary><see cref="OllamaUrl"/> as an absolute http(s) URI ending in <c>/</c>, or <c>null</c> when unset/invalid.</summary>
    public Uri? OllamaBaseUri => PdfExportOptions.ParseBaseUri(OllamaUrl); // the absolute-http(s)-plus-trailing-slash rule is shared

    /// <summary>Auto-tagging is only offered when the flag is on, the URL is a valid absolute http(s) URL and a model is named.</summary>
    public bool IsEnabled => Enabled && OllamaBaseUri is not null && !string.IsNullOrWhiteSpace(Model);

    /// <summary>True for <c>true</c>/<c>1</c>/<c>yes</c>/<c>on</c> (trimmed, case-insensitive); anything else is off.</summary>
    public static bool ParseFlag(string? value) =>
        value?.Trim() is { } v
        && (v.Equals("true", StringComparison.OrdinalIgnoreCase)
            || v.Equals("1", StringComparison.OrdinalIgnoreCase)
            || v.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || v.Equals("on", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whole seconds, clamped to 10..600; <see cref="DefaultTimeout"/> for a missing or invalid value.</summary>
    public static TimeSpan ParseTimeout(string? value) =>
        int.TryParse(value, out var seconds)
            ? TimeSpan.FromSeconds(Math.Clamp(seconds, MinTimeoutSeconds, MaxTimeoutSeconds))
            : DefaultTimeout;
}
