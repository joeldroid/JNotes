namespace JNotes.UiTests.Infrastructure;

/// <summary>Whether Gotenberg answers <c>GET /health</c>; probed once at discovery, 2 s at most.</summary>
internal static class GotenbergProbe
{
    private static readonly Lazy<bool> Reachable = new(Probe);

    public static bool IsReachable => Reachable.Value;

    private static bool Probe()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = http.GetAsync(new Uri(new Uri(UiSettings.GotenbergUrl), "/health")).GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
