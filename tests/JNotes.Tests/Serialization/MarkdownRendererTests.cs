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

    [Fact]
    public void RenderToHtml_renders_a_mermaid_fence_as_a_mermaid_pre_block()
    {
        const string markdown = "```mermaid\nflowchart LR\n  A --> B\n```";

        var html = _renderer.RenderToHtml(markdown);

        Assert.Contains("<pre class=\"mermaid\">", html);
        Assert.DoesNotContain("language-mermaid", html);
    }

    [Fact]
    public void RenderToHtml_preserves_the_mermaid_source_lines()
    {
        const string markdown = "```mermaid\nflowchart LR\n  A --> B\n```";

        var html = _renderer.RenderToHtml(markdown);

        // The JS reads the source back via textContent, which decodes "--&gt;" to "-->".
        Assert.Contains("flowchart LR\n  A --&gt; B\n", html);
    }

    [Fact]
    public void RenderToHtml_escapes_html_inside_a_mermaid_block()
    {
        const string markdown = "```mermaid\nflowchart LR\n  A[\"</pre><script>alert('x')</script>\"]\n```";

        var html = _renderer.RenderToHtml(markdown);

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;/pre&gt;", html);
    }

    [Fact]
    public void RenderToHtml_matches_the_mermaid_info_string_case_insensitively()
    {
        var html = _renderer.RenderToHtml("```MERMAID\nflowchart LR\n  A --> B\n```");

        Assert.Contains("<pre class=\"mermaid\">", html);
    }

    [Fact]
    public void RenderToHtml_treats_a_mermaid_fence_with_extra_info_arguments_as_mermaid()
    {
        var html = _renderer.RenderToHtml("```mermaid theme=dark\nflowchart LR\n  A --> B\n```");

        Assert.Contains("<pre class=\"mermaid\">", html);
    }

    [Fact]
    public void RenderToHtml_leaves_other_fenced_code_blocks_alone()
    {
        var html = _renderer.RenderToHtml("```csharp\nvar x = 1;\n```");

        Assert.Contains("language-csharp", html);
        Assert.DoesNotContain("class=\"mermaid\"", html);
    }

    [Fact]
    public void RenderToHtml_leaves_an_indented_code_block_alone()
    {
        var html = _renderer.RenderToHtml("    flowchart LR\n      A --> B\n");

        Assert.Contains("<pre><code>", html);
        Assert.DoesNotContain("mermaid", html);
    }

    [Fact]
    public void RenderToHtml_renders_an_empty_mermaid_fence_as_an_empty_block()
    {
        var html = _renderer.RenderToHtml("```mermaid\n```");

        Assert.Contains("<pre class=\"mermaid\"></pre>", html);
    }

    private const string StoredImage = "0123456789abcdef0123456789abcdef.png";

    [Fact]
    public void RenderToHtml_serves_an_uploaded_image_from_the_images_route()
    {
        var html = _renderer.RenderToHtml($"![shot](images/{StoredImage})");

        Assert.Contains($"<img src=\"/images/{StoredImage}\" alt=\"shot\" />", html);
    }

    [Theory]
    [InlineData("images/../0123456789abcdef0123456789abcdef.png")]
    [InlineData("images/other.png")]
    [InlineData("https://example.com/0123456789abcdef0123456789abcdef.png")]
    public void RenderToHtml_leaves_other_image_urls_alone(string url)
    {
        var html = _renderer.RenderToHtml($"![x]({url})");

        Assert.Contains($"src=\"{url}\"", html);
    }

    [Fact]
    public void RenderToHtml_does_not_rewrite_a_plain_link_to_an_image_name()
    {
        var html = _renderer.RenderToHtml($"[download](images/{StoredImage})");

        Assert.Contains($"href=\"images/{StoredImage}\"", html);
    }
}
