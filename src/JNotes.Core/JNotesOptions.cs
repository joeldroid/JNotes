namespace JNotes.Core;

/// <summary>
/// Populated from the flat "NOTES_PATH"/"DB_PATH" configuration keys (see
/// <c>appsettings.json</c>, or the same-named env vars — <see cref="ServiceCollectionExtensions.AddJNotesCore"/>).
/// </summary>
public sealed class JNotesOptions
{
    /// <summary>Default notes folder used only until a "NotesFolderPath" setting exists in the database.</summary>
    public string NotesFolderPath { get; set; } = "notes";

    public string DbPath { get; set; } = "jnotes.db";

    /// <summary>An IANA time zone id (e.g. "America/New_York") used to display UTC-stored timestamps. See <see cref="Time.IAppTimeZoneProvider"/>.</summary>
    public string TimeZoneId { get; set; } = "UTC";

    /// <summary>
    /// A StackExchange.Redis connection string (e.g. "localhost:6379") backing the note index — see
    /// <see cref="Indexing.RedisNoteIndexRepository"/>. Unlike <see cref="DbPath"/>, there's no
    /// built-in fallback: the index has nowhere else to live, so a missing value fails startup
    /// loudly instead of silently falling back to something that isn't there.
    /// </summary>
    public string? RedisConnectionString { get; set; }

    /// <summary>
    /// How often the background auto-indexing pass reconciles every user's index against their note
    /// files, in minutes. 0 disables it entirely (startup reconciliation and Settings -&gt; "Rescan
    /// Now" are unaffected). Populated from AUTO_INDEX_INTERVAL_MINUTES — see
    /// <see cref="Indexing.AutoIndexInterval.ParseMinutes"/>.
    /// </summary>
    public int AutoIndexIntervalMinutes { get; set; } = Indexing.AutoIndexInterval.DefaultMinutes;
}
