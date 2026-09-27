namespace JNotes.Tests.TestDoubles;

/// <summary>The real clock plus a movable offset, so backoff windows can be crossed without waiting (file mtimes stay real).</summary>
internal sealed class TestTimeProvider : TimeProvider
{
    private TimeSpan _offset;

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + _offset;

    public void Advance(TimeSpan by) => _offset += by;
}
