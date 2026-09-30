namespace JNotes.Core.Sharing;

/// <summary>An incoming grant: who owns the note (with a display name) and what the recipient may do.</summary>
public sealed record NoteShareGrant(Guid OwnerUserId, NoteSharePermission Permission, string OwnerDisplayName);
