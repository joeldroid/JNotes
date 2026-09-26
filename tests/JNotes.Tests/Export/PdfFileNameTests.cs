using JNotes.Core.Export;

namespace JNotes.Tests.Export;

public class PdfFileNameTests
{
    [Fact]
    public void Invalid_characters_become_single_spaces()
    {
        Assert.Equal("Q3 Plan draft v2.pdf", PdfFileName.FromTitle("Q3 / Plan: \"draft\" <v2>"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    public void Blank_or_dots_only_titles_fall_back(string? title)
    {
        Assert.Equal("Untitled note.pdf", PdfFileName.FromTitle(title));
    }

    [Fact]
    public void Line_breaks_and_tabs_become_spaces()
    {
        Assert.Equal("a b c d.pdf", PdfFileName.FromTitle("a\r\nb\tc\nd"));
    }

    [Fact]
    public void Unicode_is_kept()
    {
        Assert.Equal("Café 日本.pdf", PdfFileName.FromTitle("Café 日本"));
    }

    [Fact]
    public void A_long_title_is_cut_to_at_most_100_characters()
    {
        var name = PdfFileName.FromTitle(new string('a', 300));

        Assert.EndsWith(".pdf", name);
        Assert.True(name.Length - ".pdf".Length <= 100);
    }

    [Fact]
    public void A_surrogate_pair_at_the_cut_is_not_split()
    {
        // 99 ASCII characters, then an emoji (a surrogate pair) straddling the 100-character cut.
        var name = PdfFileName.FromTitle(new string('a', 99) + "😀" + "tail");

        var stem = name[..^".pdf".Length];
        Assert.Equal(new string('a', 99), stem);
        for (var i = 0; i < stem.Length; i++)
        {
            Assert.False(char.IsSurrogate(stem[i]));
        }
    }
}
