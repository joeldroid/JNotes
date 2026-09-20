using JNotes.Core.Images;

namespace JNotes.Core.Storage;

/// <summary>An image file on disk: its stored name, size, and when it was last written (for uploads, when it arrived).</summary>
public sealed record StoredImage(string FileName, long SizeBytes, DateTimeOffset LastWriteUtc);

/// <summary>An opened image and the MIME type to serve it as. The caller disposes <see cref="Stream"/>.</summary>
public sealed record ImageContent(Stream Stream, string ContentType);

/// <summary>
/// Reads and writes a user's uploaded images under <c>{NotesFolder}/{userId}/images/</c>. Like
/// <see cref="INoteFileStore"/>, it takes an explicit <paramref name="userId"/>; <c>ImageService</c>
/// is the implicit-current-user front for it.
/// </summary>
public interface IImageStore
{
    /// <summary>
    /// Stores the image under a generated name (<c>{guid}.{ext}</c>, extension from the detected format)
    /// and returns that name. Nothing is left on disk if it throws.
    /// </summary>
    /// <exception cref="UnsupportedImageException">The data isn't a supported image format.</exception>
    /// <exception cref="ImageTooLargeException">The data exceeds <see cref="ImageFormat.MaxBytes"/>.</exception>
    Task<string> SaveAsync(Guid userId, Stream content, CancellationToken cancellationToken = default);

    /// <summary>The image, or <c>null</c> if <paramref name="fileName"/> isn't a valid stored name or doesn't exist for this user.</summary>
    Task<ImageContent?> OpenReadAsync(Guid userId, string fileName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every image <paramref name="userId"/> has stored. Only files whose names JNotes itself generated
    /// (<see cref="ImageFormat.IsStoredFileName"/>) are listed, so a stray file someone dropped in the
    /// folder is never a candidate for <see cref="DeleteAsync"/>. Empty if the folder doesn't exist.
    /// </summary>
    Task<IReadOnlyList<StoredImage>> ListAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Deletes the image. No-ops if <paramref name="fileName"/> isn't a valid stored name or doesn't exist.</summary>
    Task DeleteAsync(Guid userId, string fileName, CancellationToken cancellationToken = default);
}
