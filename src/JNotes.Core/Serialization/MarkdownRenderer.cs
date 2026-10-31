using JNotes.Core.Images;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace JNotes.Core.Serialization;

/// <inheritdoc cref="IMarkdownRenderer"/>
public sealed class MarkdownRenderer : IMarkdownRenderer
{
    // Raw HTML embedded in a note is disabled (encoded as text) rather than passed through and
    // rendered — a pragmatic default for a single-user app, without pulling in a separate HTML
    // sanitizer. Pipe tables (GFM-style `| a | b |`) are enabled so the editor toolbar's "insert
    // table" snippet actually renders as a table. Extend with further extensions here as needed.
    private static readonly MarkdownPipeline Pipeline = CreateBaseBuilder()
        // Uploaded images' note-relative links (images/<file>) become the served /images/<file> route.
        .Use<ImageLinkMarkdownExtension>()
        .Build();

    // The PDF pipeline (PdfMarkdownRenderer) starts from this same base so the two can't drift.
    internal static MarkdownPipelineBuilder CreateBaseBuilder() => new MarkdownPipelineBuilder()
        .UsePipeTables()
        // `- [ ]` / `- [x]` become <input disabled type="checkbox"> list items. Display-only by design: nothing
        // toggles them (edit the markdown), and checked items never reorder.
        .UseTaskLists()
        .DisableHtml()
        // ```mermaid fences become <pre class="mermaid"> for the browser-side renderer to pick up;
        // the source is HTML-escaped, so this is not a hole in DisableHtml().
        .Use<MermaidMarkdownExtension>();

    // Same base, but with no ImageLinkMarkdownExtension: RenderToHtml(markdown, noteId) rewrites image URLs itself.
    private static readonly MarkdownPipeline BasePipeline = CreateBaseBuilder().Build();

    public string RenderToHtml(string markdown) => Markdown.ToHtml(markdown, Pipeline);

    public string RenderToHtml(string markdown, Guid noteScopedImagesFor)
    {
        var document = Markdown.Parse(markdown, BasePipeline);

        foreach (var link in document.Descendants<LinkInline>())
        {
            if (link.IsImage && ImageLinks.ToNoteScopedServedUrl(link.Url ?? "", noteScopedImagesFor) is { } url)
            {
                link.Url = url;
            }
        }

        return document.ToHtml(BasePipeline);
    }
}
