namespace JNotes.Core.Images;

/// <summary>
/// Deletes uploaded images that no note mentions — the orphans left by purging a note or editing a link
/// out, since nothing tracks references as they change (the same image may be pasted into several
/// notes). Takes an explicit <c>userId</c>, like <c>INoteIndexReconciler</c>, because the caller is a
/// background service with no current user.
/// </summary>
/// <remarks>
/// "Mentions" is judged against the raw text of every <c>.md</c> file the user has, trash included, so an
/// image in a trashed note survives until that note is purged.
/// </remarks>
public interface IImageCleanupService
{
    /// <summary>
    /// Cleans one user's images. Nothing is deleted if that user's notes can't all be read.
    /// </summary>
    /// <param name="minAge">Images written more recently than this are never deleted — see <see cref="ImageCleanupSchedule.MinImageAge"/>.</param>
    Task<ImageCleanupResult> CleanUpUserAsync(Guid userId, TimeSpan minAge, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cleans every user's images, one user's failure never stopping the rest, then records the time via
    /// <c>ISettingsService.SetImageCleanupLastRunUtcAsync</c> — so a manual run and the scheduled pass share one clock.
    /// </summary>
    Task<AllUsersImageCleanupResult> CleanUpAllUsersAsync(TimeSpan minAge, CancellationToken cancellationToken = default);
}
