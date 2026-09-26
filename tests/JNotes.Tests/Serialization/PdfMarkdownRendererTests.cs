using JNotes.Core.Serialization;

namespace JNotes.Tests.Serialization;

public class PdfMarkdownRendererTests
{
    private const string Stored = "0123456789abcdef0123456789abcdef.png";

    [Fact]
    public void A_stored_image_becomes_a_bare_file_name_and_is_listed()
    {
        var result = PdfMarkdownRenderer.Render($"![pic](images/{Stored})");

        Assert.Contains($"src=\"{Stored}\"", result.Html);
        Assert.Equal([Stored], result.ImageFileNames);
    }

    [Fact]
    public void The_same_image_twice_is_listed_once()
    {
        var result = PdfMarkdownRenderer.Render($"![a](images/{Stored})\n\n![b](images/{Stored})");

        Assert.Equal([Stored], result.ImageFileNames);
    }

    [Theory]
    [InlineData("https://example.com/x.png")]
    [InlineData("images/../0123456789abcdef0123456789abcdef.png")]
    [InlineData("images/other.png")]
    [InlineData("data:image/png;base64,AAAA")]
    public void Any_other_image_becomes_a_plain_link_and_is_not_listed(string url)
    {
        var result = PdfMarkdownRenderer.Render($"![alt]({url})");

        Assert.DoesNotContain("<img", result.Html);
        Assert.Contains("<a href=", result.Html);
        Assert.Empty(result.ImageFileNames);
    }

    [Fact]
    public void An_external_image_with_empty_alt_still_shows_visible_link_text()
    {
        var result = PdfMarkdownRenderer.Render("![](https://example.com/x.png)");

        Assert.DoesNotContain("<img", result.Html);
        Assert.Contains(">https://example.com/x.png</a>", result.Html);
    }

    [Fact]
    public void A_mermaid_fence_becomes_a_mermaid_pre()
    {
        var result = PdfMarkdownRenderer.Render("```mermaid\nflowchart LR\n  A --> B\n```");

        Assert.Contains("<pre class=\"mermaid\">", result.Html);
    }

    [Fact]
    public void Raw_html_is_still_encoded()
    {
        var result = PdfMarkdownRenderer.Render("<script>alert(1)</script>");

        Assert.DoesNotContain("<script>", result.Html);
        Assert.Contains("&lt;script&gt;", result.Html);
    }

    [Fact]
    public void Pipe_tables_render()
    {
        var result = PdfMarkdownRenderer.Render("| a | b |\n| - | - |\n| 1 | 2 |");

        Assert.Contains("<table>", result.Html);
    }
}
