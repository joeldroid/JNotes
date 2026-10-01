using JNotes.Core.Data;
using JNotes.Core.Indexing;
using JNotes.Core.Users;
using Microsoft.EntityFrameworkCore;

namespace JNotes.Core.Sharing;

/// <inheritdoc cref="INoteShareRepository"/>
/// <remarks>Singleton over the singleton <see cref="IDbContextFactory{TContext}"/>, like <c>PostgresAutoTagStateRepository</c>.</remarks>
public sealed class PostgresNoteShareRepository : INoteShareRepository
{
    private readonly IDbContextFactory<JNotesDbContext> _dbContextFactory;

    public PostgresNoteShareRepository(IDbContextFactory<JNotesDbContext> dbContextFactory) =>
        _dbContextFactory = dbContextFactory;

    public async Task<NoteShareGrant?> FindAsync(Guid noteId, Guid sharedWithUserId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var row = await (from s in db.NoteShares.AsNoTracking()
                         join u in db.Users.AsNoTracking() on s.OwnerUserId equals u.Id
                         where s.NoteId == noteId && s.SharedWithUserId == sharedWithUserId
                         select new { s.OwnerUserId, s.Permission, u.Username, u.FirstName, u.LastName })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null
            ? null
            : new NoteShareGrant(row.OwnerUserId, row.Permission, UserDisplayName.Format(row.FirstName, row.LastName, row.Username));
    }

    public async Task<IReadOnlyList<NoteShareEntry>> ListForNoteAsync(Guid ownerUserId, Guid noteId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var rows = await (from s in db.NoteShares.AsNoTracking()
                          join u in db.Users.AsNoTracking() on s.SharedWithUserId equals u.Id
                          where s.OwnerUserId == ownerUserId && s.NoteId == noteId
                          select new { u.Id, u.Username, u.FirstName, u.LastName, s.Permission, s.CreatedAt })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new NoteShareEntry(r.Id, r.Username, UserDisplayName.Format(r.FirstName, r.LastName, r.Username), r.Permission, r.CreatedAt))
            .OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.Username, StringComparer.Ordinal)
            .ToList();
    }

    public async Task UpsertAsync(Guid ownerUserId, Guid noteId, Guid sharedWithUserId, NoteSharePermission permission, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var permissionName = permission.ToString();
        // ON CONFLICT so two circuits granting the same recipient can't hit a PK violation; the WHERE keeps another
        // owner's stale row for the same (note, recipient) from being taken over.
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "NoteShares" ("NoteId","SharedWithUserId","OwnerUserId","Permission","CreatedAt")
            VALUES ({noteId}, {sharedWithUserId}, {ownerUserId}, {permissionName}, {now})
            ON CONFLICT ("NoteId","SharedWithUserId") DO UPDATE
              SET "Permission" = EXCLUDED."Permission"
              WHERE "NoteShares"."OwnerUserId" = EXCLUDED."OwnerUserId"
            """, cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid ownerUserId, Guid noteId, Guid sharedWithUserId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var deleted = await db.NoteShares
            .Where(s => s.OwnerUserId == ownerUserId && s.NoteId == noteId && s.SharedWithUserId == sharedWithUserId)
            .ExecuteDeleteAsync(cancellationToken);
        return deleted > 0;
    }

    public async Task<bool> DeleteForRecipientAsync(Guid noteId, Guid sharedWithUserId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var deleted = await db.NoteShares
            .Where(s => s.NoteId == noteId && s.SharedWithUserId == sharedWithUserId)
            .ExecuteDeleteAsync(cancellationToken);
        return deleted > 0;
    }

    public async Task DeleteAllForNoteAsync(Guid ownerUserId, Guid noteId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        await db.NoteShares
            .Where(s => s.OwnerUserId == ownerUserId && s.NoteId == noteId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ShareRecipientCandidate>> SearchCandidatesAsync(Guid ownerUserId, Guid noteId, string? term, int limit, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 20);

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var users = db.Users.AsNoTracking()
            .Where(u => u.Id != ownerUserId)
            .Where(u => !db.NoteShares.Any(s => s.NoteId == noteId && s.SharedWithUserId == u.Id));

        var trimmed = term?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            var pattern = SqlLikePattern.ContainsPattern(trimmed);
            // Username carries a non-deterministic collation, which ILIKE refuses — compare it under "default".
            // Email is deliberately never part of the match: the picker must not reveal or leak it.
            users = users.Where(u =>
                EF.Functions.ILike(EF.Functions.Collate(u.Username, "default"), pattern, SqlLikePattern.EscapeString) ||
                (u.FirstName != null && EF.Functions.ILike(u.FirstName, pattern, SqlLikePattern.EscapeString)) ||
                (u.LastName != null && EF.Functions.ILike(u.LastName, pattern, SqlLikePattern.EscapeString)));
        }

        var rows = await users
            .OrderBy(u => u.Username)
            .Select(u => new { u.Id, u.Username, u.FirstName, u.LastName })
            .Take(limit)
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new ShareRecipientCandidate(r.Id, r.Username, UserDisplayName.Format(r.FirstName, r.LastName, r.Username)))
            .ToList();
    }
}
