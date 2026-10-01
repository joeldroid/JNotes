namespace JNotes.Core.Sharing;

/// <summary>One recipient of a note, as the owner sees it in the share dialog.</summary>
public sealed record NoteShareEntry(Guid UserId, string Username, string DisplayName, NoteSharePermission Permission, DateTimeOffset CreatedAt);
