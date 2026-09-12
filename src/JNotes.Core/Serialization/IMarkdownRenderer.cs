namespace JNotes.Core.Serialization;

/// <summary>Renders a note body's markdown to HTML for read-only preview.</summary>
public interface IMarkdownRenderer
{
    string RenderToHtml(string markdown);
}
