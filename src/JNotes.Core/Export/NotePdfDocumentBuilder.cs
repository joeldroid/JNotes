using System.Net;
using System.Reflection;
using System.Text;
using JNotes.Core.Models;
using JNotes.Core.Serialization;

namespace JNotes.Core.Export;

/// <summary>A note's self-contained PDF source document and what has to travel beside it.</summary>
public sealed record NotePdfDocument(string Html, IReadOnlyList<string> ImageFileNames, bool HasMermaid);

/// <summary>
/// Builds the light, self-contained <c>index.html</c> Gotenberg prints for a note. Pure static: every
/// user string is HTML-encoded, images are referenced by bare file name (sent as sibling files), and the
/// mermaid script is referenced only when the note has a diagram.
/// </summary>
public static class NotePdfDocumentBuilder
{
    public const string ReadyExpression = "window.jnotesPdfReady === true";

    public const string MermaidScriptFileName = "mermaid.min.js";

    private const string StylesheetResourceName = "JNotes.Core.Export.note-pdf.css";

    private const string UntitledTitle = "Untitled note";

    // Same parse-then-render approach as wwwroot/js/mermaid-preview.js, but light-themed and one-shot:
    // Gotenberg waits for window.jnotesPdfReady before printing.
    private const string ReadyScript = """
        (async () => {
          try {
            await document.fonts.ready;
            if (window.mermaid) {
              mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', theme: 'default' });
              let n = 0;
              for (const el of document.querySelectorAll('pre.mermaid')) {
                const source = el.textContent ?? '';
                if (source.trim() === '') continue;
                const id = 'jnotes-mermaid-' + (n++);
                try {
                  if (!(await mermaid.parse(source, { suppressErrors: true }))) { el.classList.add('mermaid-error'); continue; }
                  const { svg } = await mermaid.render(id, source);
                  el.innerHTML = svg; el.classList.add('mermaid-rendered');
                } catch { el.classList.add('mermaid-error'); }
                finally { document.getElementById('d' + id)?.remove(); }
              }
            }
          } finally { window.jnotesPdfReady = true; }
        })();
        """;

    private static readonly Lazy<string> Stylesheet = new(LoadStylesheet);

    /// <summary>
    /// The page footer document: "Page X of Y". Chromium footers need an explicit font-size and
    /// can't load external resources, so it's fully inline.
    /// </summary>
    public static string FooterHtml { get; } =
        "<html><head><style>body { margin: 0; }</style></head><body>" +
        "<div style=\"width:100%; text-align:center; font-family: sans-serif; font-size: 8pt; color: #757575;\">" +
        "Page <span class=\"pageNumber\"></span> of <span class=\"totalPages\"></span></div></body></html>";

    public static NotePdfDocument Build(Note note, string lastUpdatedText)
    {
        var title = string.IsNullOrWhiteSpace(note.Title) ? UntitledTitle : note.Title;
        var encodedTitle = WebUtility.HtmlEncode(title);

        var rendered = PdfMarkdownRenderer.Render(note.Body);
        const string bodyClass = "note-body markdown";
        var bodyHtml = rendered.Html;
        var imageFileNames = rendered.ImageFileNames;
        var hasMermaid = rendered.Html.Contains(MermaidCodeBlockRenderer.OpeningTag, StringComparison.Ordinal);

        var html = new StringBuilder();
        html.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>")
            .Append(encodedTitle).Append("</title><style>").Append(Stylesheet.Value).Append("</style></head>\n");
        html.Append("<body><article class=\"note\">\n<header><h1 class=\"note-title\">")
            .Append(encodedTitle).Append("</h1>\n");

        if (note.Tags.Count > 0)
        {
            html.Append("<div class=\"note-tags\">");
            foreach (var tag in note.Tags)
            {
                html.Append("<span class=\"tag\">").Append(WebUtility.HtmlEncode(tag)).Append("</span>");
            }

            html.Append("</div>\n");
        }

        html.Append("<div class=\"note-meta\">")
            .Append(WebUtility.HtmlEncode(lastUpdatedText)).Append("</div></header>\n");
        html.Append("<main class=\"").Append(bodyClass).Append("\">").Append(bodyHtml).Append("</main></article>\n");

        if (hasMermaid)
        {
            html.Append("<script src=\"").Append(MermaidScriptFileName).Append("\"></script>\n");
        }

        html.Append("<script>").Append(ReadyScript).Append("</script></body></html>");

        return new NotePdfDocument(html.ToString(), imageFileNames, hasMermaid);
    }

    private static string LoadStylesheet()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(StylesheetResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{StylesheetResourceName}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
