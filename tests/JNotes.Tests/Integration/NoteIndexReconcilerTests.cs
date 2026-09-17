using JNotes.Core.Models;
using JNotes.Core.Notes;

namespace JNotes.Tests.Integration;

public class NoteIndexReconcilerTests : IAsyncLifetime
{
    private CoreServicesFixture _fixture = null!;

    public async Task InitializeAsync() => _fixture = await CoreServicesFixture.CreateAsync();

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task RescanAsync_indexes_a_note_file_added_outside_the_app()
    {
        // Written directly through the file store (bypassing NoteService, so no index row is
        // created) to simulate a file dropped into the notes folder outside the app.
        var note = new Note
        {
            Id = Guid.NewGuid(),
            Title = "Added externally",
            Type = NoteType.Text,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Body = "hello",
        };
        await _fixture.FileStore.WriteAsync(_fixture.UserAId, note);

        var result = await _fixture.Reconciler.RescanAsync(_fixture.UserAId);

        Assert.Equal(1, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Removed);
        Assert.Empty(result.Errors);

        var listed = await _fixture.NoteService.ListAsync(new NoteSearchQuery());
        Assert.Contains(listed, n => n.Id == note.Id && n.Title == "Added externally");
    }

    [Fact]
    public async Task RescanAsync_removes_the_index_row_for_a_file_deleted_outside_the_app()
    {
        var note = await _fixture.NoteService.CreateAsync(NoteType.Text, "To be deleted", [], "body");

        // Deleted directly through the file store (bypassing NoteService, so the index row is left behind).
        await _fixture.FileStore.DeleteAsync(_fixture.UserAId, note.Id);

        var result = await _fixture.Reconciler.RescanAsync(_fixture.UserAId);

        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(1, result.Removed);

        var listed = await _fixture.NoteService.ListAsync(new NoteSearchQuery());
        Assert.DoesNotContain(listed, n => n.Id == note.Id);
    }

    [Fact]
    public async Task RescanAsync_reindexes_a_file_edited_outside_the_app_once_its_mtime_advances()
    {
        var note = await _fixture.NoteService.CreateAsync(NoteType.Text, "Original title", [], "body");

        // Ensure the filesystem mtime actually advances before the external edit.
        await Task.Delay(TimeSpan.FromMilliseconds(1100));

        var current = await _fixture.FileStore.ReadAsync(_fixture.UserAId, note.Id);
        current.Title = "Edited externally";
        await _fixture.FileStore.WriteAsync(_fixture.UserAId, current); // bypasses NoteService's index update

        var result = await _fixture.Reconciler.RescanAsync(_fixture.UserAId);

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Removed);

        var listed = await _fixture.NoteService.ListAsync(new NoteSearchQuery());
        Assert.Contains(listed, n => n.Id == note.Id && n.Title == "Edited externally");
    }

    [Fact]
    public async Task RescanAsync_catches_up_a_note_left_stale_by_a_ContentOnly_autosave()
    {
        // A ContentOnly autosave is, by design, indistinguishable to the reconciler from "a file
        // edited outside the app" — this is what makes it self-healing with no new sync mechanism.
        var note = await _fixture.NoteService.CreateAsync(NoteType.Text, "Original title", [], "original body");
        var indexedBefore = Assert.Single(await _fixture.NoteService.ListAsync(new NoteSearchQuery()));

        await Task.Delay(TimeSpan.FromMilliseconds(1100)); // ensure the filesystem mtime actually advances
        note.Body = "autosaved body";
        await _fixture.NoteService.AutosaveAsync(note, NoteSaveScope.ContentOnly);

        // Still stale immediately after the autosave — this is the known, deliberate trade-off.
        var indexedRightAfter = Assert.Single(await _fixture.NoteService.ListAsync(new NoteSearchQuery()));
        Assert.Equal(indexedBefore.UpdatedAt, indexedRightAfter.UpdatedAt);

        var result = await _fixture.Reconciler.RescanAsync(_fixture.UserAId);

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Updated);
        var indexedAfterRescan = Assert.Single(await _fixture.NoteService.ListAsync(new NoteSearchQuery()));
        Assert.True(indexedAfterRescan.UpdatedAt > indexedBefore.UpdatedAt);
    }

    [Fact]
    public async Task RescanAsync_backfills_the_indexed_body_so_search_finds_a_ContentOnly_autosaved_change()
    {
        var note = await _fixture.NoteService.CreateAsync(NoteType.Text, "Note", [], "original body");
        await Task.Delay(TimeSpan.FromMilliseconds(1100)); // ensure the filesystem mtime actually advances

        note.Body = "contains a needle";
        await _fixture.NoteService.AutosaveAsync(note, NoteSaveScope.ContentOnly);
        Assert.Empty(await _fixture.NoteService.ListAsync(new NoteSearchQuery(SearchText: "needle")));

        await _fixture.Reconciler.RescanAsync(_fixture.UserAId);

        var results = await _fixture.NoteService.ListAsync(new NoteSearchQuery(SearchText: "needle"));
        Assert.Single(results);
    }

    [Fact]
    public async Task RescanAsync_is_a_noop_when_nothing_changed_since_the_last_index()
    {
        await _fixture.NoteService.CreateAsync(NoteType.Text, "Untouched", [], "body");

        var result = await _fixture.Reconciler.RescanAsync(_fixture.UserAId);

        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Removed);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task RescanAsync_records_an_error_and_preserves_the_existing_row_for_corrupted_front_matter()
    {
        var note = await _fixture.NoteService.CreateAsync(NoteType.Text, "Still good in the index", [], "body");

        // Corrupt the file directly on disk (not through any Core API) to simulate hand-editing gone wrong.
        var filePath = Path.Combine(_fixture.NotesFolder, _fixture.UserAId.ToString(), $"{note.Id}.md");
        await File.WriteAllTextAsync(filePath, "this is not valid front matter at all");

        var result = await _fixture.Reconciler.RescanAsync(_fixture.UserAId);

        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Removed);
        Assert.Single(result.Errors);
        Assert.Contains(note.Id.ToString(), result.Errors[0]);

        // The note stays visible with its last-known-good title rather than vanishing from listings.
        var listed = await _fixture.NoteService.ListAsync(new NoteSearchQuery());
        Assert.Contains(listed, n => n.Id == note.Id && n.Title == "Still good in the index");
    }

    [Fact]
    public async Task RescanAsync_only_reconciles_the_given_users_files_not_another_users()
    {
        // A file dropped externally into user A's folder...
        var note = new Note
        {
            Id = Guid.NewGuid(),
            Title = "User A's externally-added note",
            Type = NoteType.Text,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Body = "hello",
        };
        await _fixture.FileStore.WriteAsync(_fixture.UserAId, note);

        // ...only shows up when rescanning user A, never when rescanning user B.
        var userBResult = await _fixture.Reconciler.RescanAsync(_fixture.UserBId);
        Assert.Equal(0, userBResult.Added);

        var userAResult = await _fixture.Reconciler.RescanAsync(_fixture.UserAId);
        Assert.Equal(1, userAResult.Added);
    }
}
