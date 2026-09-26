namespace JNotes.Core.Export;

/// <summary>
/// Populated from the flat "GOTENBERG_URL" configuration key — same env-var-style pattern as
/// <see cref="Users.OidcOptions"/> (see <see cref="ServiceCollectionExtensions.AddJNotesCore"/>).
/// </summary>
/// <remarks>
/// Lives in Core (not Web-only) deliberately: both <c>NoteView</c> (to decide whether to show the
/// "Export PDF" button) and <c>Program.cs</c> (to decide whether to map the endpoint) need the same
/// answer to "is PDF export enabled". Never log <see cref="GotenbergUrl"/>; it may contain credentials.
/// </remarks>
public sealed class PdfExportOptions
{
    public string? GotenbergUrl { get; set; }

    /// <summary><see cref="GotenbergUrl"/> as an absolute http(s) URI ending in <c>/</c>, or <c>null</c> when unset/invalid.</summary>
    public Uri? GotenbergBaseUri => ParseBaseUri(GotenbergUrl);

    /// <summary>PDF export is only offered when <c>GOTENBERG_URL</c> is a valid absolute http(s) URL.</summary>
    public bool IsEnabled => GotenbergBaseUri is not null;

    public static Uri? ParseBaseUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var text = uri.AbsoluteUri;
        return text.EndsWith('/') ? uri : new Uri(text + "/", UriKind.Absolute);
    }
}
