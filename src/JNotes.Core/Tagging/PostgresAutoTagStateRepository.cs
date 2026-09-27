using JNotes.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace JNotes.Core.Tagging;

/// <inheritdoc cref="IAutoTagStateRepository"/>
/// <remarks>Singleton over the singleton <see cref="IDbContextFactory{TContext}"/>, like <c>PostgresNoteIndexRepository</c>.</remarks>
public sealed class PostgresAutoTagStateRepository : IAutoTagStateRepository
{
    private readonly IDbContextFactory<JNotesDbContext> _dbContextFactory;

    public PostgresAutoTagStateRepository(IDbContextFactory<JNotesDbContext> dbContextFactory) =>
        _dbContextFactory = dbContextFactory;

    public async Task<IReadOnlyList<Guid>> GetCandidateNoteIdsAsync(
        Guid userId, DateTimeOffset updatedBeforeUtc, DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await db.NoteIndex.AsNoTracking()
            .Where(e => e.UserId == userId && e.UpdatedAt < updatedBeforeUtc)
            .Where(e => e.Title.Trim() != "" || e.Body.Trim() != "")
            .Where(e => !db.NoteIndexTags.Any(t => t.NoteIndexEntryId == e.Id))
            .Where(e => !db.NoteAutoTagAttempts.Any(a => a.UserId == userId && a.NoteId == e.Id
                && (a.Outcome != AutoTagOutcome.Failed || a.RetryAfterUtc > nowUtc)))
            .OrderByDescending(e => e.UpdatedAt)
            .Select(e => e.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task RecordOutcomeAsync(Guid userId, Guid noteId, AutoTagOutcome outcome, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var row = await FindAsync(db, userId, noteId, cancellationToken);
        row.Outcome = outcome;
        row.LastAttemptUtc = nowUtc;
        row.RetryAfterUtc = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordFailureAsync(Guid userId, Guid noteId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var row = await FindAsync(db, userId, noteId, cancellationToken);
        row.FailureCount++;
        (row.Outcome, row.RetryAfterUtc) = AutoTagSchedule.AfterFailure(row.FailureCount, nowUtc);
        row.LastAttemptUtc = nowUtc;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<NoteAutoTagAttempt?> GetAsync(Guid userId, Guid noteId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await db.NoteAutoTagAttempts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.UserId == userId && a.NoteId == noteId, cancellationToken);
    }

    // The tracked row for (userId, noteId), added (not yet saved) with a placeholder outcome if there isn't one.
    private static async Task<NoteAutoTagAttempt> FindAsync(JNotesDbContext db, Guid userId, Guid noteId, CancellationToken cancellationToken)
    {
        var row = await db.NoteAutoTagAttempts.SingleOrDefaultAsync(a => a.UserId == userId && a.NoteId == noteId, cancellationToken);
        if (row is null)
        {
            row = new NoteAutoTagAttempt
            {
                UserId = userId,
                NoteId = noteId,
                Outcome = AutoTagOutcome.Failed,
                LastAttemptUtc = default,
            };
            db.NoteAutoTagAttempts.Add(row);
        }

        return row;
    }
}
