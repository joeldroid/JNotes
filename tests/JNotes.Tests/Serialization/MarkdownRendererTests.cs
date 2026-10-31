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

    [Fact]
    public void RenderToHtml_with_a_note_id_serves_an_uploaded_image_from_the_note_scoped_route()
    {
        var noteId = Guid.NewGuid();

        var html = _renderer.RenderToHtml($"![shot](images/{StoredImage})", noteId);

        Assert.Contains($"<img src=\"/notes/{noteId}/images/{StoredImage}\" alt=\"shot\" />", html);
        Assert.DoesNotContain($"src=\"/images/{StoredImage}\"", html);
    }

    [Theory]
    [InlineData("images/../0123456789abcdef0123456789abcdef.png")]
    [InlineData("images/other.png")]
    [InlineData("https://example.com/0123456789abcdef0123456789abcdef.png")]
    public void RenderToHtml_with_a_note_id_leaves_other_image_urls_alone(string url)
    {
        var html = _renderer.RenderToHtml($"![x]({url})", Guid.NewGuid());

        Assert.Contains($"src=\"{url}\"", html);
    }

    [Fact]
    public void RenderToHtml_with_a_note_id_still_escapes_html_inside_a_mermaid_block()
    {
        const string markdown = "```mermaid\nflowchart LR\n  A[\"</pre><script>alert('x')</script>\"]\n```";

        var html = _renderer.RenderToHtml(markdown, Guid.NewGuid());

        Assert.Contains("<pre class=\"mermaid\">", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;/pre&gt;", html);
    }

    [Fact]
    public void RenderToHtml_with_a_note_id_still_encodes_raw_html()
    {
        var html = _renderer.RenderToHtml("<script>alert('x')</script>", Guid.NewGuid());

        Assert.DoesNotContain("<script>", html);
    }

    [Fact]
    public void RenderToHtml_renders_task_list_items_as_disabled_checkboxes()
    {
        var html = _renderer.RenderToHtml("- [ ] milk\n- [x] eggs");

        // Pins the markup markdown-preview.css and note-pdf.css select on (`ul.contains-task-list`,
        // `li.task-list-item > input[type="checkbox"]`): Markdig puts no class on the checkbox itself.
        Assert.Contains("<ul class=\"contains-task-list\">", html);
        var items = System.Text.RegularExpressions.Regex.Matches(html, "<li class=\"task-list-item\"><input ([^>]*)/> ([^<]*)</li>");
        Assert.Equal(2, items.Count);
        foreach (var item in items.Cast<System.Text.RegularExpressions.Match>())
        {
            var attributes = item.Groups[1].Value;
            Assert.Contains("type=\"checkbox\"", attributes);
            Assert.Contains("disabled=\"disabled\"", attributes);
            Assert.DoesNotContain("class=", attributes);
        }

        Assert.Equal(("milk", false), (items[0].Groups[2].Value, items[0].Groups[1].Value.Contains("checked=\"checked\"")));
        Assert.Equal(("eggs", true), (items[1].Groups[2].Value, items[1].Groups[1].Value.Contains("checked=\"checked\"")));
    }

    [Fact]
    public void RenderToHtml_still_encodes_a_raw_input_element_typed_in_a_note()
    {
        var html = _renderer.RenderToHtml("<input type=\"checkbox\" onclick=\"alert(1)\">");

        Assert.DoesNotContain("<input", html);
        Assert.Contains("&lt;input", html);
    }
}
