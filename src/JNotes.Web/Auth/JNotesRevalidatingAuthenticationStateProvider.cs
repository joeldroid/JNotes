using System.Security.Claims;
using JNotes.Core.Users;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace JNotes.Web.Auth;

/// <summary>
/// Catches the one gap <see cref="CookiePrincipalValidator"/> can't: that validator only runs when
/// the cookie middleware handles an actual HTTP request (circuit connect, full reload, SignalR
/// reconnect) — it never fires again for the rest of an already-open circuit sitting on a live
/// SignalR connection with no further requests to hang a check on. A user deleted or demoted while
/// they have a note open in a live tab would otherwise stay "signed in" for that circuit
/// indefinitely. This is Blazor Server's purpose-built answer: it runs on its own timer *inside*
/// the circuit. Returning false here makes the framework treat the circuit as unauthenticated,
/// which <c>Routes.razor</c>'s <c>AuthorizeRouteView.NotAuthorized</c> already handles by
/// redirecting to <c>/login</c> — no separate sign-out call needed.
/// </summary>
internal sealed class JNotesRevalidatingAuthenticationStateProvider(
    ILoggerFactory loggerFactory,
    IServiceScopeFactory scopeFactory)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(10);

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();

        var idClaim = authenticationState.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(idClaim, out var userId))
        {
            return false;
        }

        var user = await userRepository.GetUserByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return false; // deleted since this circuit was opened
        }

        // Also catches a role change (promotion/demotion) mid-session, not just deletion.
        var roleClaim = authenticationState.User.FindFirst(ClaimTypes.Role)?.Value;
        return roleClaim == user.Role.ToString();
    }
}
