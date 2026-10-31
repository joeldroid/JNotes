using JNotes.Core.Export;
using JNotes.Core.Models;

namespace JNotes.Tests.Export;

public class NotePdfDocumentBuilderTests
{
    private const string Stored = "0123456789abcdef0123456789abcdef.png";

    private static Note MakeNote(string title = "Title", string body = "", params string[] tags) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Tags = tags,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Body = body,
    };

    [Fact]
    public void The_title_is_encoded_in_both_the_title_element_and_the_heading()
    {
        var doc = NotePdfDocumentBuilder.Build(MakeNote(title: "<b>x</b>"), "when");

        Assert.Contains("<title>&lt;b&gt;x&lt;/b&gt;</title>", doc.Html);
        Assert.Contains("<h1 class=\"note-title\">&lt;b&gt;x&lt;/b&gt;</h1>", doc.Html);
        Assert.DoesNotContain("<b>x</b>", doc.Html);
    }

    [Fact]
    public void An_empty_title_becomes_Untitled_note()
    {
        var doc = NotePdfDocumentBuilder.Build(MakeNote(title: "  "), "when");

        Assert.Contains("<h1 class=\"note-title\">Untitled note</h1>", doc.Html);
    }

    [Fact]
    public void Tags_are_rendered_and_encoded()
    {
        var doc = NotePdfDocumentBuilder.Build(MakeNote(tags: ["work", "<i>"]), "when");

        Assert.Contains("<span class=\"tag\">work</span>", doc.Html);
        Assert.Contains("<span class=\"tag\">&lt;i&gt;</span>", doc.Html);
    }

    [Fact]
    public void A_note_without_tags_has_no_tag_row()
    {
        Assert.DoesNotContain("class=\"note-tags\"", NotePdfDocumentBuilder.Build(MakeNote(), "when").Html);
    }

    [Fact]
    public void The_meta_line_has_the_last_updated_text()
    {
        var doc = NotePdfDocumentBuilder.Build(MakeNote(), "Last updated 1/2/2026 (UTC)");

        Assert.Contains("<div class=\"note-meta\">Last updated 1/2/2026 (UTC)</div>", doc.Html);
    }

    [Fact]
    public void The_embedded_stylesheet_is_inlined()
    {
        var html = NotePdfDocumentBuilder.Build(MakeNote(), "when").Html;

        Assert.Contains("color-scheme: light", html);
        Assert.Contains("roboto-latin-400-normal.woff2", html);
    }

    [Fact]
    public void The_ready_script_is_always_present()
    {
        var html = NotePdfDocumentBuilder.Build(MakeNote(), "when").Html;

        Assert.Contains("window.jnotesPdfReady = true", html);
    }

    [Fact]
    public void A_mermaid_note_references_the_mermaid_script()
    {
        var doc = NotePdfDocumentBuilder.Build(MakeNote(body: "```mermaid\nflowchart LR\n  A --> B\n```"), "when");

        Assert.True(doc.HasMermaid);
        Assert.Contains("<script src=\"mermaid.min.js\"></script>", doc.Html);
    }

    [Fact]
    public void A_plain_note_does_not_reference_mermaid()
    {
        var doc = NotePdfDocumentBuilder.Build(MakeNote(body: "just text"), "when");

        Assert.False(doc.HasMermaid);
        Assert.DoesNotContain("mermaid.min.js", doc.Html);
    }

    [Fact]
    public void A_text_note_passes_its_image_names_through()
    {
        var doc = NotePdfDocumentBuilder.Build(MakeNote(body: $"![a](images/{Stored})"), "when");

        Assert.Equal([Stored], doc.ImageFileNames);
        Assert.Contains($"src=\"{Stored}\"", doc.Html);
    }

    [Fact]
    public void A_task_list_renders_read_only_checkboxes()
    {
        var doc = NotePdfDocumentBuilder.Build(MakeNote(body: "- [ ] milk\n- [x] eggs"), "when");

        // Pins the markup note-pdf.css selects on (`li.task-list-item > input[type="checkbox"]`): Markdig puts no
        // class on the checkbox itself.
        var items = System.Text.RegularExpressions.Regex.Matches(doc.Html, "<li class=\"task-list-item\"><input ([^>]*)/> ([^<]*)</li>");
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
        Assert.DoesNotContain("class=\"box\"", doc.Html);
        Assert.False(doc.HasMermaid);
    }

    [Fact]
    public void The_footer_has_page_number_placeholders()
    {
        Assert.Contains("pageNumber", NotePdfDocumentBuilder.FooterHtml);
        Assert.Contains("totalPages", NotePdfDocumentBuilder.FooterHtml);
    }
}
