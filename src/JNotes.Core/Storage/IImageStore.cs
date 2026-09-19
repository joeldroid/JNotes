using JNotes.Core.Images;

namespace JNotes.Core.Storage;

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
}
