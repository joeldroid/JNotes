namespace JNotes.Core.Sharing;

/// <summary>
/// The current user's access to one note, resolved on every call. <paramref name="OwnerDisplayName"/> is
/// <c>null</c> for the owner themself.
/// </summary>
public sealed record NoteAccess(NoteAccessLevel Level, Guid OwnerUserId, string? OwnerDisplayName)
{
    public bool IsOwner => Level == NoteAccessLevel.Owner;

    public bool CanEditBody => Level != NoteAccessLevel.Read;
}
