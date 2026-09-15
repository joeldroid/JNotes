namespace JNotes.Core.Data;

/// <summary>
/// A user account. Named <c>UserAccount</c>, not <c>User</c>, to avoid colliding with
/// <see cref="System.Security.Claims.ClaimsPrincipal"/>/ASP.NET Core auth's ambient "User" naming.
/// </summary>
public sealed class UserAccount
{
    public required Guid Id { get; set; }

    public required string Username { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }

    public required UserRole Role { get; set; }

    /// <summary>Hashed via <see cref="Microsoft.AspNetCore.Identity.PasswordHasher{TUser}"/>. Null for an OIDC-only account.</summary>
    public string? PasswordHash { get; set; }

    /// <summary>
    /// Used only to match this account against an OIDC login's <c>email</c> claim before it's
    /// linked (see <see cref="OidcSubject"/>) — not required for password-only accounts.
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// The IdP's <c>sub</c> claim, set once on this account's first successful OIDC login (an
    /// admin can't type this in ahead of time — it's opaque and IdP-specific). Null until linked.
    /// </summary>
    public string? OidcSubject { get; set; }
}
