using JNotes.Core.Indexing;
using JNotes.Core.Models;
using JNotes.Core.Notes;

namespace JNotes.Tests.Integration;

/// <summary>
/// Pins <see cref="PostgresNoteIndexRepository"/> against the behaviour the Redis implementation
/// it replaces had — see CLAUDE.md's "Search matching" row. Constructs <see cref="NoteIndexEntry"/>
/// instances directly (bypassing <c>NoteService</c>/the reconciler) so each test controls exactly
/// what's indexed.
/// </summary>
public class NoteIndexRepositoryTests : IAsyncLifetime
{
    private CoreServicesFixture _fixture = null!;

    public async Task InitializeAsync() => _fixture = await CoreServicesFixture.CreateAsync();

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private static NoteIndexEntry NewEntry(
        Guid userId,
        string title = "Title",
        string body = "Body",
        NoteType type = NoteType.Text,
        DateTimeOffset? updatedAt = null,
        DateTimeOffset? indexedFileWriteTimeUtc = null)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        return new NoteIndexEntry
        {
            Id = id,
            UserId = userId,
            Title = title,
            Body = body,
            NoteType = type.ToString(),
            CreatedAt = now,
            UpdatedAt = updatedAt ?? now,
            IndexedFileWriteTimeUtc = indexedFileWriteTimeUtc ?? now,
            FilePath = $"{userId}/{id}.md",
        };
    }

    [Fact]
    public async Task SearchAsync_matches_the_title_the_body_and_a_tag_case_insensitively()
    {
        var titled = NewEntry(_fixture.UserAId, title: "A Needle In Title");
        var bodied = NewEntry(_fixture.UserAId, body: "a needle in the body");
        var tagged = NewEntry(_fixture.UserAId);
        await _fixture.IndexRepository.UpsertAsync(titled, []);
        await _fixture.IndexRepository.UpsertAsync(bodied, []);
        await _fixture.IndexRepository.UpsertAsync(tagged, ["Needle-Tag"]);

        var byTitle = await _fixture.IndexRepository.SearchAsync(_fixture.UserAId, new NoteSearchQuery(SearchText: "NEEDLE"));
        var byTag = await _fixture.IndexRepository.SearchAsync(_fixture.UserAId, new NoteSearchQuery(SearchText: "needle-tag"));

        Assert.Contains(byTitle, e => e.Id == titled.Id);
        Assert.Contains(byTitle, e => e.Id == bodied.Id);
        Assert.Contains(byTag, e => e.Id == tagged.Id);
    }

    [Fact]
    public async Task SearchAsync_treats_a_percent_or_underscore_in_the_term_as_a_literal()
    {
        var percentNote = NewEntry(_fixture.UserAId, title: "100% done");
        var notPercentNote = NewEntry(_fixture.UserAId, title: "1000 done");
        await _fixture.IndexRepository.UpsertAsync(percentNote, []);
        await _fixture.IndexRepository.UpsertAsync(notPercentNote, []);

        var percentResults = await _fixture.IndexRepository.SearchAsync(_fixture.UserAId, new NoteSearchQuery(SearchText: "100%"));

        Assert.Single(percentResults);
        Assert.Equal(percentNote.Id, percentResults[0].Id);

        var snakeNote = NewEntry(_fixture.UserAId, title: "snake_case");
        var notSnakeNote = NewEntry(_fixture.UserAId, title: "snakeXcase");
        await _fixture.IndexRepository.UpsertAsync(snakeNote, []);
        await _fixture.IndexRepository.UpsertAsync(notSnakeNote, []);

        var snakeResults = await _fixture.IndexRepository.SearchAsync(_fixture.UserAId, new NoteSearchQuery(SearchText: "snake_case"));

        Assert.Single(snakeResults);
        Assert.Equal(snakeNote.Id, snakeResults[0].Id);
    }

    [Fact]
    public async Task UpsertAsync_replaces_the_previous_tags_rather_than_adding_to_them()
    {
        var entry = NewEntry(_fixture.UserAId);
        await _fixture.IndexRepository.UpsertAsync(entry, ["a", "b"]);

        await _fixture.IndexRepository.UpsertAsync(entry, ["c"]);

        var results = await _fixture.IndexRepository.SearchAsync(_fixture.UserAId, new NoteSearchQuery());
        var found = Assert.Single(results, r => r.Id == entry.Id);
        Assert.Equal(["c"], found.Tags);

        var tags = await _fixture.IndexRepository.GetDistinctTagsAsync(_fixture.UserAId);
        Assert.DoesNotContain("a", tags);
        Assert.DoesNotContain("b", tags);
    }

    [Fact]
    public async Task SearchAsync_preserves_the_tag_order_it_was_given()
    {
        var entry = NewEntry(_fixture.UserAId);
        await _fixture.IndexRepository.UpsertAsync(entry, ["zebra", "apple"]);

        var results = await _fixture.IndexRepository.SearchAsync(_fixture.UserAId, new NoteSearchQuery());

        var found = Assert.Single(results, r => r.Id == entry.Id);
        Assert.Equal(["zebra", "apple"], found.Tags);
    }

    [Fact]
    public async Task SearchAsync_filters_by_an_exact_case_sensitive_tag()
    {
        var entry = NewEntry(_fixture.UserAId);
        await _fixture.IndexRepository.UpsertAsync(entry, ["work"]);

        var results = await _fixture.IndexRepository.SearchAsync(_fixture.UserAId, new NoteSearchQuery(Tag: "Work"));

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetDistinctTagsAsync_is_distinct_ordinal_sorted_and_scoped_to_the_user()
    {
        var entryA1 = NewEntry(_fixture.UserAId);
        var entryA2 = NewEntry(_fixture.UserAId);
        var entryB = NewEntry(_fixture.UserBId);
        await _fixture.IndexRepository.UpsertAsync(entryA1, ["zebra", "apple"]);
        await _fixture.IndexRepository.UpsertAsync(entryA2, ["apple", "mango"]);
        await _fixture.IndexRepository.UpsertAsync(entryB, ["user-b-only"]);

        var tags = await _fixture.IndexRepository.GetDistinctTagsAsync(_fixture.UserAId);

        Assert.Equal(["apple", "mango", "zebra"], tags);
    }

    [Fact]
    public async Task DeleteAsync_ignores_a_note_id_that_belongs_to_another_user()
    {
        var entry = NewEntry(_fixture.UserAId);
        await _fixture.IndexRepository.UpsertAsync(entry, []);

        await _fixture.IndexRepository.DeleteAsync(_fixture.UserBId, entry.Id);

        var results = await _fixture.IndexRepository.SearchAsync(_fixture.UserAId, new NoteSearchQuery());
        Assert.Contains(results, r => r.Id == entry.Id);
    }

    [Fact]
    public async Task DeleteAsync_is_a_noop_for_an_unknown_id()
    {
        await _fixture.IndexRepository.DeleteAsync(_fixture.UserAId, Guid.NewGuid());
    }

    [Fact]
    public async Task UpdateContentAsync_changes_only_body_and_UpdatedAt()
    {
        var indexedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var entry = NewEntry(_fixture.UserAId, title: "Keep me", body: "old body", updatedAt: indexedAt, indexedFileWriteTimeUtc: indexedAt);
        await _fixture.IndexRepository.UpsertAsync(entry, ["keep-tag"]);

        var later = indexedAt.AddMinutes(5);
        await _fixture.IndexRepository.UpdateContentAsync(_fixture.UserAId, entry.Id, "new body", later);

        var found = Assert.Single(await _fixture.IndexRepository.SearchAsync(_fixture.UserAId, new NoteSearchQuery(SearchText: "new body")));
        Assert.Equal("Keep me", found.Title);
        Assert.Equal(["keep-tag"], found.Tags);
        Assert.Equal(later, found.UpdatedAt);
        Assert.Empty(await _fixture.IndexRepository.SearchAsync(_fixture.UserAId, new NoteSearchQuery(SearchText: "old body")));

        // The indexed mtime stays behind the file's so the reconciler re-parses the note once.
        var times = await _fixture.IndexRepository.GetIndexedFileWriteTimesAsync(_fixture.UserAId);
        Assert.Equal(indexedAt, times[entry.Id]);
    }

    [Fact]
    public async Task UpdateContentAsync_ignores_an_older_UpdatedAt_than_the_row_already_has()
    {
        var newer = new DateTimeOffset(2026, 1, 1, 0, 10, 0, TimeSpan.Zero);
        var entry = NewEntry(_fixture.UserAId, body: "newer body", updatedAt: newer);
        await _fixture.IndexRepository.UpsertAsync(entry, []);

        await _fixture.IndexRepository.UpdateContentAsync(_fixture.UserAId, entry.Id, "stale body", newer.AddMinutes(-5));

        var found = Assert.Single(await _fixture.IndexRepository.SearchAsync(_fixture.UserAId, new NoteSearchQuery(SearchText: "newer body")));
        Assert.Equal(newer, found.UpdatedAt);
        Assert.Empty(await _fixture.IndexRepository.SearchAsync(_fixture.UserAId, new NoteSearchQuery(SearchText: "stale body")));
    }

    [Fact]
    public async Task UpdateContentAsync_is_a_noop_for_an_unknown_id_or_another_users_note()
    {
        var entry = NewEntry(_fixture.UserAId, body: "mine");
        await _fixture.IndexRepository.UpsertAsync(entry, []);

        await _fixture.IndexRepository.UpdateContentAsync(_fixture.UserAId, Guid.NewGuid(), "x", DateTimeOffset.UtcNow.AddMinutes(1));
        await _fixture.IndexRepository.UpdateContentAsync(_fixture.UserBId, entry.Id, "hijacked", DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Single(await _fixture.IndexRepository.SearchAsync(_fixture.UserAId, new NoteSearchQuery(SearchText: "mine")));
        Assert.Empty(await _fixture.IndexRepository.SearchAsync(_fixture.UserAId, new NoteSearchQuery(SearchText: "hijacked")));
    }

    [Fact]
    public async Task GetIndexedFileWriteTimesAsync_round_trips_the_mtime_at_index_resolution()
    {
        var subMicrosecond = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(1234567);
        var entry = NewEntry(_fixture.UserAId, indexedFileWriteTimeUtc: subMicrosecond);
        await _fixture.IndexRepository.UpsertAsync(entry, []);

        var times = await _fixture.IndexRepository.GetIndexedFileWriteTimesAsync(_fixture.UserAId);

        Assert.Equal(IndexTimestamp.ToIndexResolution(subMicrosecond), times[entry.Id]);
    }

    [Fact]
    public async Task SearchAsync_filters_by_note_type()
    {
        var textNote = NewEntry(_fixture.UserAId, type: NoteType.Text);
        var checklistNote = NewEntry(_fixture.UserAId, type: NoteType.Checklist);
        await _fixture.IndexRepository.UpsertAsync(textNote, []);
        await _fixture.IndexRepository.UpsertAsync(checklistNote, []);

        var results = await _fixture.IndexRepository.SearchAsync(_fixture.UserAId, new NoteSearchQuery(Type: NoteType.Checklist));

        var found = Assert.Single(results);
        Assert.Equal(checklistNote.Id, found.Id);
    }

    [Fact]
    public async Task SearchAsync_with_a_limit_returns_only_the_newest_rows_in_order()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = Enumerable.Range(0, 12)
            .Select(i => NewEntry(_fixture.UserAId, updatedAt: now.AddMinutes(-i)))
            .ToList();
        foreach (var entry in entries)
        {
            await _fixture.IndexRepository.UpsertAsync(entry, []);
        }

        var limited = await _fixture.IndexRepository.SearchAsync(_fixture.UserAId, new NoteSearchQuery(Limit: 10));
        var unlimited = await _fixture.IndexRepository.SearchAsync(_fixture.UserAId, new NoteSearchQuery());

        Assert.Equal(entries.Take(10).Select(e => e.Id), limited.Select(r => r.Id));
        Assert.Equal(12, unlimited.Count);
    }

    [Fact]
    public async Task SearchAsync_applies_the_limit_after_filters()
    {
        var now = DateTimeOffset.UtcNow;
        var matchOld = NewEntry(_fixture.UserAId, title: "needle old", updatedAt: now.AddMinutes(-30));
        var matchMid = NewEntry(_fixture.UserAId, title: "needle mid", updatedAt: now.AddMinutes(-20));
        var matchNew = NewEntry(_fixture.UserAId, title: "needle new", updatedAt: now.AddMinutes(-10));
        var otherA = NewEntry(_fixture.UserAId, title: "other a", updatedAt: now.AddMinutes(-2));
        var otherB = NewEntry(_fixture.UserAId, title: "other b", updatedAt: now.AddMinutes(-1));
        foreach (var entry in new[] { matchOld, matchMid, matchNew, otherA, otherB })
        {
            await _fixture.IndexRepository.UpsertAsync(entry, []);
        }

        var results = await _fixture.IndexRepository.SearchAsync(_fixture.UserAId, new NoteSearchQuery(SearchText: "needle", Limit: 2));

        Assert.Equal([matchNew.Id, matchMid.Id], results.Select(r => r.Id));
    }

    [Fact]
    public async Task SearchAsync_breaks_UpdatedAt_ties_deterministically()
    {
        var sameTime = DateTimeOffset.UtcNow.AddMinutes(-5);
        var entries = Enumerable.Range(0, 3).Select(_ => NewEntry(_fixture.UserAId, updatedAt: sameTime)).ToList();
        foreach (var entry in entries)
        {
            await _fixture.IndexRepository.UpsertAsync(entry, []);
        }

        var first = await _fixture.IndexRepository.SearchAsync(_fixture.UserAId, new NoteSearchQuery(Limit: 2));
        var second = await _fixture.IndexRepository.SearchAsync(_fixture.UserAId, new NoteSearchQuery(Limit: 2));

        // PostgreSQL orders uuids bytewise, which matches the ordinal order of the lowercase hex "D" form
        // (not Guid.CompareTo's order).
        var expected = entries.Select(e => e.Id).OrderByDescending(id => id.ToString(), StringComparer.Ordinal).Take(2);
        Assert.Equal(expected, first.Select(r => r.Id));
        Assert.Equal(expected, second.Select(r => r.Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task SearchAsync_rejects_a_limit_below_one(int limit)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _fixture.IndexRepository.SearchAsync(_fixture.UserAId, new NoteSearchQuery(Limit: limit)));
    }
}
