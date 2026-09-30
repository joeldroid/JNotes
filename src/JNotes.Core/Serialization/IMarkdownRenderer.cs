namespace JNotes.Core.Serialization;

/// <summary>Renders a note body's markdown to HTML for read-only preview.</summary>
public interface IMarkdownRenderer
{
    /// <summary>Uploaded images (<c>images/{file}</c>) are served from the current user's own <c>/images/{file}</c> route.</summary>
    string RenderToHtml(string markdown);

    /// <summary>
    /// Like <see cref="RenderToHtml(string)"/>, but uploaded images are served from the note-scoped route
    /// <c>/notes/{noteScopedImagesFor}/images/{file}</c> — for a viewer who isn't the note's owner.
    /// </summary>
    string RenderToHtml(string markdown, Guid noteScopedImagesFor);
}
