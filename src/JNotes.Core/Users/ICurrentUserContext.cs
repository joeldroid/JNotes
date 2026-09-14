namespace JNotes.Core.Users;

/// <summary>
/// Who the current request/circuit is acting as. Scoped — resolved once per request/circuit.
/// <c>NoteService</c> and the Settings page's per-user theme lookups depend on this; Core has no
/// implementation of its own (that lives in <c>JNotes.Web</c> — see
/// <c>CookieCurrentUserContext</c> — since it inherently needs ASP.NET Core's HTTP context, a
/// hosting-layer concern Core doesn't otherwise depend on).
/// </summary>
/// <remarks>
/// Implementations that read from <c>IHttpContextAccessor</c> can only do so reliably from their
/// constructor — Blazor Server only guarantees a live <c>HttpContext</c> while the DI scope
/// backing a request/circuit-connect is still being built, not later during interactive
/// rendering. That in turn only works if something actually resolves this service (directly or
/// transitively, e.g. via <c>NoteService</c>) during that window — Blazor constructs scoped
/// services lazily on first use, so every routed page must resolve it (or a service that depends
/// on it) from its *first* lifecycle method (<c>OnInitializedAsync</c>/<c>OnParametersSetAsync</c>),
/// never deferred behind a later interaction, or it will resolve to a stale/default value.
/// </remarks>
public interface ICurrentUserContext
{
    Guid UserId { get; }
}
