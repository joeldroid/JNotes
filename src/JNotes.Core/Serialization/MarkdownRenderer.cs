using Markdig;

namespace JNotes.Core.Serialization;

/// <inheritdoc cref="IMarkdownRenderer"/>
public sealed class MarkdownRenderer : IMarkdownRenderer
{
    // Raw HTML embedded in a note is disabled (encoded as text) rather than passed through and
    // rendered — a pragmatic default for a single-user app, without pulling in a separate HTML
    // sanitizer. Extend with further extensions (tables, etc.) here as note content needs grow.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .Build();

    public string RenderToHtml(string markdown) => Markdown.ToHtml(markdown, Pipeline);
}
