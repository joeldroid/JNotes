namespace JNotes.Core.Sharing;

/// <summary>
/// The caller can see the note (or is otherwise entitled to know it exists) but may not do this to it —
/// e.g. a Read recipient saving, or a recipient trying to manage shares. Distinct from
/// <see cref="Storage.NoteNotFoundException"/>, which means "no access at all".
/// </summary>
public sealed class NoteAccessDeniedException(Guid noteId, string message) : Exception(message)
{
    public Guid NoteId { get; } = noteId;
}
