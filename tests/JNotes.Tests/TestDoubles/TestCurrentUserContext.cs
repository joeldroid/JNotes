using JNotes.Core.Users;

namespace JNotes.Tests.TestDoubles;

/// <summary>
/// Mutable <see cref="ICurrentUserContext"/> for tests — unlike the real
/// <c>CookieCurrentUserContext</c>, this can be switched mid-test (<c>NoteService</c> reads
/// <see cref="UserId"/> fresh on every call, not just once at construction) to simulate acting as
/// different users against one shared DI scope.
/// </summary>
internal sealed class TestCurrentUserContext : ICurrentUserContext
{
    public Guid UserId { get; set; }
}
