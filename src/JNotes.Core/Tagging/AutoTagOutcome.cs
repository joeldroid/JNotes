namespace JNotes.Core.Tagging;

/// <summary>What became of a note's auto-tag attempt; persisted as a string in <c>NoteAutoTagAttempts</c>.</summary>
public enum AutoTagOutcome
{
    /// <summary>Tags were written. Final: tags a user later removes never come back.</summary>
    Tagged,

    /// <summary>The model had nothing useful to add. Final.</summary>
    NoTags,

    /// <summary>The file isn't in the canonical format JNotes writes (or won't parse), so it is left alone. Final.</summary>
    Skipped,

    /// <summary>A failure caused by this note; retried after a backoff (see <see cref="AutoTagSchedule"/>).</summary>
    Failed,

    /// <summary>Failed <see cref="AutoTagSchedule.MaxFailures"/> times. Final.</summary>
    GaveUp,
}
