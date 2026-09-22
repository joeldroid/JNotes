namespace JNotes.Core.Indexing;

/// <summary>
/// The note index stores timestamps in PostgreSQL `timestamptz`, which has **microsecond**
/// resolution, while File.GetLastWriteTimeUtc gives 100-nanosecond ticks. The reconciler's
/// "unchanged since last indexed" test is `lastIndexed >= currentMtime`, so without rounding both
/// sides to the store's own resolution, a truncated round trip makes every file look modified on
/// every single rescan — a full re-parse of the whole collection at startup and on every
/// auto-index tick. (Redis stored the full "O" round-trip string, so this didn't arise there.)
/// </summary>
public static class IndexTimestamp
{
    /// <summary>100-nanosecond ticks per microsecond.</summary>
    public const long TicksPerMicrosecond = 10;

    /// <summary>Rounds <paramref name="value"/> down to whole microseconds.</summary>
    public static DateTimeOffset ToIndexResolution(DateTimeOffset value) =>
        value.AddTicks(-(value.Ticks % TicksPerMicrosecond));
}
