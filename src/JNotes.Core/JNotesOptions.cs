namespace JNotes.Core;

/// <summary>
/// Populated from the flat "NOTES_PATH"/"POSTGRES_CONNECTION_STRING" configuration keys (see
/// <c>appsettings.json</c>, or the same-named env vars — <see cref="ServiceCollectionExtensions.AddJNotesCore"/>).
/// </summary>
public sealed class JNotesOptions
{
    /// <summary>Default notes folder used only until a "NotesFolderPath" setting exists in the database.</summary>
    public string NotesFolderPath { get; set; } = "notes";

    /// <summary>An IANA time zone id (e.g. "America/New_York") used to display UTC-stored timestamps. See <see cref="Time.IAppTimeZoneProvider"/>.</summary>
    public string TimeZoneId { get; set; } = "UTC";

    /// <summary>
    /// An Npgsql connection string (e.g. "Host=localhost;Port=5432;Database=jnotes;Username=jnotes;Password=…")
    /// for the single PostgreSQL database holding user accounts, app/user settings, and the note index.
    /// There is no fallback: a blank value fails startup loudly (see
    /// <see cref="ServiceCollectionExtensions.AddJNotesCore"/>) rather than starting with nowhere to
    /// write. Note the notes themselves never live here — they're markdown files under
    /// <see cref="NotesFolderPath"/>, and the NoteIndex* tables are a derived, rebuildable cache over
    /// them (see <see cref="Indexing.NoteIndexReconciler"/>).
    /// </summary>
    public string? PostgresConnectionString { get; set; }

    /// <summary>
    /// How often the background auto-indexing pass reconciles every user's index against their note
    /// files, in minutes. 0 disables it entirely (startup reconciliation and Settings -&gt; "Rescan
    /// Now" are unaffected). Populated from AUTO_INDEX_INTERVAL_MINUTES — see
    /// <see cref="Indexing.AutoIndexInterval.ParseMinutes"/>.
    /// </summary>
    public int AutoIndexIntervalMinutes { get; set; } = Indexing.AutoIndexInterval.DefaultMinutes;
}
