namespace JNotes.Core.Data;

/// <summary>
/// A user account. Named <c>UserAccount</c>, not <c>User</c>, to avoid colliding with
/// <see cref="System.Security.Claims.ClaimsPrincipal"/>/ASP.NET Core auth's ambient "User" naming
/// that a later, separate login feature will introduce.
/// </summary>
public sealed class UserAccount
{
    public required Guid Id { get; set; }

    public required string Username { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }
}
