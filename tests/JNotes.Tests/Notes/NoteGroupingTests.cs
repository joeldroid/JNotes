using JNotes.Core.Models;
using JNotes.Core.Notes;

namespace JNotes.Tests.Notes;

public class NoteGroupingTests
{
    private static NoteSummary NoteAt(DateTimeOffset updatedAt, string title = "note") =>
        new(Guid.NewGuid(), title, [], updatedAt, updatedAt);

    /// <summary>
    /// A reference "now", chosen fresh from the real clock but nudged onto a Wednesday, so the
    /// start of the current week (Monday) always lands at least two days before "today" —
    /// otherwise a test run on a Monday or Tuesday could make "This week"'s boundary collide with
    /// "Today"/"Yesterday" and produce a flaky, date-dependent false failure.
    /// </summary>
    private static DateTimeOffset MidWeekNow()
    {
        var now = DateTimeOffset.UtcNow;
        var daysSinceMonday = ((int)now.DayOfWeek + 6) % 7;
        return now.AddDays(2 - daysSinceMonday);
    }

    /// <summary>
    /// A fully fixed (not real-clock-relative) reference "now": a Wednesday between the 20th and
    /// 26th of its month. Fixed rather than derived from <see cref="MidWeekNow"/> because this
    /// specific test also needs day-of-month margin — a "This month" note must land comfortably
    /// before "Last week"'s ~9-day reach, which isn't guaranteed near the start of a month.
    /// </summary>
    private static DateTimeOffset FixedMidMonthWednesday()
    {
        var day = Enumerable.Range(20, 7).First(d => new DateTime(2026, 6, d).DayOfWeek == DayOfWeek.Wednesday);
        return new DateTimeOffset(new DateTime(2026, 6, day, 12, 0, 0));
    }

    [Fact]
    public void GroupByRelativeDate_buckets_notes_into_today_yesterday_this_week_last_week_this_month_and_previous()
    {
        var now = FixedMidMonthWednesday();
        var today = now.Date;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var lastWeekStart = weekStart.AddDays(-7);
        var monthStart = new DateTime(today.Year, today.Month, 1);

        var todayNote = NoteAt(new DateTimeOffset(today, TimeSpan.Zero), "today");
        var yesterdayNote = NoteAt(new DateTimeOffset(today.AddDays(-1), TimeSpan.Zero), "yesterday");
        var thisWeekNote = NoteAt(new DateTimeOffset(weekStart, TimeSpan.Zero), "this-week");
        var lastWeekNote = NoteAt(new DateTimeOffset(lastWeekStart, TimeSpan.Zero), "last-week");
        var thisMonthNote = NoteAt(new DateTimeOffset(monthStart, TimeSpan.Zero), "this-month");
        var previousNote = NoteAt(new DateTimeOffset(monthStart.AddMonths(-2), TimeSpan.Zero), "previous");

        var groups = NoteGrouping.GroupByRelativeDate(
            [todayNote, yesterdayNote, thisWeekNote, lastWeekNote, thisMonthNote, previousNote],
            now, TimeZoneInfo.Utc);

        Assert.Equal(["Today", "Yesterday", "This week", "Last week", "This month", "Previous"], groups.Select(g => g.Label));
        Assert.Equal("today", Assert.Single(groups[0].Notes).Title);
        Assert.Equal("yesterday", Assert.Single(groups[1].Notes).Title);
        Assert.Equal("this-week", Assert.Single(groups[2].Notes).Title);
        Assert.Equal("last-week", Assert.Single(groups[3].Notes).Title);
        Assert.Equal("this-month", Assert.Single(groups[4].Notes).Title);
        Assert.Equal("previous", Assert.Single(groups[5].Notes).Title);
    }

    [Fact]
    public void GroupByRelativeDate_omits_empty_buckets()
    {
        var now = MidWeekNow();
        var note = NoteAt(now);

        var groups = NoteGrouping.GroupByRelativeDate([note], now, TimeZoneInfo.Utc);

        Assert.Equal(["Today"], groups.Select(g => g.Label));
    }

    [Fact]
    public void GroupByRelativeDate_returns_no_groups_for_an_empty_list()
    {
        Assert.Empty(NoteGrouping.GroupByRelativeDate([], MidWeekNow(), TimeZoneInfo.Utc));
    }

    [Fact]
    public void GroupByRelativeDate_preserves_the_given_order_within_a_bucket()
    {
        var now = MidWeekNow();
        var first = NoteAt(now, "first");
        var second = NoteAt(now.AddHours(-1), "second");
        var third = NoteAt(now.AddHours(-2), "third");

        var groups = NoteGrouping.GroupByRelativeDate([first, second, third], now, TimeZoneInfo.Utc);

        var today = Assert.Single(groups);
        Assert.Equal(["first", "second", "third"], today.Notes.Select(n => n.Title));
    }

    [Fact]
    public void GroupByRelativeDate_converts_both_now_and_each_notes_timestamp_to_the_given_time_zone()
    {
        var utcPlus10 = TimeZoneInfo.CreateCustomTimeZone("Test+10", TimeSpan.FromHours(10), "Test+10", "Test+10");
        var nowUtc = new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero); // 11:00 on Jan 1 in +10 — "today" locally is Jan 1
        // Same UTC calendar day as "now", but 06:00 on Jan 2 once converted to +10. A naive
        // UTC-date comparison would wrongly call this "Today" — the correct, time-zone-aware
        // comparison must place it in a later bucket since its local date is tomorrow.
        var noteUtc = new DateTimeOffset(2026, 1, 1, 20, 0, 0, TimeSpan.Zero);

        var groups = NoteGrouping.GroupByRelativeDate([NoteAt(noteUtc)], nowUtc, utcPlus10);

        Assert.DoesNotContain(groups, g => g.Label == "Today");
    }
}
