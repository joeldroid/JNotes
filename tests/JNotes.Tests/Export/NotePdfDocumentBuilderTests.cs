using JNotes.Core.Export;
using JNotes.Core.Models;

namespace JNotes.Tests.Export;

public class NotePdfDocumentBuilderTests
{
    private const string Stored = "0123456789abcdef0123456789abcdef.png";

    private static Note MakeNote(NoteType type = NoteType.Text, string title = "Title", string body = "", params string[] tags) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Type = type,
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

    [Theory]
    [InlineData(NoteType.Text, "Text")]
    [InlineData(NoteType.Checklist, "Checklist")]
    public void The_meta_line_has_the_type_label_and_the_last_updated_text(NoteType type, string label)
    {
        var doc = NotePdfDocumentBuilder.Build(MakeNote(type), "Last updated 1/2/2026 (UTC)");

        Assert.Contains($"<div class=\"note-meta\">{label} · Last updated 1/2/2026 (UTC)</div>", doc.Html);
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
    public void Checklist_headings_become_h2_and_checked_items_are_marked()
    {
        var doc = NotePdfDocumentBuilder.Build(
            MakeNote(NoteType.Checklist, body: "## Groceries\n- [ ] milk\n- [x] eggs"), "when");

        Assert.Contains("<h2>Groceries</h2>", doc.Html);
        Assert.Contains("<li><span class=\"box\"></span><span class=\"text\">milk</span></li>", doc.Html);
        Assert.Contains("<li class=\"checked\"><span class=\"box\"></span><span class=\"text\">eggs</span></li>", doc.Html);
        Assert.Empty(doc.ImageFileNames);
        Assert.False(doc.HasMermaid);
    }

    [Fact]
    public void Checklist_item_text_is_encoded()
    {
        var doc = NotePdfDocumentBuilder.Build(MakeNote(NoteType.Checklist, body: "- [ ] <i>x</i>"), "when");

        Assert.Contains("&lt;i&gt;x&lt;/i&gt;", doc.Html);
        Assert.DoesNotContain("<i>x</i>", doc.Html);
    }

    [Fact]
    public void An_untitled_empty_checklist_section_is_skipped_when_categories_exist()
    {
        var doc = NotePdfDocumentBuilder.Build(MakeNote(NoteType.Checklist, body: "## Only\n- [ ] a"), "when");

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(doc.Html, "<ul>"));
        Assert.DoesNotContain("This checklist has no items.", doc.Html);
    }

    [Fact]
    public void An_empty_category_says_No_items()
    {
        var doc = NotePdfDocumentBuilder.Build(MakeNote(NoteType.Checklist, body: "## Empty\n\n## Full\n- [ ] a"), "when");

        Assert.Contains("<h2>Empty</h2>\n<p class=\"empty\">No items.</p>", doc.Html);
    }

    [Fact]
    public void An_empty_checklist_body_says_the_checklist_has_no_items()
    {
        var doc = NotePdfDocumentBuilder.Build(MakeNote(NoteType.Checklist, body: ""), "when");

        Assert.Contains("This checklist has no items.", doc.Html);
    }

    [Fact]
    public void Checklist_prose_lines_are_not_included()
    {
        var doc = NotePdfDocumentBuilder.Build(MakeNote(NoteType.Checklist, body: "some prose line\n- [ ] a"), "when");

        Assert.DoesNotContain("some prose line", doc.Html);
    }

    [Fact]
    public void The_footer_has_page_number_placeholders()
    {
        Assert.Contains("pageNumber", NotePdfDocumentBuilder.FooterHtml);
        Assert.Contains("totalPages", NotePdfDocumentBuilder.FooterHtml);
    }
}
