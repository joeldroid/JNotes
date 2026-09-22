namespace JNotes.Core;

/// <summary>
/// Populated from the flat "NOTES_PATH"/"POSTGRES_HOST"/etc. configuration keys (see
/// <c>appsettings.json</c>, or the same-named env vars — <see cref="ServiceCollectionExtensions.AddJNotesCore"/>).
/// </summary>
public sealed class JNotesOptions
{
    /// <summary>Default notes folder used only until a "NotesFolderPath" setting exists in the database.</summary>
    public string NotesFolderPath { get; set; } = "notes";

    /// <summary>An IANA time zone id (e.g. "America/New_York") used to display UTC-stored timestamps. See <see cref="Time.IAppTimeZoneProvider"/>.</summary>
    public string TimeZoneId { get; set; } = "UTC";

    /// <summary>PostgreSQL host, from "POSTGRES_HOST". Combined with <see cref="PostgresPort"/>/<see cref="PostgresDatabase"/>/<see cref="PostgresUsername"/>/<see cref="PostgresPassword"/> into <see cref="PostgresConnectionString"/> — see <see cref="ServiceCollectionExtensions.AddJNotesCore"/>.</summary>
    public string? PostgresHost { get; set; }

    /// <summary>PostgreSQL port, from "POSTGRES_PORT". Defaults to 5432 when unset.</summary>
    public int? PostgresPort { get; set; }

    /// <summary>PostgreSQL database name, from "POSTGRES_DB".</summary>
    public string? PostgresDatabase { get; set; }

    /// <summary>PostgreSQL username, from "POSTGRES_USER".</summary>
    public string? PostgresUsername { get; set; }

    /// <summary>PostgreSQL password, from "POSTGRES_PASSWORD".</summary>
    public string? PostgresPassword { get; set; }

    /// <summary>
    /// The Npgsql connection string for the single PostgreSQL database holding user accounts,
    /// app/user settings, and the note index — assembled from <see cref="PostgresHost"/>/
    /// <see cref="PostgresPort"/>/<see cref="PostgresDatabase"/>/<see cref="PostgresUsername"/>/
    /// <see cref="PostgresPassword"/> rather than read directly, so docker-compose setups can share
    /// plain host/port/db/user/password values with the `postgres` service instead of hand-building
    /// a connection string. Tests set this directly instead (see <c>CoreServicesFixture</c>), since
    /// they need a per-test database name a fixed set of parts can't express.
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
