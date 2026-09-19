using Markdig;

namespace JNotes.Core.Serialization;

/// <inheritdoc cref="IMarkdownRenderer"/>
public sealed class MarkdownRenderer : IMarkdownRenderer
{
    // Raw HTML embedded in a note is disabled (encoded as text) rather than passed through and
    // rendered — a pragmatic default for a single-user app, without pulling in a separate HTML
    // sanitizer. Pipe tables (GFM-style `| a | b |`) are enabled so the editor toolbar's "insert
    // table" snippet actually renders as a table. Extend with further extensions here as needed.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .DisableHtml()
        // ```mermaid fences become <pre class="mermaid"> for the browser-side renderer to pick up;
        // the source is HTML-escaped, so this is not a hole in DisableHtml().
        .Use<MermaidMarkdownExtension>()
        // Uploaded images' note-relative links (images/<file>) become the served /images/<file> route.
        .Use<ImageLinkMarkdownExtension>()
        .Build();

    public string RenderToHtml(string markdown) => Markdown.ToHtml(markdown, Pipeline);
}
