using JNotes.Core.Data;
using JNotes.Core.Notes;
using JNotes.Core.Sharing;
using JNotes.Core.Users;
using Microsoft.EntityFrameworkCore;

namespace JNotes.Core.Indexing;

/// <inheritdoc cref="INoteIndexRepository"/>
/// <remarks>
/// Schema: one NoteIndexEntries row per note (see <see cref="NoteIndexEntry"/>), one
/// NoteIndexTags row per tag (see <see cref="NoteIndexTag"/>), and one NoteIndexUserTags row per
/// (user, distinct tag) (see <see cref="NoteIndexUserTag"/>, a projection of NoteIndexTags kept in
/// sync inside the same transaction), in the same PostgreSQL database as
/// users/settings — one connection string, one migration history, one MigrateAsync at startup. The
/// index is still a rebuildable cache (see <see cref="NoteIndexReconciler"/>), so truncating these
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
        if (query.Limit is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(query), query.Limit, "Limit must be at least 1.");
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        // Requiring s.OwnerUserId == e.UserId means a stale or foreign share row can never expose an entry from
        // a folder its "owner" doesn't own.
        var entries = db.NoteIndex.AsNoTracking();
        entries = query.Scope == NoteListScope.SharedWithMe
            ? entries.Where(e => db.NoteShares.Any(s => s.NoteId == e.Id && s.OwnerUserId == e.UserId && s.SharedWithUserId == userId))
            : entries.Where(e => e.UserId == userId
                || db.NoteShares.Any(s => s.NoteId == e.Id && s.OwnerUserId == e.UserId && s.SharedWithUserId == userId));

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

        // Id breaks UpdatedAt ties so a LIMIT is deterministic. The Take sits before the Select so it becomes a SQL
        // LIMIT and the per-row subqueries below only run for the rows kept.
        var ordered = entries.OrderByDescending(e => e.UpdatedAt).ThenByDescending(e => e.Id);
        IQueryable<NoteIndexEntry> page = query.Limit is { } limit ? ordered.Take(limit) : ordered;

        // Projected to an anonymous type and mapped afterwards: the SharedNoteInfo construction has no SQL translation.
        var rows = await page
            .Select(e => new
            {
                e.Id,
                e.UserId,
                e.Title,
                e.CreatedAt,
                e.UpdatedAt,
                OutgoingShareCount = e.UserId == userId
                    ? db.NoteShares.Count(s => s.OwnerUserId == userId && s.NoteId == e.Id)
                    : 0,
                Permission = db.NoteShares
                    .Where(s => s.NoteId == e.Id && s.OwnerUserId == e.UserId && s.SharedWithUserId == userId)
                    .Select(s => (NoteSharePermission?)s.Permission)
                    .FirstOrDefault(),
                Owner = db.Users
                    .Where(u => u.Id == e.UserId && e.UserId != userId)
                    .Select(u => new { u.Username, u.FirstName, u.LastName })
                    .FirstOrDefault(),
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
                r.Tags,
                r.CreatedAt,
                r.UpdatedAt)
            {
                OwnerUserId = r.UserId,
                OutgoingShareCount = r.OutgoingShareCount,
                SharedWithMe = r.UserId != userId && r.Permission is { } permission && r.Owner is { } owner
                    ? new SharedNoteInfo(r.UserId, UserDisplayName.Format(owner.FirstName, owner.LastName, owner.Username), permission)
                    : null,
            })
            .ToList();
    }

    // Own-only on purpose: recipients can't edit tags, so the tag vocabulary (auto-suggest, distinct tags) never
    // includes tags from notes shared with the user.
    public async Task<IReadOnlyList<string>> GetDistinctTagsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var tags = await db.NoteIndexUserTags.AsNoTracking()
            .Where(u => u.UserId == userId)
            .Select(u => u.Tag)
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

        // The tags this note carried before, so their NoteIndexUserTags rows can be recomputed too.
        var oldTags = await db.NoteIndexTags
            .Where(t => t.NoteIndexEntryId == entry.Id)
            .Select(t => t.Tag)
            .ToListAsync(cancellationToken);

        await db.NoteIndexTags
            .Where(t => t.NoteIndexEntryId == entry.Id)
            .ExecuteDeleteAsync(cancellationToken);

        var existing = await db.NoteIndex.FirstOrDefaultAsync(e => e.Id == entry.Id, cancellationToken);
        var previousUserId = existing?.UserId;
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

        // All the tags, not just the changed ones: LastUsedAt depends on this entry's UpdatedAt.
        var affectedTags = oldTags.Concat(tags).Distinct(StringComparer.Ordinal).ToArray();
        await RecomputeUserTagsAsync(db, entry.UserId, affectedTags, cancellationToken);
        if (previousUserId is { } oldUserId && oldUserId != entry.UserId)
        {
            // Can't happen with GUID ids in practice, but the entry changed owner: the old owner's rows shrink.
            await RecomputeUserTagsAsync(db, oldUserId, affectedTags, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpdateContentAsync(Guid userId, Guid noteId, string body, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // IndexedFileWriteTimeUtc is deliberately not set — see INoteIndexRepository.UpdateContentAsync.
        var rows = await db.NoteIndex
            .Where(e => e.Id == noteId && e.UserId == userId && e.UpdatedAt <= updatedAt)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Body, body)
                .SetProperty(e => e.UpdatedAt, updatedAt), cancellationToken);
        if (rows == 0)
        {
            return;
        }

        // NoteIndexUserTags.LastUsedAt is the max UpdatedAt of a tag's notes, so the bumped UpdatedAt can move it.
        // The tag rows themselves are untouched; a note carries a handful of tags, so this stays cheap.
        var tags = await db.NoteIndexTags
            .Where(t => t.NoteIndexEntryId == noteId)
            .Select(t => t.Tag)
            .ToArrayAsync(cancellationToken);
        await RecomputeUserTagsAsync(db, userId, tags, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid userId, Guid noteId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Scoped by userId as well as id, so one user's id can never delete another's row (the Redis
        // version deleted the hash by note id alone and only the set membership was user-scoped).
        // No-ops when nothing matches (the tag read is empty and the recompute returns immediately).
        // The tags cascade.
        var tags = await (from t in db.NoteIndexTags
                          join e in db.NoteIndex on t.NoteIndexEntryId equals e.Id
                          where e.Id == noteId && e.UserId == userId
                          select t.Tag)
            .Distinct()
            .ToArrayAsync(cancellationToken);

        await db.NoteIndex
            .Where(e => e.Id == noteId && e.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await RecomputeUserTagsAsync(db, userId, tags, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, DateTimeOffset>> GetIndexedFileWriteTimesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.NoteIndex.AsNoTracking()
            .Where(e => e.UserId == userId)
            .Select(e => new { e.Id, e.IndexedFileWriteTimeUtc })
            .ToDictionaryAsync(x => x.Id, x => x.IndexedFileWriteTimeUtc, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> SuggestTagsAsync(Guid userId, string? term, IReadOnlyCollection<string> exclude, int limit, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 50);
        var excluded = exclude.Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).Distinct().ToArray();

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var tags = db.NoteIndexUserTags.AsNoTracking()
            .Where(u => u.UserId == userId && !excluded.Contains(u.Tag.ToLower()));

        var trimmed = term?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return await tags
                .OrderByDescending(u => u.NoteCount)
                .ThenByDescending(u => u.LastUsedAt)
                .ThenBy(u => u.Tag)
                .Select(u => u.Tag)
                .Take(limit)
                .ToListAsync(cancellationToken);
        }

        var contains = SqlLikePattern.ContainsPattern(trimmed);
        var prefix = SqlLikePattern.Escape(trimmed) + "%";
        return await tags
            .Where(u => EF.Functions.ILike(u.Tag, contains, SqlLikePattern.EscapeString))
            // Prefix matches before mere contains matches, then most-used, then most recent.
            .OrderBy(u => EF.Functions.ILike(u.Tag, prefix, SqlLikePattern.EscapeString) ? 0 : 1)
            .ThenByDescending(u => u.NoteCount)
            .ThenByDescending(u => u.LastUsedAt)
            .ThenBy(u => u.Tag)
            .Select(u => u.Tag)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task RebuildUserTagsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.NoteIndexUserTags
            .Where(u => u.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        // ON CONFLICT is race-safe against a concurrent UpsertAsync recomputing the same tag.
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "NoteIndexUserTags" ("UserId","Tag","NoteCount","LastUsedAt")
            SELECT e."UserId", t."Tag", COUNT(DISTINCT e."Id"), MAX(e."UpdatedAt")
            FROM "NoteIndexTags" t JOIN "NoteIndexEntries" e ON e."Id" = t."NoteIndexEntryId"
            WHERE e."UserId" = {userId}
            GROUP BY e."UserId", t."Tag"
            ON CONFLICT ("UserId","Tag") DO UPDATE
              SET "NoteCount" = EXCLUDED."NoteCount", "LastUsedAt" = EXCLUDED."LastUsedAt"
            """, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Recomputes <paramref name="userId"/>'s NoteIndexUserTags rows for just <paramref name="tags"/> from
    /// NoteIndexTags/NoteIndexEntries, on <paramref name="db"/>'s current transaction. Upserts with
    /// ON CONFLICT rather than delete-then-insert so two circuits saving notes that share a tag can't hit a
    /// primary-key violation.
    /// </summary>
    private static async Task RecomputeUserTagsAsync(JNotesDbContext db, Guid userId, string[] tags, CancellationToken cancellationToken)
    {
        if (tags.Length == 0)
        {
            return;
        }

        // Tags no note of this user carries any more.
        await db.Database.ExecuteSqlAsync($"""
            DELETE FROM "NoteIndexUserTags" u
            WHERE u."UserId" = {userId} AND u."Tag" = ANY({tags})
              AND NOT EXISTS (SELECT 1 FROM "NoteIndexTags" t
                              JOIN "NoteIndexEntries" e ON e."Id" = t."NoteIndexEntryId"
                              WHERE e."UserId" = {userId} AND t."Tag" = u."Tag")
            """, cancellationToken);

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "NoteIndexUserTags" ("UserId","Tag","NoteCount","LastUsedAt")
            SELECT e."UserId", t."Tag", COUNT(DISTINCT e."Id"), MAX(e."UpdatedAt")
            FROM "NoteIndexTags" t JOIN "NoteIndexEntries" e ON e."Id" = t."NoteIndexEntryId"
            WHERE e."UserId" = {userId} AND t."Tag" = ANY({tags})
            GROUP BY e."UserId", t."Tag"
            ON CONFLICT ("UserId","Tag") DO UPDATE
              SET "NoteCount" = EXCLUDED."NoteCount", "LastUsedAt" = EXCLUDED."LastUsedAt"
            """, cancellationToken);
    }
}
