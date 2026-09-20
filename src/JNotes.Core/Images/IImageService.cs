using JNotes.Core.Storage;

namespace JNotes.Core.Images;

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
}
