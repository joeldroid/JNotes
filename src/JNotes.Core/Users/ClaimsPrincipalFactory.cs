using System.Security.Claims;
using JNotes.Core.Data;

namespace JNotes.Core.Users;

/// <summary>
/// Builds the <see cref="ClaimsPrincipal"/> JNotes signs in with — pure <see cref="System.Security.Claims"/>
/// logic (no ASP.NET Core dependency, so this can live in Core). Used identically by the password
/// login handler and OIDC's token-validated handler (both in <c>JNotes.Web</c>), so the resulting
/// claim shape is the same no matter which method was used to sign in — nothing downstream
/// (<c>AuthenticatedCurrentUserContext</c>, <c>[Authorize(Roles = ...)]</c>) needs to care.
/// </summary>
public static class ClaimsPrincipalFactory
{
    public static ClaimsPrincipal Create(UserAccount user, string authenticationType)
    {
        var identity = new ClaimsIdentity(authenticationType);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Name, user.Username));
        identity.AddClaim(new Claim(ClaimTypes.Role, user.Role.ToString()));
        return new ClaimsPrincipal(identity);
    }
}
