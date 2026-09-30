using JNotes.Core.Images;

namespace JNotes.Tests.Images;

public class ImageLinksTests
{
    private const string Stored = "0123456789abcdef0123456789abcdef.png";

    [Fact]
    public void ToMarkdown_uses_a_note_relative_link_and_the_original_name_as_alt_text()
    {
        Assert.Equal($"![holiday photo](images/{Stored})", ImageLinks.ToMarkdown("holiday photo.JPG", Stored));
    }

    [Theory]
    [InlineData("a[b](c).png", "abc")]
    [InlineData("line\nbreak.png", "linebreak")]
    [InlineData("[].png", "image")]
    [InlineData("", "image")]
    public void ToMarkdown_strips_characters_that_would_break_the_link(string original, string expectedAlt)
    {
        Assert.Equal($"![{expectedAlt}](images/{Stored})", ImageLinks.ToMarkdown(original, Stored));
    }

    [Fact]
    public void ToStoredFileName_returns_the_name_only_for_an_exact_stored_image_link()
    {
        Assert.Equal(Stored, ImageLinks.ToStoredFileName("images/" + Stored));
        Assert.Null(ImageLinks.ToStoredFileName("images/../" + Stored));
        Assert.Null(ImageLinks.ToStoredFileName("pictures/" + Stored));
        Assert.Null(ImageLinks.ToStoredFileName("https://example.com/images/" + Stored));
        Assert.Null(ImageLinks.ToStoredFileName("/images/" + Stored));
    }

    [Fact]
    public void ToServedUrl_rewrites_only_an_exact_stored_image_link()
    {
        Assert.Equal("/images/" + Stored, ImageLinks.ToServedUrl("images/" + Stored));
        Assert.Null(ImageLinks.ToServedUrl("images/../" + Stored));
        Assert.Null(ImageLinks.ToServedUrl("images/other.png"));
        Assert.Null(ImageLinks.ToServedUrl("https://example.com/images/" + Stored));
        Assert.Null(ImageLinks.ToServedUrl("/images/" + Stored));
    }

    [Fact]
    public void NoteScopedPrefix_is_the_notes_id_images_route()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");

        Assert.Equal("/notes/11111111-2222-3333-4444-555555555555/images/", ImageLinks.NoteScopedPrefix(id));
    }

    [Fact]
    public void ToNoteScopedServedUrl_rewrites_only_an_exact_stored_image_link()
    {
        var id = Guid.NewGuid();

        Assert.Equal($"/notes/{id}/images/{Stored}", ImageLinks.ToNoteScopedServedUrl("images/" + Stored, id));
        Assert.Null(ImageLinks.ToNoteScopedServedUrl("https://example.com/images/" + Stored, id));
        Assert.Null(ImageLinks.ToNoteScopedServedUrl("images/../" + Stored, id));
        Assert.Null(ImageLinks.ToNoteScopedServedUrl("images/" + Stored.ToUpperInvariant(), id));
        Assert.Null(ImageLinks.ToNoteScopedServedUrl("images/other.png", id));
        Assert.Null(ImageLinks.ToNoteScopedServedUrl("/images/" + Stored, id));
    }
}
