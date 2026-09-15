using System.Security.Claims;
using JNotes.Core.Users;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace JNotes.Web.Auth;

/// <summary>
/// The cheap, HTTP-layer half of session revalidation — fires whenever the cookie middleware
/// actually runs (circuit connect, full page reload, SignalR reconnect after a drop), rejecting and
/// signing out a principal for a user who's been deleted or had their role changed since the cookie
/// was issued. Doesn't cover an already-open circuit with no further HTTP requests — see
/// <see cref="JNotesRevalidatingAuthenticationStateProvider"/> for that half.
/// </summary>
internal static class CookiePrincipalValidator
{
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var idClaim = context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(idClaim, out var userId))
        {
            context.RejectPrincipal();
            return;
        }

        var userRepository = context.HttpContext.RequestServices.GetRequiredService<IUserRepository>();
        var user = await userRepository.GetUserByIdAsync(userId, context.HttpContext.RequestAborted);
        var roleClaim = context.Principal?.FindFirst(ClaimTypes.Role)?.Value;

        if (user is null || roleClaim != user.Role.ToString())
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }
}
