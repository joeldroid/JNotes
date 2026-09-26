using JNotes.Core.Images;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace JNotes.Core.Serialization;

/// <summary>The PDF export's rendered body and the stored image files it references.</summary>
public sealed record PdfMarkdownResult(string Html, IReadOnlyList<string> ImageFileNames);

/// <summary>
/// Renders a text note's markdown for the PDF export. Nothing user-controlled may make Chromium
/// fetch, so only JNotes-stored images (<c>images/{stored name}</c>) stay images — they are rewritten
/// to the bare file name Gotenberg serves beside <c>index.html</c> — and every other image (external
/// URL, <c>data:</c>, odd path) becomes a plain link. Shares <see cref="MarkdownRenderer.CreateBaseBuilder"/>
/// with the in-app renderer, minus <see cref="ImageLinkMarkdownExtension"/>.
/// </summary>
public static class PdfMarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = MarkdownRenderer.CreateBaseBuilder().Build();

    public static PdfMarkdownResult Render(string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        var imageFileNames = new List<string>();

        foreach (var link in document.Descendants<LinkInline>().ToList())
        {
            if (!link.IsImage)
            {
                continue;
            }

            if (ImageLinks.ToStoredFileName(link.Url ?? "") is { } fileName)
            {
                link.Url = fileName;
                if (!imageFileNames.Contains(fileName, StringComparer.Ordinal))
                {
                    imageFileNames.Add(fileName);
                }

                continue;
            }

            link.IsImage = false;
            if (link.FirstChild is null)
            {
                // An empty alt would otherwise leave an invisible link.
                link.AppendChild(new LiteralInline(link.Url ?? ""));
            }
        }

        return new PdfMarkdownResult(document.ToHtml(Pipeline), imageFileNames);
    }
}
