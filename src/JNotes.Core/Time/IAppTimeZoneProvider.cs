namespace JNotes.Core.Time;

/// <summary>
/// Resolves the time zone note timestamps are displayed in, from the "TZ" env var (see
/// <see cref="JNotesOptions.TimeZoneId"/>). Storage stays UTC regardless (see
/// <c>Note.CreatedAt</c>/<c>UpdatedAt</c> and the front matter schema) — this only affects display.
/// </summary>
public interface IAppTimeZoneProvider
{
    TimeZoneInfo TimeZone { get; }

    /// <summary>Converts a UTC-based <see cref="DateTimeOffset"/> into the configured display time zone.</summary>
    DateTimeOffset ToAppTimeZone(DateTimeOffset utcValue);
}
