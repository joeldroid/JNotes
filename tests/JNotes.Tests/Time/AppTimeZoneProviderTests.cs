using JNotes.Core;
using JNotes.Core.Time;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JNotes.Tests.Time;

public class AppTimeZoneProviderTests
{
    private static AppTimeZoneProvider Create(string timeZoneId) =>
        new(Options.Create(new JNotesOptions { TimeZoneId = timeZoneId }), NullLogger<AppTimeZoneProvider>.Instance);

    [Fact]
    public void Resolves_a_valid_IANA_time_zone_id()
    {
        var provider = Create("America/New_York");

        Assert.Equal("America/New_York", provider.TimeZone.Id);
    }

    [Fact]
    public void Falls_back_to_UTC_for_an_unknown_time_zone_id()
    {
        var provider = Create("Not/A_Real_Zone");

        Assert.Equal(TimeZoneInfo.Utc.Id, provider.TimeZone.Id);
    }

    [Fact]
    public void Falls_back_to_UTC_for_an_empty_time_zone_id()
    {
        var provider = Create("");

        Assert.Equal(TimeZoneInfo.Utc.Id, provider.TimeZone.Id);
    }

    [Fact]
    public void ToAppTimeZone_converts_a_UTC_value_into_the_configured_zone()
    {
        var provider = Create("America/New_York");
        // A UTC instant in January (EST, UTC-5, no DST) — chosen to avoid daylight-saving ambiguity.
        var utc = new DateTimeOffset(2026, 1, 15, 17, 30, 0, TimeSpan.Zero);

        var converted = provider.ToAppTimeZone(utc);

        Assert.Equal(new DateTimeOffset(2026, 1, 15, 12, 30, 0, TimeSpan.FromHours(-5)), converted);
    }

    [Fact]
    public void ToAppTimeZone_is_a_noop_when_configured_zone_is_UTC()
    {
        var provider = Create("UTC");
        var utc = DateTimeOffset.UtcNow;

        Assert.Equal(utc, provider.ToAppTimeZone(utc));
    }
}
