using JNotes.Core.Tagging;

namespace JNotes.Tests.Tagging;

public class AutoTagScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 6)]
    [InlineData(3, 24)]
    [InlineData(4, 72)]
    [InlineData(9, 72)]
    public void RetryDelay_backs_off_1h_6h_24h_then_72h(int failureCount, int expectedHours)
    {
        Assert.Equal(TimeSpan.FromHours(expectedHours), AutoTagSchedule.RetryDelay(failureCount));
    }

    [Fact]
    public void AfterFailure_retries_until_the_fifth_failure_then_gives_up()
    {
        Assert.Equal((AutoTagOutcome.Failed, (DateTimeOffset?)(Now + TimeSpan.FromHours(1))), AutoTagSchedule.AfterFailure(1, Now));
        Assert.Equal((AutoTagOutcome.Failed, (DateTimeOffset?)(Now + TimeSpan.FromHours(72))), AutoTagSchedule.AfterFailure(4, Now));
        Assert.Equal((AutoTagOutcome.GaveUp, (DateTimeOffset?)null), AutoTagSchedule.AfterFailure(5, Now));
        Assert.Equal((AutoTagOutcome.GaveUp, (DateTimeOffset?)null), AutoTagSchedule.AfterFailure(6, Now));
    }

    [Fact]
    public void A_note_must_be_quiet_for_ten_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), AutoTagSchedule.MinNoteAge);
    }
}
