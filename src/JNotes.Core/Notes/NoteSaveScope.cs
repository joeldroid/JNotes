namespace JNotes.Core.Notes;

/// <summary>What a save to <see cref="INoteService.AutosaveAsync"/> actually needs to touch.</summary>
public enum NoteSaveScope
{
    /// <summary>
    /// Only the note's body changed — write the file and update the index row's Body/UpdatedAt (one row; title, tag
    /// rows and the indexed mtime are left alone, so the reconciler still re-parses the note once).
    /// </summary>
    ContentOnly,

    /// <summary>Title and/or tags changed — write the file and fully replace the index row and its tag rows.</summary>
    Metadata,
}
