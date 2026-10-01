namespace JNotes.Core.Sharing;

/// <summary>A user the owner may share a note with. Never carries an email address.</summary>
public sealed record ShareRecipientCandidate(Guid UserId, string Username, string DisplayName);
