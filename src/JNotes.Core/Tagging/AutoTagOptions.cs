using System.Globalization;
using JNotes.Core.Export;

namespace JNotes.Core.Tagging;

/// <summary>
/// Populated from the flat "OLLAMA_URL"/"OLLAMA_MODEL"/"OLLAMA_TIMEOUT_SECONDS"/
/// "AUTO_TAG_INTERVAL_MINUTES"/"AUTO_TAG_BATCH_SIZE" configuration keys — same env-var-style pattern
/// as <see cref="PdfExportOptions"/> (see <see cref="ServiceCollectionExtensions.AddJNotesCore"/>).
/// There is deliberately no separate on/off flag: setting a valid <c>OLLAMA_URL</c> is the opt-in.
/// </summary>
/// <remarks>
/// Lives in Core because both Web's background tagger (whether to run) and <c>Program.cs</c>/Settings
/// (startup log, status card) need the same answer to "is auto-tagging enabled" via
/// <see cref="IsEnabled"/>. Note text is sent to <see cref="OllamaUrl"/>, so never log the URL.
/// </remarks>
public sealed class AutoTagOptions
{
    public const string DefaultModel = "phi4";

    public const int DefaultIntervalMinutes = 10;

    public const int MaxIntervalMinutes = 1440;

    public const int DefaultBatchSize = 10;

    public const int MaxBatchSize = 100;

    /// <summary>A 14B model on CPU, or a cold 9 GB model load, can exceed a minute.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    private const int MinTimeoutSeconds = 10;
    private const int MaxTimeoutSeconds = 600;

    public string? OllamaUrl { get; set; }

    public string Model { get; set; } = DefaultModel;

    public TimeSpan Timeout { get; set; } = DefaultTimeout;

    /// <summary>Minutes between background passes.</summary>
    public int IntervalMinutes { get; set; } = DefaultIntervalMinutes;

    /// <summary>Maximum notes tagged per pass, across all users.</summary>
    public int BatchSize { get; set; } = DefaultBatchSize;

    /// <summary><see cref="OllamaUrl"/> as an absolute http(s) URI ending in <c>/</c>, or <c>null</c> when unset/invalid.</summary>
    public Uri? OllamaBaseUri => PdfExportOptions.ParseBaseUri(OllamaUrl); // the absolute-http(s)-plus-trailing-slash rule is shared

    /// <summary>Auto-tagging runs only when the URL is a valid absolute http(s) URL and a model is named.</summary>
    public bool IsEnabled => OllamaBaseUri is not null && !string.IsNullOrWhiteSpace(Model);

    /// <summary>Whole seconds, clamped to 10..600; <see cref="DefaultTimeout"/> for a missing or invalid value.</summary>
    public static TimeSpan ParseTimeout(string? value) =>
        int.TryParse(value, out var seconds)
            ? TimeSpan.FromSeconds(Math.Clamp(seconds, MinTimeoutSeconds, MaxTimeoutSeconds))
            : DefaultTimeout;

    /// <summary>Minutes clamped to 1..1440; <see cref="DefaultIntervalMinutes"/> for a missing, invalid or non-positive value.</summary>
    public static int ParseIntervalMinutes(string? value) => ParsePositive(value, DefaultIntervalMinutes, MaxIntervalMinutes);

    /// <summary>Notes per pass clamped to 1..100; <see cref="DefaultBatchSize"/> for a missing, invalid or non-positive value.</summary>
    public static int ParseBatchSize(string? value) => ParsePositive(value, DefaultBatchSize, MaxBatchSize);

    private static int ParsePositive(string? value, int defaultValue, int max) =>
        int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? Math.Clamp(parsed, 1, max)
            : defaultValue;
}
