using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace JNotes.Core.Serialization;

/// <summary>
/// Renders a ```mermaid fenced block as &lt;pre class="mermaid"&gt;source&lt;/pre&gt; instead of
/// Markdig's default &lt;pre&gt;&lt;code class="language-mermaid"&gt;, so the browser-side mermaid
/// module (wwwroot/js/mermaid-preview.js) has one stable element to swap for an SVG. Every other
/// code block falls through to the base renderer untouched.
/// The source is written with escape: true — a note body is untrusted input, and DisableHtml()
/// elsewhere in the pipeline would otherwise be trivially bypassed by a "&lt;/pre&gt;&lt;script&gt;"
/// inside a mermaid fence. The JS reads it back with textContent, which decodes the entities, so
/// mermaid still sees exactly what the author typed.
/// </summary>
public sealed class MermaidCodeBlockRenderer : CodeBlockRenderer
{
    /// <summary>The exact opening tag written for a mermaid block (also what the PDF builder looks for).</summary>
    public const string OpeningTag = "<pre class=\"mermaid\">";

    private const string MermaidInfo = "mermaid";

    protected override void Write(HtmlRenderer renderer, CodeBlock obj)
    {
        if (obj is FencedCodeBlock fenced &&
            string.Equals(fenced.Info?.Trim(), MermaidInfo, StringComparison.OrdinalIgnoreCase))
        {
            renderer.Write(OpeningTag);
            renderer.WriteLeafRawLines(obj, writeEndOfLines: true, escape: true);
            renderer.WriteLine("</pre>");
            return;
        }

        base.Write(renderer, obj);
    }
}
