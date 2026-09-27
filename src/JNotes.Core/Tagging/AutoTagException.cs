namespace JNotes.Core.Tagging;

/// <summary>
/// Ollama was unreachable, timed out, errored or returned garbage; <see cref="Kind"/> says which. The
/// message is safe to show or log (no URL, no note content); details stay in Debug logs.
/// </summary>
public sealed class AutoTagException(string message, AutoTagFailureKind kind, Exception? inner = null) : Exception(message, inner)
{
    public AutoTagFailureKind Kind { get; } = kind;
}
