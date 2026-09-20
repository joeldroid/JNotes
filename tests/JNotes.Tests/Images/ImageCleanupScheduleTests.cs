using JNotes.Core.Images;

namespace JNotes.Tests.Images;

public class ImageCleanupScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_default_is_seven_days()
    {
        Assert.Equal(7, ImageCleanupSchedule.DefaultIntervalDays);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(7, true)]
    [InlineData(365, true)]
    [InlineData(-1, false)]
    [InlineData(366, false)]
    public void IsValidIntervalDays_accepts_zero_through_the_maximum(int days, bool expected)
    {
        Assert.Equal(expected, ImageCleanupSchedule.IsValidIntervalDays(days));
    }

    [Fact]
    public void IsDue_is_false_when_disabled_however_long_ago_it_last_ran()
    {
        Assert.False(ImageCleanupSchedule.IsDue(0, Now.AddYears(-1), Now));
    }

    [Fact]
    public void IsDue_is_false_when_it_has_never_run_so_the_caller_starts_the_clock_instead()
    {
        Assert.False(ImageCleanupSchedule.IsDue(7, null, Now));
    }

    [Theory]
    [InlineData(6, false)]
    [InlineData(7, true)] // exactly one interval ago is due
    [InlineData(30, true)]
    public void IsDue_compares_days_since_the_last_run_to_the_interval(int daysAgo, bool expected)
    {
        Assert.Equal(expected, ImageCleanupSchedule.IsDue(7, Now.AddDays(-daysAgo), Now));
    }

    [Fact]
    public void IsDue_follows_a_changed_interval_immediately()
    {
        var lastRun = Now.AddDays(-3);

        Assert.False(ImageCleanupSchedule.IsDue(7, lastRun, Now));
        Assert.True(ImageCleanupSchedule.IsDue(2, lastRun, Now));
    }
}
