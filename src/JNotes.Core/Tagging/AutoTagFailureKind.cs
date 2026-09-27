namespace JNotes.Core.Tagging;

/// <summary>
/// Why a call to the tagging model failed — decides what a background pass does next, since the
/// fault is either Ollama's (stop, and don't blame the note) or the note's (record it and back off).
/// </summary>
public enum AutoTagFailureKind
{
    /// <summary>
    /// Ollama can't be reached, is overloaded or erroring (5xx, 429), or hasn't got the model (404).
    /// Not the note's fault: the pass stops at once and nothing is recorded per note.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The model didn't answer in time (a huge note, or a cold or busy server). Recorded against the
    /// note with backoff, and the pass stops so one slow note can't hold up the whole queue.
    /// </summary>
    Timeout,

    /// <summary>
    /// Ollama answered but the reply was unusable (unparseable output, a 200 carrying an error, another
    /// 4xx). Recorded against the note with backoff; the pass carries on with the next note.
    /// </summary>
    BadResponse,
}
