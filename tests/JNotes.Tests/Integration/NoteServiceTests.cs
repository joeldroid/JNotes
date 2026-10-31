using JNotes.Core.Models;
using JNotes.Core.Notes;
using JNotes.Core.Serialization;

namespace JNotes.Tests.Integration;

public class NoteServiceTests : IAsyncLifetime
{
    private CoreServicesFixture _fixture = null!;

    public async Task InitializeAsync() => _fixture = await CoreServicesFixture.CreateAsync();

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private string PathForUserA(Guid noteId) => Path.Combine(_fixture.NotesFolder, _fixture.UserAId.ToString(), $"{noteId}.md");

    private string TrashPathForUserA(Guid noteId) => Path.Combine(_fixture.NotesFolder, _fixture.UserAId.ToString(), "trash", $"{noteId}.md");

    [Fact]
    public async Task CreateAsync_writes_the_file_and_index_entry_so_ListAsync_reflects_it_immediately()
    {
        var note = await _fixture.NoteService.CreateAsync("New note", ["work"], "body text");

        Assert.True(File.Exists(PathForUserA(note.Id)));

        var listed = await _fixture.NoteService.ListAsync(new NoteSearchQuery());
        var summary = Assert.Single(listed);
        Assert.Equal(note.Id, summary.Id);
        Assert.Equal("New note", summary.Title);
        Assert.Equal(["work"], summary.Tags);
    }

    [Fact]
    public async Task UpdateAsync_persists_changes_to_disk_and_the_index_and_bumps_UpdatedAt()
    {
        var note = await _fixture.NoteService.CreateAsync("Original", [], "original body");
        var originalUpdatedAt = note.UpdatedAt;
        await Task.Delay(TimeSpan.FromMilliseconds(10));

        note.Title = "Renamed";
        note.Body = "new body";
        var updated = await _fixture.NoteService.UpdateAsync(note);

        Assert.True(updated.UpdatedAt > originalUpdatedAt);

        var reloaded = await _fixture.NoteService.GetAsync(note.Id);
        Assert.Equal("Renamed", reloaded.Title);
        Assert.Equal("new body", reloaded.Body);

        var listed = await _fixture.NoteService.ListAsync(new NoteSearchQuery());
        Assert.Contains(listed, n => n.Id == note.Id && n.Title == "Renamed");
    }

    [Fact]
    public async Task AutosaveAsync_with_ContentOnly_scope_updates_the_indexed_body_and_UpdatedAt_but_not_title_or_tags()
    {
        var note = await _fixture.NoteService.CreateAsync("Original title", ["keep"], "original body");
        var indexedBefore = Assert.Single(await _fixture.NoteService.ListAsync(new NoteSearchQuery()));
        await Task.Delay(TimeSpan.FromMilliseconds(10));

        note.Body = "new body from autosave";
        note.Title = "Renamed but not yet saved as metadata";
        await _fixture.NoteService.AutosaveAsync(note, NoteSaveScope.ContentOnly);

        var reloaded = await _fixture.NoteService.GetAsync(note.Id);
        Assert.Equal("new body from autosave", reloaded.Body);

        var indexedAfter = Assert.Single(await _fixture.NoteService.ListAsync(new NoteSearchQuery()));
        Assert.Equal(indexedBefore.Title, indexedAfter.Title);
        Assert.Equal(["keep"], indexedAfter.Tags);
        Assert.True(indexedAfter.UpdatedAt > indexedBefore.UpdatedAt);
        Assert.Single(await _fixture.NoteService.ListAsync(new NoteSearchQuery(SearchText: "new body from autosave")));
        Assert.Empty(await _fixture.NoteService.ListAsync(new NoteSearchQuery(SearchText: "original body")));
    }

    [Fact]
    public async Task AutosaveAsync_with_ContentOnly_scope_moves_the_note_to_the_top_of_the_list()
    {
        var older = await _fixture.NoteService.CreateAsync("Older", [], "a");
        await Task.Delay(TimeSpan.FromMilliseconds(10));
        await _fixture.NoteService.CreateAsync("Newer", [], "b");
        await Task.Delay(TimeSpan.FromMilliseconds(10));

        older.Body = "edited";
        await _fixture.NoteService.AutosaveAsync(older, NoteSaveScope.ContentOnly);

        var listed = await _fixture.NoteService.ListAsync(new NoteSearchQuery());
        Assert.Equal(["Older", "Newer"], listed.Select(n => n.Title));
    }

    [Fact]
    public async Task AutosaveAsync_with_ContentOnly_scope_advances_the_tag_LastUsedAt_used_to_rank_suggestions()
    {
        var first = await _fixture.NoteService.CreateAsync("First", ["alpha"], "a");
        await Task.Delay(TimeSpan.FromMilliseconds(10));
        await _fixture.NoteService.CreateAsync("Second", ["beta"], "b");
        await Task.Delay(TimeSpan.FromMilliseconds(10));

        // Both tags are on one note each, so ties fall to LastUsedAt: "beta" leads until "alpha"'s note is edited.
        Assert.Equal(["beta", "alpha"], await _fixture.NoteService.SuggestTagsAsync(null, []));

        first.Body = "edited";
        await _fixture.NoteService.AutosaveAsync(first, NoteSaveScope.ContentOnly);

        Assert.Equal(["alpha", "beta"], await _fixture.NoteService.SuggestTagsAsync(null, []));
    }


    [Fact]
    public async Task AutosaveAsync_with_Metadata_scope_writes_both_the_file_and_the_index_immediately()
    {
        var note = await _fixture.NoteService.CreateAsync("Original title", [], "body");

        note.Title = "Renamed via autosave";
        note.Tags = ["autosaved"];
        await _fixture.NoteService.AutosaveAsync(note, NoteSaveScope.Metadata);

        var listed = await _fixture.NoteService.ListAsync(new NoteSearchQuery());
        var summary = Assert.Single(listed);
        Assert.Equal("Renamed via autosave", summary.Title);
        Assert.Equal(["autosaved"], summary.Tags);

        var reloaded = await _fixture.NoteService.GetAsync(note.Id);
        Assert.Equal("Renamed via autosave", reloaded.Title);
        Assert.Equal(["autosaved"], reloaded.Tags);
    }

    [Fact]
    public async Task TrashAsync_moves_the_file_to_the_trash_folder_and_removes_the_index_entry()
    {
        var note = await _fixture.NoteService.CreateAsync("Temporary", [], "body");

        await _fixture.NoteService.TrashAsync(note.Id);

        Assert.False(File.Exists(PathForUserA(note.Id)));
        Assert.True(File.Exists(TrashPathForUserA(note.Id)));
        var listed = await _fixture.NoteService.ListAsync(new NoteSearchQuery());
        Assert.DoesNotContain(listed, n => n.Id == note.Id);
    }

    [Fact]
    public async Task ListTrashAsync_reflects_a_trashed_note_newest_first()
    {
        var first = await _fixture.NoteService.CreateAsync("First", [], "body");
        await _fixture.NoteService.TrashAsync(first.Id);
        await Task.Delay(TimeSpan.FromMilliseconds(10));
        var second = await _fixture.NoteService.CreateAsync("Second", ["work"], "body");
        await _fixture.NoteService.TrashAsync(second.Id);

        var trash = await _fixture.NoteService.ListTrashAsync();

        Assert.Equal(2, trash.Count);
        Assert.Equal(second.Id, trash[0].Id);
        Assert.Equal(first.Id, trash[1].Id);
        Assert.Equal(["work"], trash[0].Tags);
    }

    [Fact]
    public async Task RestoreAsync_moves_the_note_back_and_reindexes_it()
    {
        var note = await _fixture.NoteService.CreateAsync("Comeback", ["home"], "body");
        await _fixture.NoteService.TrashAsync(note.Id);

        var restored = await _fixture.NoteService.RestoreAsync(note.Id);

        Assert.Equal("Comeback", restored.Title);
        Assert.True(File.Exists(PathForUserA(note.Id)));
        Assert.False(File.Exists(TrashPathForUserA(note.Id)));
        Assert.Empty(await _fixture.NoteService.ListTrashAsync());

        var listed = await _fixture.NoteService.ListAsync(new NoteSearchQuery());
        var summary = Assert.Single(listed);
        Assert.Equal(note.Id, summary.Id);
        Assert.Equal(["home"], summary.Tags);
    }

    [Fact]
    public async Task PurgeAsync_permanently_removes_a_trashed_note()
    {
        var note = await _fixture.NoteService.CreateAsync("Gone for good", [], "body");
        await _fixture.NoteService.TrashAsync(note.Id);

        await _fixture.NoteService.PurgeAsync(note.Id);

        Assert.False(File.Exists(TrashPathForUserA(note.Id)));
        Assert.Empty(await _fixture.NoteService.ListTrashAsync());
    }

    [Fact]
    public async Task ListAsync_filters_by_tag()
    {
        await _fixture.NoteService.CreateAsync("Home note", ["home"], "b");
        await _fixture.NoteService.CreateAsync("Work note", ["work"], "c");

        var workTagged = await _fixture.NoteService.ListAsync(new NoteSearchQuery(Tag: "work"));
        Assert.Single(workTagged);
        Assert.Equal("Work note", workTagged[0].Title);
    }

    [Fact]
    public async Task A_legacy_checklist_file_is_indexed_and_read_as_a_text_note()
    {
        var id = Guid.NewGuid();
        var folder = Path.Combine(_fixture.NotesFolder, _fixture.UserAId.ToString());
        Directory.CreateDirectory(folder);
        const string body = "- [ ] milk\n- [x] eggs";
        await File.WriteAllTextAsync(
            PathForUserA(id),
            $"---\nid: {id}\ntitle: Old list\ntype: checklist\ntags: []\ncreated: 2026-01-01T00:00:00Z\nupdated: 2026-01-01T00:00:00Z\n---\n{body}");

        await _fixture.Reconciler.RescanAsync(_fixture.UserAId);

        var found = Assert.Single(await _fixture.NoteService.ListAsync(new NoteSearchQuery(SearchText: "milk")));
        Assert.Equal(id, found.Id);

        var note = await _fixture.NoteService.GetAsync(id);
        Assert.Equal(body, note.Body);

        note.Title = "Old list, renamed";
        await _fixture.NoteService.UpdateAsync(note);
        var raw = await File.ReadAllTextAsync(PathForUserA(id));
        Assert.Contains("type: text", raw);
        Assert.DoesNotContain("checklist", raw);
    }

    [Fact]
    public async Task ListAsync_SearchText_matches_the_title()
    {
        await _fixture.NoteService.CreateAsync("Grocery list", [], "milk, eggs");
        await _fixture.NoteService.CreateAsync("Meeting notes", [], "agenda items");

        var results = await _fixture.NoteService.ListAsync(new NoteSearchQuery(SearchText: "grocery"));

        Assert.Single(results);
        Assert.Equal("Grocery list", results[0].Title);
    }

    [Fact]
    public async Task ListAsync_SearchText_matches_the_body()
    {
        await _fixture.NoteService.CreateAsync("Untitled", [], "remember to buy oat milk");
        await _fixture.NoteService.CreateAsync("Other note", [], "unrelated content");

        var results = await _fixture.NoteService.ListAsync(new NoteSearchQuery(SearchText: "oat milk"));

        Assert.Single(results);
        Assert.Equal("Untitled", results[0].Title);
    }

    [Fact]
    public async Task ListAsync_SearchText_matches_a_tag()
    {
        await _fixture.NoteService.CreateAsync("Tagged note", ["urgent"], "body");
        await _fixture.NoteService.CreateAsync("Untagged note", [], "body");

        var results = await _fixture.NoteService.ListAsync(new NoteSearchQuery(SearchText: "urgent"));

        Assert.Single(results);
        Assert.Equal("Tagged note", results[0].Title);
    }

    [Fact]
    public async Task ListAsync_SearchText_reflects_a_body_edit_after_a_Metadata_scope_autosave()
    {
        var note = await _fixture.NoteService.CreateAsync("Note", [], "original body");
        Assert.Empty(await _fixture.NoteService.ListAsync(new NoteSearchQuery(SearchText: "needle")));

        note.Body = "contains a needle in it";
        await _fixture.NoteService.AutosaveAsync(note, NoteSaveScope.Metadata);

        var results = await _fixture.NoteService.ListAsync(new NoteSearchQuery(SearchText: "needle"));
        Assert.Single(results);
    }

    [Fact]
    public async Task A_note_created_by_one_user_never_appears_in_another_users_listing_or_lookup()
    {
        _fixture.ActAsUserA();
        var userANote = await _fixture.NoteService.CreateAsync("User A's note", [], "secret to A");

        _fixture.ActAsUserB();
        var userBListing = await _fixture.NoteService.ListAsync(new NoteSearchQuery());
        Assert.Empty(userBListing);
        await Assert.ThrowsAsync<JNotes.Core.Storage.NoteNotFoundException>(() => _fixture.NoteService.GetAsync(userANote.Id));

        // And each user's file genuinely lives under their own subfolder, not a shared one.
        Assert.True(File.Exists(Path.Combine(_fixture.NotesFolder, _fixture.UserAId.ToString(), $"{userANote.Id}.md")));
        Assert.False(File.Exists(Path.Combine(_fixture.NotesFolder, _fixture.UserBId.ToString(), $"{userANote.Id}.md")));

        _fixture.ActAsUserA();
        var userAListing = await _fixture.NoteService.ListAsync(new NoteSearchQuery());
        Assert.Contains(userAListing, n => n.Id == userANote.Id);
    }
}
