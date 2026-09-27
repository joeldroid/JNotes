namespace JNotes.Core.Tagging;

/// <summary>Pure helper for merging per-user candidate lists into one fair, capped work list.</summary>
public static class AutoTagCandidates
{
    /// <summary>
    /// Round-robin across users — each user's first note, then each user's second, and so on — stopping at
    /// <paramref name="max"/>, so one user with a huge backlog can't starve the others. The input order is
    /// preserved (the caller shuffles the users); a user with no notes contributes nothing.
    /// </summary>
    public static IReadOnlyList<(Guid UserId, Guid NoteId)> Interleave(
        IReadOnlyList<(Guid UserId, IReadOnlyList<Guid> NoteIds)> perUser, int max)
    {
        var result = new List<(Guid UserId, Guid NoteId)>();
        if (max <= 0)
        {
            return result;
        }

        for (var round = 0; ; round++)
        {
            var tookAny = false;
            foreach (var (userId, noteIds) in perUser)
            {
                if (round >= noteIds.Count)
                {
                    continue;
                }

                result.Add((userId, noteIds[round]));
                tookAny = true;
                if (result.Count >= max)
                {
                    return result;
                }
            }

            if (!tookAny)
            {
                return result;
            }
        }
    }
}
