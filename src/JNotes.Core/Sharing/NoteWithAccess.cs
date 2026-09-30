namespace JNotes.Core.Sharing;

/// <summary>A note together with the current user's access to it.</summary>
public sealed record NoteWithAccess(Models.Note Note, NoteAccess Access);
