namespace JNotes.Core.Serialization;

/// <summary>
/// Thrown when a note's markdown file cannot be parsed (missing/malformed front matter, unknown
/// note type, etc.). Callers such as the index reconciler catch this to record a per-note error
/// without failing the whole operation — see <c>INoteIndexReconciler</c>.
/// </summary>
public sealed class NoteParseException : Exception
{
    public NoteParseException(string message)
        : base(message)
    {
    }

    public NoteParseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
