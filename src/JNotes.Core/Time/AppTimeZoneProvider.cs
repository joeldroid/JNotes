using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JNotes.Core.Time;

/// <inheritdoc cref="IAppTimeZoneProvider"/>
public sealed class AppTimeZoneProvider : IAppTimeZoneProvider
{
    public TimeZoneInfo TimeZone { get; }

    public AppTimeZoneProvider(IOptions<JNotesOptions> options, ILogger<AppTimeZoneProvider> logger)
    {
        var timeZoneId = options.Value.TimeZoneId;
        try
        {
            TimeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // Resolved once at startup (like the notes folder setting, changing TZ needs a
            // restart) — falling back to UTC rather than throwing keeps a typo'd TZ from taking
            // the whole app down.
            logger.LogWarning(ex, "Unknown or invalid TZ '{TimeZoneId}' — falling back to UTC.", timeZoneId);
            TimeZone = TimeZoneInfo.Utc;
        }
    }

    public DateTimeOffset ToAppTimeZone(DateTimeOffset utcValue) => TimeZoneInfo.ConvertTime(utcValue, TimeZone);
}
