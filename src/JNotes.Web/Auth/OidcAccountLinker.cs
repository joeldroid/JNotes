using System.Security.Claims;
using JNotes.Core.Users;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace JNotes.Web.Auth;

/// <summary>
/// Runs in normal request-pipeline context on the OIDC callback (not inside a live Blazor circuit,
/// so full async/DB access is fine here — none of <see cref="ICurrentUserContext"/>'s
/// constructor-timing constraints apply). Implements the locked-in provisioning policy: an admin
/// must have pre-created the account (no auto-provisioning) — this only ever links an existing,
/// unlinked account to the IdP's subject, never creates one.
/// </summary>
internal static class OidcAccountLinker
{
    public static async Task OnTokenValidatedAsync(TokenValidatedContext context)
    {
        var userRepository = context.HttpContext.RequestServices.GetRequiredService<IUserRepository>();
        var principal = context.Principal!;

        // MapInboundClaims = false means these come through under their raw IdP names, not the
        // legacy long-URI ClaimTypes.* remapping.
        var subject = principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var email = principal.FindFirst("email")?.Value
            ?? principal.FindFirst(ClaimTypes.Email)?.Value
            ?? principal.FindFirst("preferred_username")?.Value;

        if (string.IsNullOrEmpty(subject))
        {
            context.HandleResponse();
            context.Response.Redirect("/login?error=no-account");
            return;
        }

        // Already-linked subject takes priority over email: once linked, a later email change at
        // the IdP shouldn't break login, and a stale/reused email must never silently relink a
        // different account.
        var user = await userRepository.GetUserByOidcSubjectAsync(subject, context.HttpContext.RequestAborted);
        user ??= email is null ? null : await userRepository.GetUnlinkedUserByEmailAsync(email, context.HttpContext.RequestAborted);

        if (user is null)
        {
            context.HandleResponse();
            context.Response.Redirect("/login?error=no-account");
            return;
        }

        if (user.OidcSubject is null)
        {
            await userRepository.LinkOidcSubjectAsync(user.Id, subject, context.HttpContext.RequestAborted);
        }

        // Replace the IdP's own claims with JNotes' own shape — downstream code (ICurrentUserContext,
        // [Authorize(Roles = ...)]) can't tell OIDC and password logins apart afterward.
        context.Principal = ClaimsPrincipalFactory.Create(user, context.Scheme.Name);
    }
}
