using JNotes.Core.Images;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace JNotes.Core.Serialization;

/// <summary>
/// Rewrites an uploaded image's note-relative link (<c>images/{file}</c>) to the authenticated route
/// the app serves it from. Only the exact stored-name shape is touched — see <see cref="ImageLinks.ToServedUrl"/>.
/// </summary>
public sealed class ImageLinkMarkdownExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline) =>
        pipeline.DocumentProcessed += RewriteImageLinks;

    // NOTE: fully qualified for the same reason as in MermaidMarkdownExtension — an unqualified
    // IMarkdownRenderer would bind to JNotes's own interface.
    public void Setup(MarkdownPipeline pipeline, Markdig.Renderers.IMarkdownRenderer renderer)
    {
    }

    private static void RewriteImageLinks(MarkdownDocument document)
    {
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (link.IsImage && link.Url is { } url && ImageLinks.ToServedUrl(url) is { } served)
            {
                link.Url = served;
            }
        }
    }
}
