using JNotes.Core.Serialization;

namespace JNotes.Tests.Serialization;

public class MarkdownRendererTests
{
    private readonly MarkdownRenderer _renderer = new();

    [Fact]
    public void RenderToHtml_renders_a_pipe_table_as_an_actual_table()
    {
        const string markdown = """
            | Header 1 | Header 2 |
            | --- | --- |
            | Cell 1 | Cell 2 |
            """;

        var html = _renderer.RenderToHtml(markdown);

        Assert.Contains("<table>", html);
        Assert.Contains("<th>Header 1</th>", html);
        Assert.Contains("<td>Cell 1</td>", html);
    }

    [Fact]
    public void RenderToHtml_encodes_raw_html_instead_of_passing_it_through()
    {
        var html = _renderer.RenderToHtml("<script>alert('x')</script>");

        Assert.DoesNotContain("<script>", html);
    }
}
