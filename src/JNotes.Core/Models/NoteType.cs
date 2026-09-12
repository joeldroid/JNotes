namespace JNotes.Core.Models;

/// <summary>
/// The kind of note, which determines how its <see cref="Note.Body"/> markdown is edited and rendered.
/// </summary>
public enum NoteType
{
    /// <summary>Free-form markdown, edited as text and rendered as a preview.</summary>
    Text,

    /// <summary>A markdown task list (<c>- [ ] item</c>), edited as interactive checkboxes.</summary>
    Checklist,
}
