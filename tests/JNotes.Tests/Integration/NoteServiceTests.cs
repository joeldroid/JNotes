using JNotes.Core.Models;
using JNotes.Core.Notes;

namespace JNotes.Tests.Integration;

public class NoteServiceTests : IAsyncLifetime
{
    private CoreServicesFixture _fixture = null!;

    public async Task InitializeAsync() => _fixture = await CoreServicesFixture.CreateAsync();

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private string PathForUserA(Guid noteId) => Path.Combine(_fixture.NotesFolder, _fixture.UserAId.ToString(), $"{noteId}.md");

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
    public async Task ToggleChecklistItemAsync_flips_the_item_and_persists_it()
    {
        var note = await _fixture.NoteService.CreateAsync(NoteType.Checklist, "Groceries", [], "- [ ] milk\n- [ ] eggs");

        var toggled = await _fixture.NoteService.ToggleChecklistItemAsync(note.Id, lineIndex: 0);

        Assert.StartsWith("- [x] milk", toggled.Body);

        var reloaded = await _fixture.NoteService.GetAsync(note.Id);
        Assert.StartsWith("- [x] milk", reloaded.Body);
    }

    [Fact]
    public async Task ToggleChecklistItemAsync_throws_for_a_non_checklist_note()
    {
        var note = await _fixture.NoteService.CreateAsync(NoteType.Text, "Plain", [], "not a checklist");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _fixture.NoteService.ToggleChecklistItemAsync(note.Id, 0));
    }

    [Fact]
    public async Task ToggleChecklistItemAsync_does_not_update_the_index_immediately()
    {
        // Same self-healing trade-off as a ContentOnly autosave: toggling is a body edit, so the
        // index (and therefore list ordering) only catches up on the next thing that touches it —
        // a title/tag edit, explicit Save, or reconciliation.
        var note = await _fixture.NoteService.CreateAsync(NoteType.Checklist, "Groceries", [], "- [ ] milk");
        var indexedBefore = Assert.Single(await _fixture.NoteService.ListAsync(new NoteSearchQuery()));
        await Task.Delay(TimeSpan.FromMilliseconds(10));

        await _fixture.NoteService.ToggleChecklistItemAsync(note.Id, lineIndex: 0);

        var indexedAfter = Assert.Single(await _fixture.NoteService.ListAsync(new NoteSearchQuery()));
        Assert.Equal(indexedBefore.UpdatedAt, indexedAfter.UpdatedAt);
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
    public async Task DeleteAsync_removes_both_the_file_and_the_index_entry()
    {
        var note = await _fixture.NoteService.CreateAsync(NoteType.Text, "Temporary", [], "body");

        await _fixture.NoteService.DeleteAsync(note.Id);

        Assert.False(File.Exists(PathForUserA(note.Id)));
        var listed = await _fixture.NoteService.ListAsync(new NoteSearchQuery());
        Assert.DoesNotContain(listed, n => n.Id == note.Id);
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
