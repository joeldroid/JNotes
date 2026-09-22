using JNotes.Core.Data;
using JNotes.Core.Notes;
using Microsoft.EntityFrameworkCore;

namespace JNotes.Core.Indexing;

/// <inheritdoc cref="INoteIndexRepository"/>
/// <remarks>
/// Schema: one NoteIndexEntries row per note (see <see cref="NoteIndexEntry"/>) plus one
/// NoteIndexTags row per tag (see <see cref="NoteIndexTag"/>), in the same PostgreSQL database as
/// users/settings — one connection string, one migration history, one MigrateAsync at startup. The
/// index is still a rebuildable cache (see <see cref="NoteIndexReconciler"/>), so truncating both
/// tables is never a correctness problem, only a one-time rescan.
/// Text search is ILIKE '%term%', not to_tsvector/plainto_tsquery: it has to keep matching exactly
/// what the Redis implementation's case-insensitive Contains matched, partial words included.
/// Singleton, like the Redis repository it replaces: it creates a context per call through the
/// singleton IDbContextFactory, the same way SettingsService and EfUserRepository do.
/// </remarks>
public sealed class PostgresNoteIndexRepository : INoteIndexRepository
{
    private readonly IDbContextFactory<JNotesDbContext> _dbContextFactory;

    public PostgresNoteIndexRepository(IDbContextFactory<JNotesDbContext> dbContextFactory) =>
        _dbContextFactory = dbContextFactory;

    public async Task<IReadOnlyList<NoteSummary>> SearchAsync(Guid userId, NoteSearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var entries = db.NoteIndex.AsNoTracking().Where(e => e.UserId == userId);

        if (query.Type is { } type)
        {
            var typeName = type.ToString();
            entries = entries.Where(e => e.NoteType == typeName);
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var pattern = SqlLikePattern.ContainsPattern(query.SearchText);
            entries = entries.Where(e =>
                EF.Functions.ILike(e.Title, pattern, SqlLikePattern.EscapeString) ||
                EF.Functions.ILike(e.Body, pattern, SqlLikePattern.EscapeString) ||
                db.NoteIndexTags.Any(t => t.NoteIndexEntryId == e.Id
                    && EF.Functions.ILike(t.Tag, pattern, SqlLikePattern.EscapeString)));
        }

        if (!string.IsNullOrWhiteSpace(query.Tag))
        {
            var tag = query.Tag;
            // Exact and case-sensitive, matching the old List<string>.Contains(tag).
            entries = entries.Where(e => db.NoteIndexTags.Any(t => t.NoteIndexEntryId == e.Id && t.Tag == tag));
        }

        // Projected to an anonymous type and mapped afterwards: Enum.Parse has no SQL translation.
        var rows = await entries
            .OrderByDescending(e => e.UpdatedAt)
            .Select(e => new
            {
                e.Id,
                e.Title,
                e.NoteType,
                e.CreatedAt,
                e.UpdatedAt,
                Tags = db.NoteIndexTags
                    .Where(t => t.NoteIndexEntryId == e.Id)
                    .OrderBy(t => t.Ordinal)
                    .Select(t => t.Tag)
                    .ToList(),
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new NoteSummary(
                r.Id,
                r.Title,
                Enum.Parse<Models.NoteType>(r.NoteType, ignoreCase: true),
                r.Tags,
                r.CreatedAt,
                r.UpdatedAt))
            .ToList();
    }

    public async Task<IReadOnlyList<string>> GetDistinctTagsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var tags = await (from t in db.NoteIndexTags.AsNoTracking()
                          join e in db.NoteIndex.AsNoTracking() on t.NoteIndexEntryId equals e.Id
                          where e.UserId == userId
                          select t.Tag)
            .Distinct()
            .ToListAsync(cancellationToken);

        // Sorted in memory, exactly as the Redis version did: ORDER BY in SQL would use the database's
        // collation, which orders mixed case and punctuation differently from StringComparer.Ordinal.
        // The list is one user's distinct tags — tiny.
        return tags.OrderBy(t => t, StringComparer.Ordinal).ToList();
    }

    public async Task UpsertAsync(NoteIndexEntry entry, IReadOnlyList<string> tags, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        // Explicit transaction because ExecuteDeleteAsync runs immediately, outside the change tracker: a
        // failure between clearing the old tags and saving the new ones would otherwise leave a note with
        // none.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.NoteIndexTags
            .Where(t => t.NoteIndexEntryId == entry.Id)
            .ExecuteDeleteAsync(cancellationToken);

        var existing = await db.NoteIndex.FirstOrDefaultAsync(e => e.Id == entry.Id, cancellationToken);
        // Rounded to the store's own resolution so what comes back out of GetIndexedFileWriteTimesAsync
        // equals what went in — see IndexTimestamp.
        var indexedWriteTime = IndexTimestamp.ToIndexResolution(entry.IndexedFileWriteTimeUtc);

        if (existing is null)
        {
            // A new NoteIndexEntry rather than Add(entry) — never attach the caller's instance to a
            // context this method is about to dispose.
            db.NoteIndex.Add(new NoteIndexEntry
            {
                Id = entry.Id,
                UserId = entry.UserId,
                Title = entry.Title,
                Body = entry.Body,
                NoteType = entry.NoteType,
                CreatedAt = entry.CreatedAt,
                UpdatedAt = entry.UpdatedAt,
                IndexedFileWriteTimeUtc = indexedWriteTime,
                FilePath = entry.FilePath,
            });
        }
        else
        {
            // Assign every field, not just the changed ones: this method's contract is a full replace.
            existing.UserId = entry.UserId;
            existing.Title = entry.Title;
            existing.Body = entry.Body;
            existing.NoteType = entry.NoteType;
            existing.CreatedAt = entry.CreatedAt;
            existing.UpdatedAt = entry.UpdatedAt;
            existing.IndexedFileWriteTimeUtc = indexedWriteTime;
            existing.FilePath = entry.FilePath;
        }

        var ordinal = 0;
        foreach (var tag in tags)
        {
            db.NoteIndexTags.Add(new NoteIndexTag { NoteIndexEntryId = entry.Id, Ordinal = ordinal++, Tag = tag });
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid userId, Guid noteId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        // Scoped by userId as well as id, so one user's id can never delete another's row (the Redis
        // version deleted the hash by note id alone and only the set membership was user-scoped).
        // No-ops when nothing matches. The tags cascade.
        await db.NoteIndex
            .Where(e => e.Id == noteId && e.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, DateTimeOffset>> GetIndexedFileWriteTimesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.NoteIndex.AsNoTracking()
            .Where(e => e.UserId == userId)
            .Select(e => new { e.Id, e.IndexedFileWriteTimeUtc })
            .ToDictionaryAsync(x => x.Id, x => x.IndexedFileWriteTimeUtc, cancellationToken);
    }
}
