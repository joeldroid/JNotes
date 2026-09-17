using System.Text.Json;
using JNotes.Core.Notes;
using StackExchange.Redis;

namespace JNotes.Core.Indexing;

/// <inheritdoc cref="INoteIndexRepository"/>
/// <remarks>
/// Schema: a Redis hash <c>jnotes:note:{noteId}</c> per note (fields mirror
/// <see cref="NoteIndexEntry"/>, plus a JSON-encoded <c>Tags</c> field — there's no separate join
/// table the way SQLite had one, since nothing here benefits from an indexed join: search/filter
/// happens in-memory, same as the SQLite version already did for ordering), and a set
/// <c>jnotes:user:{userId}:notes</c> of that user's note ids for enumeration. Filtering and sorting
/// happen client-side after loading a user's notes — deliberately not RediSearch or a Lua script,
/// matching this app's existing "not worth it at this scale, one user's notes" reasoning for the
/// SQLite version this replaces. The index is a rebuildable cache (see
/// <see cref="NoteIndexReconciler"/>), so an empty/flushed Redis is never a correctness problem,
/// only a one-time rescan.
/// </remarks>
public sealed class RedisNoteIndexRepository : INoteIndexRepository
{
    private readonly IConnectionMultiplexer _redis;

    public RedisNoteIndexRepository(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task<IReadOnlyList<NoteSummary>> SearchAsync(Guid userId, NoteSearchQuery query, CancellationToken cancellationToken = default)
    {
        var entries = await LoadUserEntriesAsync(userId);

        IEnumerable<StoredEntry> filtered = entries;

        if (query.Type is { } type)
        {
            var typeName = type.ToString();
            filtered = filtered.Where(e => e.Entry.NoteType == typeName);
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var text = query.SearchText;
            filtered = filtered.Where(e =>
                e.Entry.Title.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                e.Entry.Body.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                e.Tags.Any(t => t.Contains(text, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(query.Tag))
        {
            var tag = query.Tag;
            filtered = filtered.Where(e => e.Tags.Contains(tag));
        }

        return filtered
            .OrderByDescending(e => e.Entry.UpdatedAt)
            .Select(e => new NoteSummary(
                e.Entry.Id,
                e.Entry.Title,
                Enum.Parse<Models.NoteType>(e.Entry.NoteType, ignoreCase: true),
                e.Tags,
                e.Entry.CreatedAt,
                e.Entry.UpdatedAt))
            .ToList();
    }

    public async Task<IReadOnlyList<string>> GetDistinctTagsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var entries = await LoadUserEntriesAsync(userId);
        return entries
            .SelectMany(e => e.Tags)
            .Distinct()
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
    }

    public async Task UpsertAsync(NoteIndexEntry entry, IReadOnlyList<string> tags, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();

        var fields = new HashEntry[]
        {
            new(TitleField, entry.Title),
            new(BodyField, entry.Body),
            new(NoteTypeField, entry.NoteType),
            new(CreatedAtField, entry.CreatedAt.ToString("O")),
            new(UpdatedAtField, entry.UpdatedAt.ToString("O")),
            new(IndexedFileWriteTimeField, entry.IndexedFileWriteTimeUtc.ToString("O")),
            new(FilePathField, entry.FilePath),
            new(TagsField, JsonSerializer.Serialize(tags)),
        };

        await db.HashSetAsync(NoteKey(entry.Id), fields);
        await db.SetAddAsync(UserNotesKey(entry.UserId), entry.Id.ToString());
    }

    public async Task DeleteAsync(Guid userId, Guid noteId, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        await db.KeyDeleteAsync(NoteKey(noteId));
        await db.SetRemoveAsync(UserNotesKey(userId), noteId.ToString());
    }

    public async Task<IReadOnlyDictionary<Guid, DateTimeOffset>> GetIndexedFileWriteTimesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        var ids = await db.SetMembersAsync(UserNotesKey(userId));

        var lookups = ids
            .Select(id => (Id: Guid.Parse(id.ToString()), Task: db.HashGetAsync(NoteKey(Guid.Parse(id.ToString())), IndexedFileWriteTimeField)))
            .ToList();

        await Task.WhenAll(lookups.Select(l => l.Task));

        var result = new Dictionary<Guid, DateTimeOffset>();
        foreach (var (id, task) in lookups)
        {
            var value = task.Result;
            if (value.HasValue)
            {
                result[id] = DateTimeOffset.Parse(value.ToString());
            }
        }

        return result;
    }

    private async Task<IReadOnlyList<StoredEntry>> LoadUserEntriesAsync(Guid userId)
    {
        var db = _redis.GetDatabase();
        var ids = await db.SetMembersAsync(UserNotesKey(userId));

        var lookups = ids
            .Select(id => db.HashGetAllAsync(NoteKey(Guid.Parse(id.ToString()))))
            .ToArray();

        var hashes = await Task.WhenAll(lookups);

        var entries = new List<StoredEntry>(hashes.Length);
        for (var i = 0; i < ids.Length; i++)
        {
            var hash = hashes[i];
            if (hash.Length == 0)
            {
                continue; // Deleted between SMEMBERS and HGETALL — the set will self-correct on the next mutation.
            }

            var fields = hash.ToDictionary(h => h.Name.ToString(), h => h.Value.ToString());
            var noteId = Guid.Parse(ids[i].ToString());
            var entry = new NoteIndexEntry
            {
                Id = noteId,
                UserId = userId,
                Title = fields[TitleField],
                Body = fields[BodyField],
                NoteType = fields[NoteTypeField],
                CreatedAt = DateTimeOffset.Parse(fields[CreatedAtField]),
                UpdatedAt = DateTimeOffset.Parse(fields[UpdatedAtField]),
                IndexedFileWriteTimeUtc = DateTimeOffset.Parse(fields[IndexedFileWriteTimeField]),
                FilePath = fields[FilePathField],
            };
            var tags = JsonSerializer.Deserialize<IReadOnlyList<string>>(fields[TagsField]) ?? [];
            entries.Add(new StoredEntry(entry, tags));
        }

        return entries;
    }

    private sealed record StoredEntry(NoteIndexEntry Entry, IReadOnlyList<string> Tags);

    private const string TitleField = "Title";
    private const string BodyField = "Body";
    private const string NoteTypeField = "NoteType";
    private const string CreatedAtField = "CreatedAt";
    private const string UpdatedAtField = "UpdatedAt";
    private const string IndexedFileWriteTimeField = "IndexedFileWriteTimeUtc";
    private const string FilePathField = "FilePath";
    private const string TagsField = "Tags";

    private static RedisKey NoteKey(Guid noteId) => $"jnotes:note:{noteId}";

    private static RedisKey UserNotesKey(Guid userId) => $"jnotes:user:{userId}:notes";
}
