namespace JNotes.Core.Images;

/// <summary>The uploaded data isn't one of the supported image formats (see <see cref="ImageFormat"/>).</summary>
public sealed class UnsupportedImageException : Exception
{
    public UnsupportedImageException()
        : base("Only PNG, JPEG, GIF and WebP images are supported.")
    {
    }
}
