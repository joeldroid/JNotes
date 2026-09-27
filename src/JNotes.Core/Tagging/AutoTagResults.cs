namespace JNotes.Core.Tagging;

/// <summary>
/// What one background pass did. <c>Deferred</c> counts notes left for a later pass (too recent, changed
/// while the model ran, already tagged, gone, or blank); <see cref="UnavailableReason"/> is set when the pass
/// stopped early because Ollama was unavailable or timed out (a user-safe, URL-free message).
/// </summary>
public sealed record AutoTagPassResult(
    int UsersProcessed,
    int UsersFailed,
    int Tagged,
    int NoTags,
    int Skipped,
    int Failed,
    int Deferred,
    string? UnavailableReason)
{
    public bool ServiceUnavailable => UnavailableReason is not null;
}

/// <summary>What happened to one note in a pass.</summary>
internal enum AutoTagNoteResult
{
    Tagged,
    NoTags,
    Skipped,
    Failed,
    Deferred,
}
