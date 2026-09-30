namespace JNotes.Core.Sharing;

/// <summary>On a list row for a note someone else owns: who owns it and what the current user may do.</summary>
public sealed record SharedNoteInfo(Guid OwnerUserId, string OwnerDisplayName, NoteSharePermission Permission);
