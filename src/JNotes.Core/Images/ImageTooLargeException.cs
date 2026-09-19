namespace JNotes.Core.Images;

/// <summary>The uploaded image is larger than <see cref="ImageFormat.MaxBytes"/>.</summary>
public sealed class ImageTooLargeException : Exception
{
    public ImageTooLargeException()
        : base($"Images can be at most {ImageFormat.MaxBytes / (1024 * 1024)} MB.")
    {
    }
}
