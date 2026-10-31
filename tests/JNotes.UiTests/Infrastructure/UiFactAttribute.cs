namespace JNotes.UiTests.Infrastructure;

internal static class UiSkip
{
    public const string NotEnabled = "UI tests are opt-in: JNOTES_UI_TESTS=1 dotnet test tests/JNotes.UiTests";
    public const int TimeoutMilliseconds = 180_000; // honoured because xunit.runner.json disables parallelization
}

/// <summary>A browser test: Skipped unless <c>JNOTES_UI_TESTS=1</c>.</summary>
public class UiFactAttribute : FactAttribute
{
    public UiFactAttribute()
    {
        if (!UiSettings.Enabled)
        {
            Skip = UiSkip.NotEnabled;
        }

        Timeout = UiSkip.TimeoutMilliseconds;
    }
}

/// <summary><see cref="UiFactAttribute"/> for a data-driven browser test.</summary>
public sealed class UiTheoryAttribute : TheoryAttribute
{
    public UiTheoryAttribute()
    {
        if (!UiSettings.Enabled)
        {
            Skip = UiSkip.NotEnabled;
        }

        Timeout = UiSkip.TimeoutMilliseconds;
    }
}

/// <summary>A browser test that also needs Gotenberg; Skipped (naming the URL) when it isn't reachable.</summary>
public sealed class UiGotenbergFactAttribute : UiFactAttribute
{
    public UiGotenbergFactAttribute()
    {
        if (Skip is null && !GotenbergProbe.IsReachable)
        {
            Skip = $"Gotenberg isn't reachable at {UiSettings.GotenbergUrl} (GET /health). Start jnotes-debug-pdf or set JNOTES_UI_GOTENBERG_URL.";
        }
    }
}
