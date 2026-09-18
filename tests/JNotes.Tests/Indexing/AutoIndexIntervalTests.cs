using JNotes.Core.Indexing;

namespace JNotes.Tests.Indexing;

public class AutoIndexIntervalTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("5.5")]
    [InlineData("5m")]
    public void ParseMinutes_falls_back_to_the_default_for_blank_or_unparseable_input(string? rawValue)
    {
        Assert.Equal(AutoIndexInterval.DefaultMinutes, AutoIndexInterval.ParseMinutes(rawValue));
    }

    [Theory]
    [InlineData("10", 10)]
    [InlineData(" 10 ", 10)]
    [InlineData("1", 1)]
    public void ParseMinutes_returns_the_parsed_value(string rawValue, int expected)
    {
        Assert.Equal(expected, AutoIndexInterval.ParseMinutes(rawValue));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void ParseMinutes_normalizes_zero_or_negative_to_disabled(string rawValue)
    {
        Assert.Equal(AutoIndexInterval.DisabledMinutes, AutoIndexInterval.ParseMinutes(rawValue));
    }

    [Fact]
    public void ParseMinutes_clamps_an_absurdly_large_value_to_the_PeriodicTimer_ceiling_guard()
    {
        Assert.Equal(AutoIndexInterval.MaxMinutes, AutoIndexInterval.ParseMinutes("99999999"));
    }

    [Fact]
    public void IsEnabled_reflects_whether_minutes_is_positive()
    {
        Assert.False(AutoIndexInterval.IsEnabled(0));
        Assert.True(AutoIndexInterval.IsEnabled(5));
    }
}
