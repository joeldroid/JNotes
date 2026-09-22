using JNotes.Core.Indexing;

namespace JNotes.Tests.Indexing;

public class IndexTimestampTests
{
    [Fact]
    public void ToIndexResolution_rounds_sub_microsecond_ticks_down_to_the_microsecond()
    {
        var value = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(1234567);

        var result = IndexTimestamp.ToIndexResolution(value);

        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(1234560), result);
    }

    [Fact]
    public void ToIndexResolution_leaves_a_value_already_on_a_microsecond_boundary_unchanged()
    {
        var value = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(1234560);

        Assert.Equal(value, IndexTimestamp.ToIndexResolution(value));
    }

    [Fact]
    public void ToIndexResolution_never_rounds_up()
    {
        var value = DateTimeOffset.UtcNow;

        Assert.True(IndexTimestamp.ToIndexResolution(value) <= value);
    }

    [Fact]
    public void ToIndexResolution_preserves_the_offset()
    {
        var value = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(2)).AddTicks(1234567);

        Assert.Equal(TimeSpan.FromHours(2), IndexTimestamp.ToIndexResolution(value).Offset);
    }
}
