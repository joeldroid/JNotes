namespace JNotes.Core.Images;

/// <summary>
/// The rules for the automatic unused-image cleanup: the admin-set interval (in days) and when a pass is
/// due. A plain static class like <see cref="Indexing.AutoIndexInterval"/>, so the semantics are
/// unit-testable without a host, a timer, or a database.
/// </summary>
public static class ImageCleanupSchedule
{
    public const int DefaultIntervalDays = 7;

    /// <summary>Stored/entered as <c>0</c> to turn the automatic cleanup off.</summary>
    public const int DisabledIntervalDays = 0;

    public const int MaxIntervalDays = 365;

    /// <summary>
    /// Images written more recently than this are never deleted, by the automatic pass or the manual one:
    /// an upload lands on disk before the debounced autosave writes the note containing its link, so a
    /// brand-new image can look unreferenced.
    /// </summary>
    public static readonly TimeSpan MinImageAge = TimeSpan.FromMinutes(10);

    public static bool IsValidIntervalDays(int days) => days is >= DisabledIntervalDays and <= MaxIntervalDays;

    public static bool IsEnabled(int intervalDays) => intervalDays > DisabledIntervalDays;

    /// <summary>
    /// Whether a pass should run now: enabled, and at least <paramref name="intervalDays"/> since
    /// <paramref name="lastRunUtc"/>. A never-run schedule (<c>null</c>) is not due — the caller starts
    /// the clock instead, so upgrading never deletes anything on the very first start.
    /// </summary>
    public static bool IsDue(int intervalDays, DateTimeOffset? lastRunUtc, DateTimeOffset nowUtc) =>
        IsEnabled(intervalDays) && lastRunUtc is { } last && nowUtc - last >= TimeSpan.FromDays(intervalDays);
}
