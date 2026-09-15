using System.Security.Claims;
using JNotes.Core.Users;

namespace JNotes.Web.Auth;

/// <summary>
/// Real replacement for the old, throwaway <c>CookieCurrentUserContext</c> — reads the id off the
/// authenticated <see cref="ClaimsPrincipal"/> (a real signed cookie, or an OIDC-derived one; both
/// produce the same claim shape via <c>ClaimsPrincipalFactory</c>) instead of an unsigned cookie.
/// </summary>
/// <remarks>
/// Same constructor-timing constraint as before — see <see cref="ICurrentUserContext"/>'s doc
/// comment — except now it's simpler to satisfy: <c>HttpContext.User</c> is populated by the auth
/// middleware well before any page's first lifecycle method runs, on every request, always.
/// </remarks>
internal sealed class AuthenticatedCurrentUserContext : ICurrentUserContext
{
    private readonly Guid? _userId;

    public AuthenticatedCurrentUserContext(IHttpContextAccessor httpContextAccessor)
    {
        var httpContext = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("No HttpContext available to resolve the current user.");

        // Read (not thrown on) here even when nobody's authenticated: @inject constructs this
        // service the moment any component asks for it, whether or not that component ever
        // actually reads UserId — App.razor does, on every page including /login and /setup,
        // where there's no signed-in user yet. Throwing only from the UserId getter means those
        // pages (which never read UserId) work fine; only a page that genuinely needs the current
        // user without being behind [Authorize] would hit this.
        var claim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        _userId = Guid.TryParse(claim, out var id) ? id : null;
    }

    public Guid UserId => _userId
        ?? throw new InvalidOperationException("Current user is not authenticated — this page must be behind [Authorize].");
}
