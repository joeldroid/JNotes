namespace JNotes.Core.Users;

/// <summary>
/// Whether the very first (admin) <see cref="Data.UserAccount"/> has been created yet. Backs the
/// "redirect everything to /setup until an admin exists" gate — cached in-memory so that check
/// costs zero database I/O per request. See <c>HostExtensions.InitializeSetupStateAsync</c> (seeds
/// it at startup) and <c>SetupStateProvider</c> (flips it the instant setup completes).
/// </summary>
public interface ISetupStateProvider
{
    bool IsSetupComplete { get; }
}
