using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;

namespace JNotes.Core.Serialization;

/// <summary>Installs <see cref="MermaidCodeBlockRenderer"/> into the HTML renderer.</summary>
public sealed class MermaidMarkdownExtension : IMarkdownExtension
{
    // Nothing to add to the parser: a mermaid diagram is an ordinary fenced code block, which is
    // exactly why a note file stays portable (GitHub/Obsidian render ```mermaid fences too).
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
    }

    // NOTE: the parameter type must stay fully qualified. This namespace already has JNotes's own
    // IMarkdownRenderer, and an unqualified name would bind to that and silently fail to implement
    // IMarkdownExtension.
    public void Setup(MarkdownPipeline pipeline, Markdig.Renderers.IMarkdownRenderer renderer)
    {
        if (renderer is HtmlRenderer html)
        {
            html.ObjectRenderers.Replace<CodeBlockRenderer>(new MermaidCodeBlockRenderer());
        }
    }
}
