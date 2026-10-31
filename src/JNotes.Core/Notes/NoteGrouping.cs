namespace JNotes.Core.Notes;

/// <summary>
/// Buckets an already-sorted note list into relative-date sections for the default notes view.
/// Pure — no clock/timezone reads of its own; <paramref name="nowUtc"/>/<paramref name="timeZone"/>
/// are passed in (see <see cref="Time.IAppTimeZoneProvider"/>) so this is deterministic and easy to
/// test, the same reasoning <c>UserInitials</c> is a plain static class for.
/// </summary>
public static class NoteGrouping
{
    private const string Today = "Today";
    private const string Yesterday = "Yesterday";
    private const string ThisWeek = "This week";
    private const string LastWeek = "Last week";
    private const string ThisMonth = "This month";
    private const string Previous = "Previous";

    /// <summary>
    /// Groups <paramref name="notes"/> into Today/Yesterday/This week/Last week/This month/Previous,
    /// in that order, skipping empty buckets. Each bucket's notes keep whatever relative order they
    /// arrived in — this never re-sorts, so callers already ordering by <c>UpdatedAt</c> descending
    /// (see <c>PostgresNoteIndexRepository.SearchAsync</c>) get that order preserved within each bucket.
    /// Weeks start on Monday (ISO 8601) — this app has no per-user locale setting to key off instead.
    /// </summary>
    public static IReadOnlyList<NoteGroup> GroupByRelativeDate(
        IReadOnlyList<NoteSummary> notes, DateTimeOffset nowUtc, TimeZoneInfo timeZone)
    {
        var today = TimeZoneInfo.ConvertTime(nowUtc, timeZone).Date;
        var yesterday = today.AddDays(-1);
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)); // Monday on/before today
        var lastWeekStart = weekStart.AddDays(-7);
        var monthStart = new DateTime(today.Year, today.Month, 1);

        var buckets = new (string Label, List<NoteSummary> Notes)[]
        {
            (Today, []),
            (Yesterday, []),
            (ThisWeek, []),
            (LastWeek, []),
            (ThisMonth, []),
            (Previous, []),
        };

        foreach (var note in notes)
        {
            var date = TimeZoneInfo.ConvertTime(note.UpdatedAt, timeZone).Date;
            var index =
                date == today ? 0 :
                date == yesterday ? 1 :
                date >= weekStart ? 2 :
                date >= lastWeekStart ? 3 :
                date >= monthStart ? 4 :
                5;
            buckets[index].Notes.Add(note);
        }

        return buckets
            .Where(b => b.Notes.Count > 0)
            .Select(b => new NoteGroup(b.Label, b.Notes))
            .ToList();
    }
}
