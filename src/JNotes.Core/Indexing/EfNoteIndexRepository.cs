using JNotes.Core.Data;
using JNotes.Core.Models;
using JNotes.Core.Notes;
using Microsoft.EntityFrameworkCore;

namespace JNotes.Core.Indexing;

/// <inheritdoc cref="INoteIndexRepository"/>
public sealed class EfNoteIndexRepository : INoteIndexRepository
{
    private readonly IDbContextFactory<JNotesDbContext> _dbContextFactory;

    public EfNoteIndexRepository(IDbContextFactory<JNotesDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<IReadOnlyList<NoteSummary>> SearchAsync(Guid userId, NoteSearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var notesQuery = db.Notes.AsNoTracking().Where(n => n.UserId == userId);

        if (query.Type is { } type)
        {
            var typeName = type.ToString();
            notesQuery = notesQuery.Where(n => n.NoteType == typeName);
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            // Matches title, body, or any tag — the sidebar search box's whole point is not
            // needing to remember which of the three a note was found by.
            var pattern = $"%{query.SearchText}%";
            notesQuery = notesQuery.Where(n =>
                EF.Functions.Like(n.Title, pattern) ||
                EF.Functions.Like(n.Body, pattern) ||
                db.NoteTags.Any(t => t.NoteId == n.Id && EF.Functions.Like(t.Tag, pattern)));
        }

        if (!string.IsNullOrWhiteSpace(query.Tag))
        {
            var tag = query.Tag;
            notesQuery = notesQuery.Where(n => db.NoteTags.Any(t => t.NoteId == n.Id && t.Tag == tag));
        }

        // Ordered client-side after materializing: EF Core's SQLite provider can't translate an
        // ORDER BY over a DateTimeOffset column into SQL. Fine at this app's scale (one user's
        // notes) — not worth a surrogate sortable column just to push the sort into SQLite.
        var entries = (await notesQuery.ToListAsync(cancellationToken))
            .OrderByDescending(n => n.UpdatedAt)
            .ToList();

        var ids = entries.Select(e => e.Id).ToList();
        var tagsByNote = await db.NoteTags.AsNoTracking()
            .Where(t => ids.Contains(t.NoteId))
            .ToListAsync(cancellationToken);
        var tagsLookup = tagsByNote
            .GroupBy(t => t.NoteId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(t => t.Tag).ToList());

        return entries
            .Select(e => new NoteSummary(
                e.Id,
                e.Title,
                Enum.Parse<NoteType>(e.NoteType, ignoreCase: true),
                tagsLookup.GetValueOrDefault(e.Id, Array.Empty<string>()),
                e.CreatedAt,
                e.UpdatedAt))
            .ToList();
    }

    public async Task<IReadOnlyList<string>> GetDistinctTagsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.NoteTags.AsNoTracking()
            .Where(t => db.Notes.Any(n => n.Id == t.NoteId && n.UserId == userId))
            .Select(t => t.Tag)
            .Distinct()
            .OrderBy(t => t)
            .ToListAsync(cancellationToken);
    }

    public async Task UpsertAsync(NoteIndexEntry entry, IReadOnlyList<string> tags, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await db.Notes.FindAsync([entry.Id], cancellationToken);
        if (existing is null)
        {
            db.Notes.Add(entry);
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(entry);
        }

        var existingTags = await db.NoteTags.Where(t => t.NoteId == entry.Id).ToListAsync(cancellationToken);
        db.NoteTags.RemoveRange(existingTags);
        db.NoteTags.AddRange(tags.Select(tag => new NoteTag { NoteId = entry.Id, Tag = tag }));

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid userId, Guid noteId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Notes.FirstOrDefaultAsync(n => n.Id == noteId && n.UserId == userId, cancellationToken);
        if (existing is not null)
        {
            db.Notes.Remove(existing); // NoteTags cascade-delete with it.
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyDictionary<Guid, DateTimeOffset>> GetIndexedFileWriteTimesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Notes.AsNoTracking()
            .Where(n => n.UserId == userId)
            .ToDictionaryAsync(n => n.Id, n => n.IndexedFileWriteTimeUtc, cancellationToken);
    }
}
