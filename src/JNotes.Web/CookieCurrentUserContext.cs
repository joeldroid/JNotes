using JNotes.Core.Users;

namespace JNotes.Web;

/// <summary>
/// Temporary, non-secure stand-in for real login: reads a plain, unsigned "who am I acting as"
/// cookie set client-side by the nav's "act as" dropdown (see <c>NavMenu.razor</c> and
/// <c>wwwroot/js/act-as-cookie.js</c>) — trivially spoofable, not a security boundary, purely so
/// per-user file/index scoping can be exercised end to end before real authentication exists. This
/// whole class is meant to be deleted, not extended, once real login lands.
/// </summary>
/// <remarks>
/// All <see cref="IHttpContextAccessor.HttpContext"/> access happens here, once, synchronously, in
/// the constructor — see <see cref="ICurrentUserContext"/>'s doc comment for why that's the only
/// point in this service's lifetime <c>HttpContext</c> is guaranteed to be non-null in Blazor
/// Server.
/// </remarks>
internal sealed class CookieCurrentUserContext : ICurrentUserContext
{
    public const string CookieName = "actAsUserId";

    public Guid UserId { get; }

    public CookieCurrentUserContext(IHttpContextAccessor httpContextAccessor, IDefaultUserProvider defaultUserProvider)
    {
        var cookieValue = httpContextAccessor.HttpContext?.Request.Cookies[CookieName];

        // Falls back to the default user not just when the cookie is missing/malformed, but also
        // when it names a user that no longer exists — e.g. a leftover cookie from before a
        // fresh-start database reset (see README's upgrade note). Without this check, acting as a
        // stale id doesn't fail until the first write, as an unhandled FK-constraint crash.
        UserId = Guid.TryParse(cookieValue, out var id) && defaultUserProvider.IsKnownUser(id)
            ? id
            : defaultUserProvider.DefaultUserId;
    }
}
