using System.Globalization;

namespace JNotes.UiTests.Infrastructure;

/// <summary>
/// The suite's own switches. None of these are app configuration: the app never reads a JNOTES_UI_* variable.
/// </summary>
internal static class UiSettings
{
    /// <summary>The opt-in: without <c>JNOTES_UI_TESTS=1</c> every UI test reports Skipped and nothing is started.</summary>
    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("JNOTES_UI_TESTS") == "1";

    /// <summary>Show the browser window (debugging).</summary>
    public static bool Headed { get; } = Environment.GetEnvironmentVariable("JNOTES_UI_HEADED") == "1";

    /// <summary>Milliseconds Playwright waits between actions (watching a run).</summary>
    public static float SlowMo { get; } =
        float.TryParse(Environment.GetEnvironmentVariable("JNOTES_UI_SLOWMO"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) ? ms : 0;

    /// <summary>The Gotenberg the app is pointed at (the PDF test skips when it doesn't answer <c>/health</c>).</summary>
    public static string GotenbergUrl { get; } =
        Environment.GetEnvironmentVariable("JNOTES_UI_GOTENBERG_URL") is { Length: > 0 } url ? url : "http://localhost:3000";

    /// <summary>Where failure screenshots, traces and problem lists are written (default: under bin/, which is gitignored).</summary>
    public static string ArtifactsPath { get; } =
        Environment.GetEnvironmentVariable("JNOTES_UI_ARTIFACTS") is { Length: > 0 } path
            ? path
            : Path.Combine(AppContext.BaseDirectory, "ui-artifacts");

    /// <summary>Don't run <c>playwright install chromium</c> at startup (browsers were installed another way).</summary>
    public static bool SkipBrowserInstall { get; } = Environment.GetEnvironmentVariable("JNOTES_UI_SKIP_BROWSER_INSTALL") == "1";
}
