using System.Globalization;

namespace JNotes.Core.Indexing;

/// <summary>
/// Parses/clamps the "AUTO_INDEX_INTERVAL_MINUTES" env var into a value safe to feed a
/// <see cref="PeriodicTimer"/> — a plain static class, no DI, same shape as
/// <see cref="Users.UserValidation"/>/<c>NoteGrouping</c>/<c>UserInitials</c>, so the config
/// semantics are unit-testable without a host, a timer, or a database.
/// </summary>
public static class AutoIndexInterval
{
    public const int DefaultMinutes = 5;

    public const int MinMinutes = 1;

    public const int MaxMinutes = 10_080; // 7 days; PeriodicTimer's own period ceiling is ~49.7 days.

    public const int DisabledMinutes = 0;

    /// <summary>
    /// Null/blank/unparseable -&gt; <see cref="DefaultMinutes"/>; &lt;= 0 -&gt;
    /// <see cref="DisabledMinutes"/>; otherwise clamped to [<see cref="MinMinutes"/>,
    /// <see cref="MaxMinutes"/>].
    /// </summary>
    public static int ParseMinutes(string? rawValue)
    {
        if (!int.TryParse(rawValue?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
        {
            return DefaultMinutes;
        }

        if (minutes <= 0)
        {
            return DisabledMinutes;
        }

        return Math.Clamp(minutes, MinMinutes, MaxMinutes);
    }

    public static bool IsEnabled(int minutes) => minutes > 0;
}
