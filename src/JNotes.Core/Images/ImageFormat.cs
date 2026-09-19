using System.Text.RegularExpressions;

namespace JNotes.Core.Images;

/// <summary>An image format JNotes accepts: the extension it's stored under and the MIME type it's served as.</summary>
public readonly record struct ImageType(string Extension, string ContentType);

/// <summary>
/// Which images JNotes accepts, detected from the file's leading bytes rather than the client's
/// name or MIME type (both trivially spoofed). Deliberately only PNG/JPEG/GIF/WebP — the standard
/// browser-renderable raster formats. SVG is excluded on purpose: opened directly, an SVG can run
/// script in the app's origin. Pure static, like <c>UserValidation</c>.
/// </summary>
public static partial class ImageFormat
{
    /// <summary>Largest image accepted, in bytes.</summary>
    public const int MaxBytes = 10 * 1024 * 1024;

    /// <summary>How many leading bytes <see cref="Detect"/> needs to tell every supported format apart.</summary>
    public const int HeaderLength = 12;

    private static readonly ImageType Png = new("png", "image/png");
    private static readonly ImageType Jpeg = new("jpg", "image/jpeg");
    private static readonly ImageType Gif = new("gif", "image/gif");
    private static readonly ImageType WebP = new("webp", "image/webp");

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> JpegSignature => [0xFF, 0xD8, 0xFF];

    /// <summary>The supported format whose signature <paramref name="header"/> starts with, or <c>null</c>.</summary>
    public static ImageType? Detect(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith(PngSignature))
        {
            return Png;
        }

        if (header.StartsWith(JpegSignature))
        {
            return Jpeg;
        }

        if (header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8))
        {
            return Gif;
        }

        // WebP is a RIFF container: "RIFF", a 4-byte size, then "WEBP".
        if (header.Length >= HeaderLength && header[..4].SequenceEqual("RIFF"u8) && header.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return WebP;
        }

        return null;
    }

    /// <summary>
    /// True only for a name JNotes itself generated: 32 lowercase hex digits and a supported
    /// extension. Everything that turns a name from a URL or a note into a path goes through this,
    /// which is also what rules out path traversal (<c>..</c>, separators).
    /// </summary>
    public static bool IsStoredFileName(string? fileName) =>
        fileName is not null && StoredFileNameRegex().IsMatch(fileName);

    /// <summary>The MIME type for a stored file name, or <c>null</c> if it isn't a valid one (see <see cref="IsStoredFileName"/>).</summary>
    public static string? ContentTypeOf(string? fileName)
    {
        if (!IsStoredFileName(fileName))
        {
            return null;
        }

        return Path.GetExtension(fileName) switch
        {
            ".png" => Png.ContentType,
            ".jpg" => Jpeg.ContentType,
            ".gif" => Gif.ContentType,
            ".webp" => WebP.ContentType,
            _ => null,
        };
    }

    // \z, not $: "$" also matches just before a trailing newline.
    [GeneratedRegex(@"^[0-9a-f]{32}\.(png|jpg|gif|webp)\z", RegexOptions.CultureInvariant)]
    private static partial Regex StoredFileNameRegex();
}
