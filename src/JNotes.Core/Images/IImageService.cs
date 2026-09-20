using JNotes.Core.Storage;

namespace JNotes.Core.Images;

/// <summary>
/// What a cleanup did: how many unreferenced images it deleted and how many bytes that freed, plus how
/// many unreferenced images it left alone only because they were too new (see
/// <see cref="IImageService.CleanUpUnusedAsync"/>).
/// </summary>
public sealed record ImageCleanupResult(int Deleted, long BytesFreed, int SkippedRecent);

/// <summary>
/// Image upload/serving for whoever the current user is (implicit, like <c>INoteService</c> — see
/// <c>ICurrentUserContext</c>), so callers can't name another user's folder.
/// </summary>
public interface IImageService
{
    /// <summary>Stores the image in the current user's <c>images/</c> folder and returns its generated file name.</summary>
    /// <exception cref="UnsupportedImageException">The data isn't a supported image format.</exception>
    /// <exception cref="ImageTooLargeException">The data exceeds <see cref="ImageFormat.MaxBytes"/>.</exception>
    Task<string> SaveAsync(Stream content, CancellationToken cancellationToken = default);

    /// <summary>The current user's image, or <c>null</c> — including for another user's file name.</summary>
    Task<ImageContent?> OpenAsync(string fileName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the current user's images that no note mentions — the orphans left by purging a note or
    /// editing a link out, since nothing tracks references as they change (the same image may be pasted
    /// into several notes). "Mentions" is judged against every <c>.md</c> file the user has, trash
    /// included, so an image in a trashed note survives until that note is purged. Nothing is deleted
    /// if the notes can't all be read.
    /// </summary>
    /// <param name="minAge">
    /// Images written more recently than this are never deleted: an upload lands on disk before the note
    /// containing its link is saved (autosave is debounced), so a brand-new image can look unreferenced.
    /// </param>
    Task<ImageCleanupResult> CleanUpUnusedAsync(TimeSpan minAge, CancellationToken cancellationToken = default);
}
