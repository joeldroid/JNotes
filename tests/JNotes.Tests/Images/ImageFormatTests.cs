using JNotes.Core.Images;

namespace JNotes.Tests.Images;

public class ImageFormatTests
{
    private const string ValidName = "0123456789abcdef0123456789abcdef";

    // Just the signatures (padded to the 12-byte header window), not whole images.
    internal static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
    internal static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0];
    internal static readonly byte[] Gif = "GIF89a\0\0\0\0\0\0"u8.ToArray();
    internal static readonly byte[] WebP = "RIFF\0\0\0\0WEBP"u8.ToArray();

    [Theory]
    [MemberData(nameof(Supported))]
    public void Detect_recognises_each_supported_format(byte[] header, string extension, string contentType)
    {
        var type = ImageFormat.Detect(header);

        Assert.Equal(new ImageType(extension, contentType), type);
    }

    public static TheoryData<byte[], string, string> Supported() => new()
    {
        { Png, "png", "image/png" },
        { Jpeg, "jpg", "image/jpeg" },
        { Gif, "gif", "image/gif" },
        { WebP, "webp", "image/webp" },
    };

    [Theory]
    [InlineData("hello world!")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"/>")]
    [InlineData("<?xml version=\"1.0\"?>")]
    [InlineData("RIFF\0\0\0\0WAVE")] // RIFF, but not WebP
    [InlineData("")]
    public void Detect_rejects_anything_that_is_not_a_supported_image(string text)
    {
        Assert.Null(ImageFormat.Detect(System.Text.Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void Detect_rejects_a_truncated_signature()
    {
        Assert.Null(ImageFormat.Detect(Png.AsSpan(0, 4)));
        Assert.Null(ImageFormat.Detect(WebP.AsSpan(0, 8)));
    }

    [Theory]
    [InlineData(ValidName + ".png", true)]
    [InlineData(ValidName + ".jpg", true)]
    [InlineData(ValidName + ".gif", true)]
    [InlineData(ValidName + ".webp", true)]
    [InlineData(ValidName + ".svg", false)]
    [InlineData(ValidName + ".jpeg", false)]
    [InlineData(ValidName + ".png\n", false)]
    [InlineData("../" + ValidName + ".png", false)]
    [InlineData(ValidName + "/../x.png", false)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF.png", false)]
    [InlineData("0123456789abcdef.png", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsStoredFileName_only_accepts_generated_names(string? name, bool expected)
    {
        Assert.Equal(expected, ImageFormat.IsStoredFileName(name));
    }

    [Fact]
    public void ContentTypeOf_maps_the_extension_and_is_null_for_an_invalid_name()
    {
        Assert.Equal("image/jpeg", ImageFormat.ContentTypeOf(ValidName + ".jpg"));
        Assert.Null(ImageFormat.ContentTypeOf("../etc/passwd"));
    }

    [Fact]
    public void FindStoredFileNames_finds_every_name_wherever_it_appears()
    {
        const string Other = "fedcba9876543210fedcba9876543210";
        var text = $"---\ncover: images/{ValidName}.png\n---\n![a](images/{ValidName}.png) and <img src=\"images/{Other}.webp\">\n```\n{Other}.jpg\n```";

        var names = ImageFormat.FindStoredFileNames(text).ToList();

        Assert.Equal([$"{ValidName}.png", $"{ValidName}.png", $"{Other}.webp", $"{Other}.jpg"], names);
    }

    [Theory]
    [InlineData("no images here")]
    [InlineData("images/short.png")]
    [InlineData("images/0123456789abcdef0123456789abcdef.svg")]
    [InlineData("0123456789abcdef0123456789abcdef")] // no extension
    public void FindStoredFileNames_ignores_text_that_is_not_a_stored_name(string text)
    {
        Assert.Empty(ImageFormat.FindStoredFileNames(text));
    }
}
