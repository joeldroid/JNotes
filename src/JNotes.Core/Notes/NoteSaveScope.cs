namespace JNotes.Core.Notes;

/// <summary>What a save to <see cref="INoteService.AutosaveAsync"/> actually needs to touch.</summary>
public enum NoteSaveScope
{
    /// <summary>Only the note's body changed — write the file, skip the index (body isn't indexed).</summary>
    ContentOnly,

    /// <summary>Title and/or tags changed — write the file and update the index (both are indexed).</summary>
    Metadata,
}
