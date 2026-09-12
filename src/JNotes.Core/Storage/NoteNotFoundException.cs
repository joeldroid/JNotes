namespace JNotes.Core.Storage;

public sealed class NoteNotFoundException : Exception
{
    public Guid NoteId { get; }

    public NoteNotFoundException(Guid noteId)
        : base($"Note '{noteId}' was not found.")
    {
        NoteId = noteId;
    }
}
