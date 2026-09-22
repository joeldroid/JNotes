namespace JNotes.Core.Users;

/// <summary>
/// Populated from the flat "OIDC_AUTHORITY"/"OIDC_CLIENT_ID"/"OIDC_CLIENT_SECRET"/"OIDC_DISPLAY_NAME"
/// configuration keys — same env-var-style pattern <see cref="JNotesOptions"/> already uses for
/// "NOTES_PATH"/"POSTGRES_CONNECTION_STRING"/"TZ" (see <see cref="ServiceCollectionExtensions.AddJNotesCore"/>).
/// </summary>
/// <remarks>
/// Lives in Core (not Web-only) deliberately: both <c>SettingsService</c> (to refuse disabling
/// password auth when OIDC isn't configured) and <c>JNotes.Web</c>'s auth wiring/login page (to
/// decide whether to register the OIDC handler / show the "sign in with OIDC" button) need the same
/// answer to "is OIDC configured" — one options object read once at startup serves both, rather than
/// inventing a second config mechanism just for Web. Never log <see cref="ClientSecret"/>.
/// </remarks>
public sealed class OidcOptions
{
    public string Authority { get; set; } = "";

    public string ClientId { get; set; } = "";

    public string ClientSecret { get; set; } = "";

    public string DisplayName { get; set; } = "OIDC";

    /// <summary>OIDC login is only offered when all three of Authority/ClientId/ClientSecret are set.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Authority) &&
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret);
}
