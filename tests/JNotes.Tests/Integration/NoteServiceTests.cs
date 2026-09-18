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
        var note = await _fixture.NoteService.CreateAsync(NoteType.Text, "New note", ["work"], "body text");

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
        var note = await _fixture.NoteService.CreateAsync(NoteType.Text, "Original", [], "original body");
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
    public async Task SetChecklistItemCheckedAsync_checks_the_item_and_moves_it_to_the_bottom_on_disk()
    {
        var note = await _fixture.NoteService.CreateAsync(NoteType.Checklist, "Groceries", [], "- [ ] milk\n- [ ] eggs");

        var result = await _fixture.NoteService.SetChecklistItemCheckedAsync(note.Id, lineIndex: 0, isChecked: true);

        Assert.Equal("- [ ] eggs\n- [x] milk", result.Body);

        var reloaded = await _fixture.NoteService.GetAsync(note.Id);
        Assert.Equal("- [ ] eggs\n- [x] milk", reloaded.Body);
    }

    [Fact]
    public async Task SetChecklistItemCheckedAsync_unchecking_moves_the_item_back_above_the_checked_ones()
    {
        var note = await _fixture.NoteService.CreateAsync(NoteType.Checklist, "Groceries", [], "- [ ] milk\n- [x] eggs\n- [x] bread");

        await _fixture.NoteService.SetChecklistItemCheckedAsync(note.Id, lineIndex: 2, isChecked: false);

        var reloaded = await _fixture.NoteService.GetAsync(note.Id);
        Assert.Equal("- [ ] milk\n- [ ] bread\n- [x] eggs", reloaded.Body);
    }

    [Fact]
    public async Task SetChecklistItemCheckedAsync_throws_for_a_non_checklist_note()
    {
        var note = await _fixture.NoteService.CreateAsync(NoteType.Text, "Plain", [], "not a checklist");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _fixture.NoteService.SetChecklistItemCheckedAsync(note.Id, 0, true));
    }

    [Fact]
    public async Task SetChecklistItemCheckedAsync_does_not_update_the_index_immediately()
    {
        // Same self-healing trade-off as a ContentOnly autosave: checking an item is a body edit, so
        // the index (and therefore list ordering) only catches up on the next thing that touches it —
        // a title/tag edit, explicit Save, or reconciliation.
        var note = await _fixture.NoteService.CreateAsync(NoteType.Checklist, "Groceries", [], "- [ ] milk");
        var indexedBefore = Assert.Single(await _fixture.NoteService.ListAsync(new NoteSearchQuery()));
        await Task.Delay(TimeSpan.FromMilliseconds(10));

        await _fixture.NoteService.SetChecklistItemCheckedAsync(note.Id, lineIndex: 0, isChecked: true);

        var indexedAfter = Assert.Single(await _fixture.NoteService.ListAsync(new NoteSearchQuery()));
        Assert.Equal(indexedBefore.UpdatedAt, indexedAfter.UpdatedAt);
    }

    [Fact]
    public async Task AddChecklistItemAsync_appends_the_item_and_persists_it_to_disk()
    {
        var note = await _fixture.NoteService.CreateAsync(NoteType.Checklist, "Groceries", [], "- [ ] milk");

        var result = await _fixture.NoteService.AddChecklistItemAsync(note.Id, "eggs");

        Assert.Equal("- [ ] milk\n- [ ] eggs", result.Body);

        var reloaded = await _fixture.NoteService.GetAsync(note.Id);
        Assert.Equal("- [ ] milk\n- [ ] eggs", reloaded.Body);
    }

    [Fact]
    public async Task AddChecklistItemAsync_places_a_new_item_above_already_checked_items()
    {
        var note = await _fixture.NoteService.CreateAsync(NoteType.Checklist, "Groceries", [], "- [ ] milk\n- [x] eggs");

        await _fixture.NoteService.AddChecklistItemAsync(note.Id, "bread");

        var reloaded = await _fixture.NoteService.GetAsync(note.Id);
        Assert.Equal("- [ ] milk\n- [ ] bread\n- [x] eggs", reloaded.Body);
    }

    [Fact]
    public async Task AddChecklistItemAsync_adds_one_item_per_line_for_multiline_text()
    {
        var note = await _fixture.NoteService.CreateAsync(NoteType.Checklist, "Groceries", [], "");

        await _fixture.NoteService.AddChecklistItemAsync(note.Id, "milk\neggs\n\nbread");

        var reloaded = await _fixture.NoteService.GetAsync(note.Id);
        Assert.Equal(["milk", "eggs", "bread"], ChecklistMarkdownParser.Parse(reloaded.Body).Select(i => i.Text));
    }

    [Fact]
    public async Task AddChecklistItemAsync_throws_for_a_non_checklist_note()
    {
        var note = await _fixture.NoteService.CreateAsync(NoteType.Text, "Plain", [], "not a checklist");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _fixture.NoteService.AddChecklistItemAsync(note.Id, "item"));
    }

    [Fact]
    public async Task AddChecklistItemAsync_throws_for_whitespace_only_text()
    {
        var note = await _fixture.NoteService.CreateAsync(NoteType.Checklist, "Groceries", [], "- [ ] milk");

        await Assert.ThrowsAsync<ArgumentException>(() => _fixture.NoteService.AddChecklistItemAsync(note.Id, "  \n "));
    }

    [Fact]
    public async Task AddChecklistItemAsync_does_not_update_the_index_immediately()
    {
        var note = await _fixture.NoteService.CreateAsync(NoteType.Checklist, "Groceries", [], "- [ ] milk");
        var indexedBefore = Assert.Single(await _fixture.NoteService.ListAsync(new NoteSearchQuery()));
        await Task.Delay(TimeSpan.FromMilliseconds(10));

        await _fixture.NoteService.AddChecklistItemAsync(note.Id, "eggs");

        var indexedAfter = Assert.Single(await _fixture.NoteService.ListAsync(new NoteSearchQuery()));
        Assert.Equal(indexedBefore.UpdatedAt, indexedAfter.UpdatedAt);
    }

    [Fact]
    public async Task AddChecklistItemAsync_then_a_rescan_makes_the_new_item_searchable()
    {
        // Pins the documented ContentOnly staleness contract: the new item's text isn't indexed
        // until reconciliation catches the file up, and then it is.
        var note = await _fixture.NoteService.CreateAsync(NoteType.Checklist, "Groceries", [], "- [ ] milk");
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        await _fixture.NoteService.AddChecklistItemAsync(note.Id, "zucchini");
        Assert.Empty(await _fixture.NoteService.ListAsync(new NoteSearchQuery(SearchText: "zucchini")));

        await _fixture.Reconciler.RescanAsync(_fixture.UserAId);

        var found = Assert.Single(await _fixture.NoteService.ListAsync(new NoteSearchQuery(SearchText: "zucchini")));
        Assert.Equal(note.Id, found.Id);
    }

    [Fact]
    public async Task AutosaveAsync_with_ContentOnly_scope_writes_the_file_but_leaves_the_index_untouched()
    {
        var note = await _fixture.NoteService.CreateAsync(NoteType.Text, "Original title", [], "original body");
        var indexedBefore = Assert.Single(await _fixture.NoteService.ListAsync(new NoteSearchQuery()));
        await Task.Delay(TimeSpan.FromMilliseconds(10));

        note.Body = "new body from autosave";
        await _fixture.NoteService.AutosaveAsync(note, NoteSaveScope.ContentOnly);

        var reloaded = await _fixture.NoteService.GetAsync(note.Id);
        Assert.Equal("new body from autosave", reloaded.Body);

        var indexedAfter = Assert.Single(await _fixture.NoteService.ListAsync(new NoteSearchQuery()));
        Assert.Equal(indexedBefore.Title, indexedAfter.Title);
        Assert.Equal(indexedBefore.UpdatedAt, indexedAfter.UpdatedAt);
    }

    [Fact]
    public async Task AutosaveAsync_with_Metadata_scope_writes_both_the_file_and_the_index_immediately()
    {
        var note = await _fixture.NoteService.CreateAsync(NoteType.Text, "Original title", [], "body");

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
        var note = await _fixture.NoteService.CreateAsync(NoteType.Text, "Temporary", [], "body");

        await _fixture.NoteService.TrashAsync(note.Id);

        Assert.False(File.Exists(PathForUserA(note.Id)));
        Assert.True(File.Exists(TrashPathForUserA(note.Id)));
        var listed = await _fixture.NoteService.ListAsync(new NoteSearchQuery());
        Assert.DoesNotContain(listed, n => n.Id == note.Id);
    }

    [Fact]
    public async Task ListTrashAsync_reflects_a_trashed_note_newest_first()
    {
        var first = await _fixture.NoteService.CreateAsync(NoteType.Text, "First", [], "body");
        await _fixture.NoteService.TrashAsync(first.Id);
        await Task.Delay(TimeSpan.FromMilliseconds(10));
        var second = await _fixture.NoteService.CreateAsync(NoteType.Checklist, "Second", ["work"], "- [ ] x");
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
        var note = await _fixture.NoteService.CreateAsync(NoteType.Text, "Comeback", ["home"], "body");
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
        var note = await _fixture.NoteService.CreateAsync(NoteType.Text, "Gone for good", [], "body");
        await _fixture.NoteService.TrashAsync(note.Id);

        await _fixture.NoteService.PurgeAsync(note.Id);

        Assert.False(File.Exists(TrashPathForUserA(note.Id)));
        Assert.Empty(await _fixture.NoteService.ListTrashAsync());
    }

    [Fact]
    public async Task ListAsync_filters_by_type_and_tag()
    {
        await _fixture.NoteService.CreateAsync(NoteType.Text, "Text note", ["home"], "b");
        await _fixture.NoteService.CreateAsync(NoteType.Checklist, "Checklist note", ["work"], "- [ ] x");

        var textOnly = await _fixture.NoteService.ListAsync(new NoteSearchQuery(Type: NoteType.Text));
        Assert.Single(textOnly);
        Assert.Equal("Text note", textOnly[0].Title);

        var workTagged = await _fixture.NoteService.ListAsync(new NoteSearchQuery(Tag: "work"));
        Assert.Single(workTagged);
        Assert.Equal("Checklist note", workTagged[0].Title);
    }

    [Fact]
    public async Task ListAsync_SearchText_matches_the_title()
    {
        await _fixture.NoteService.CreateAsync(NoteType.Text, "Grocery list", [], "milk, eggs");
        await _fixture.NoteService.CreateAsync(NoteType.Text, "Meeting notes", [], "agenda items");

        var results = await _fixture.NoteService.ListAsync(new NoteSearchQuery(SearchText: "grocery"));

        Assert.Single(results);
        Assert.Equal("Grocery list", results[0].Title);
    }

    [Fact]
    public async Task ListAsync_SearchText_matches_the_body()
    {
        await _fixture.NoteService.CreateAsync(NoteType.Text, "Untitled", [], "remember to buy oat milk");
        await _fixture.NoteService.CreateAsync(NoteType.Text, "Other note", [], "unrelated content");

        var results = await _fixture.NoteService.ListAsync(new NoteSearchQuery(SearchText: "oat milk"));

        Assert.Single(results);
        Assert.Equal("Untitled", results[0].Title);
    }

    [Fact]
    public async Task ListAsync_SearchText_matches_a_tag()
    {
        await _fixture.NoteService.CreateAsync(NoteType.Text, "Tagged note", ["urgent"], "body");
        await _fixture.NoteService.CreateAsync(NoteType.Text, "Untagged note", [], "body");

        var results = await _fixture.NoteService.ListAsync(new NoteSearchQuery(SearchText: "urgent"));

        Assert.Single(results);
        Assert.Equal("Tagged note", results[0].Title);
    }

    [Fact]
    public async Task ListAsync_SearchText_reflects_a_body_edit_after_a_Metadata_scope_autosave()
    {
        var note = await _fixture.NoteService.CreateAsync(NoteType.Text, "Note", [], "original body");
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
        var userANote = await _fixture.NoteService.CreateAsync(NoteType.Text, "User A's note", [], "secret to A");

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
