using JNotes.Core.Models;
using JNotes.Core.Serialization;
using JNotes.Core.Storage;
using JNotes.Tests.TestDoubles;

namespace JNotes.Tests.Storage;

public class FileSystemNoteFileStoreTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "jnotes-tests-" + Guid.NewGuid());
    private readonly Guid _userId = Guid.NewGuid();
    private FileSystemNoteFileStore _store = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        _store = new FileSystemNoteFileStore(new StubSettingsService(_tempDir), new NoteMarkdownSerializer());
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        return Task.CompletedTask;
    }

    private static Note NewNote(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Title = "Test note",
        Tags = ["a", "b"],
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Body = "hello world",
    };

    [Fact]
    public async Task WriteAsync_then_ReadAsync_round_trips_the_note()
    {
        var note = NewNote();

        await _store.WriteAsync(_userId, note);
        var result = await _store.ReadAsync(_userId, note.Id);

        Assert.Equal(note.Id, result.Id);
        Assert.Equal(note.Title, result.Title);
        Assert.Equal(note.Body, result.Body);
    }

    [Fact]
    public async Task WriteAsync_creates_a_file_under_the_users_subfolder_named_after_the_note_id()
    {
        var note = NewNote();

        await _store.WriteAsync(_userId, note);

        Assert.True(File.Exists(Path.Combine(_tempDir, _userId.ToString(), $"{note.Id}.md")));
    }

    [Fact]
    public async Task ReadAsync_throws_NoteNotFoundException_when_no_file_exists()
    {
        await Assert.ThrowsAsync<NoteNotFoundException>(() => _store.ReadAsync(_userId, Guid.NewGuid()));
    }

    [Fact]
    public async Task ListIdsAsync_returns_every_written_note_and_ignores_non_guid_named_md_files()
    {
        var noteA = NewNote();
        var noteB = NewNote();
        await _store.WriteAsync(_userId, noteA);
        await _store.WriteAsync(_userId, noteB);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, _userId.ToString(), "readme.md"), "not a note");

        var ids = await _store.ListIdsAsync(_userId);

        Assert.Equal(2, ids.Count);
        Assert.Contains(noteA.Id, ids);
        Assert.Contains(noteB.Id, ids);
    }

    [Fact]
    public async Task ListIdsAsync_ignores_non_markdown_files()
    {
        await _store.WriteAsync(_userId, NewNote()); // ensures the user's folder exists
        await File.WriteAllTextAsync(Path.Combine(_tempDir, _userId.ToString(), $"{Guid.NewGuid()}.txt"), "not markdown");

        Assert.Single(await _store.ListIdsAsync(_userId));
    }

    [Fact]
    public async Task ListIdsAsync_never_returns_another_users_notes()
    {
        var otherUserId = Guid.NewGuid();
        await _store.WriteAsync(_userId, NewNote());
        await _store.WriteAsync(otherUserId, NewNote());

        var ids = await _store.ListIdsAsync(_userId);

        Assert.Single(ids);
        Assert.Empty(await _store.ListIdsAsync(Guid.NewGuid())); // a third, empty user
    }

    [Fact]
    public async Task DeleteAsync_removes_the_file()
    {
        var note = NewNote();
        await _store.WriteAsync(_userId, note);

        await _store.DeleteAsync(_userId, note.Id);

        Assert.False(File.Exists(Path.Combine(_tempDir, _userId.ToString(), $"{note.Id}.md")));
    }

    [Fact]
    public async Task DeleteAsync_does_not_throw_when_the_file_does_not_exist()
    {
        await _store.DeleteAsync(_userId, Guid.NewGuid());
    }

    [Fact]
    public async Task GetLastWriteTimeUtcAsync_reflects_a_subsequent_write()
    {
        var note = NewNote();
        await _store.WriteAsync(_userId, note);
        var firstWrite = await _store.GetLastWriteTimeUtcAsync(_userId, note.Id);

        await Task.Delay(TimeSpan.FromMilliseconds(50));
        note.Body = "updated body";
        await _store.WriteAsync(_userId, note);
        var secondWrite = await _store.GetLastWriteTimeUtcAsync(_userId, note.Id);

        Assert.True(secondWrite >= firstWrite);
    }

    [Fact]
    public async Task GetLastWriteTimeUtcAsync_throws_NoteNotFoundException_when_no_file_exists()
    {
        await Assert.ThrowsAsync<NoteNotFoundException>(() => _store.GetLastWriteTimeUtcAsync(_userId, Guid.NewGuid()));
    }

    [Fact]
    public async Task TrashAsync_moves_the_file_into_a_trash_subfolder()
    {
        var note = NewNote();
        await _store.WriteAsync(_userId, note);

        await _store.TrashAsync(_userId, note.Id);

        Assert.False(File.Exists(Path.Combine(_tempDir, _userId.ToString(), $"{note.Id}.md")));
        Assert.True(File.Exists(Path.Combine(_tempDir, _userId.ToString(), "trash", $"{note.Id}.md")));
    }

    [Fact]
    public async Task TrashAsync_does_not_throw_when_the_file_does_not_exist()
    {
        await _store.TrashAsync(_userId, Guid.NewGuid());
    }

    [Fact]
    public async Task ListIdsAsync_ignores_the_trash_subfolder()
    {
        var live = NewNote();
        var trashed = NewNote();
        await _store.WriteAsync(_userId, live);
        await _store.WriteAsync(_userId, trashed);
        await _store.TrashAsync(_userId, trashed.Id);

        var ids = await _store.ListIdsAsync(_userId);

        Assert.Equal([live.Id], ids);
    }

    [Fact]
    public async Task ListTrashedIdsAsync_returns_only_trashed_notes()
    {
        var live = NewNote();
        var trashed = NewNote();
        await _store.WriteAsync(_userId, live);
        await _store.WriteAsync(_userId, trashed);
        await _store.TrashAsync(_userId, trashed.Id);

        var ids = await _store.ListTrashedIdsAsync(_userId);

        Assert.Equal([trashed.Id], ids);
    }

    [Fact]
    public async Task ReadTrashedAsync_round_trips_a_trashed_note()
    {
        var note = NewNote();
        await _store.WriteAsync(_userId, note);
        await _store.TrashAsync(_userId, note.Id);

        var result = await _store.ReadTrashedAsync(_userId, note.Id);

        Assert.Equal(note.Title, result.Title);
        Assert.Equal(note.Body, result.Body);
    }

    [Fact]
    public async Task ReadTrashedAsync_throws_NoteNotFoundException_when_not_in_the_trash()
    {
        await Assert.ThrowsAsync<NoteNotFoundException>(() => _store.ReadTrashedAsync(_userId, Guid.NewGuid()));
    }

    [Fact]
    public async Task GetTrashedAtUtcAsync_throws_NoteNotFoundException_when_not_in_the_trash()
    {
        await Assert.ThrowsAsync<NoteNotFoundException>(() => _store.GetTrashedAtUtcAsync(_userId, Guid.NewGuid()));
    }

    [Fact]
    public async Task RestoreAsync_moves_the_file_back_out_of_the_trash()
    {
        var note = NewNote();
        await _store.WriteAsync(_userId, note);
        await _store.TrashAsync(_userId, note.Id);

        await _store.RestoreAsync(_userId, note.Id);

        Assert.True(File.Exists(Path.Combine(_tempDir, _userId.ToString(), $"{note.Id}.md")));
        Assert.False(File.Exists(Path.Combine(_tempDir, _userId.ToString(), "trash", $"{note.Id}.md")));
    }

    [Fact]
    public async Task RestoreAsync_does_not_throw_when_the_note_is_not_in_the_trash()
    {
        await _store.RestoreAsync(_userId, Guid.NewGuid());
    }

    [Fact]
    public async Task PurgeAsync_deletes_a_trashed_file_permanently()
    {
        var note = NewNote();
        await _store.WriteAsync(_userId, note);
        await _store.TrashAsync(_userId, note.Id);

        await _store.PurgeAsync(_userId, note.Id);

        Assert.False(File.Exists(Path.Combine(_tempDir, _userId.ToString(), "trash", $"{note.Id}.md")));
    }

    [Fact]
    public async Task PurgeAsync_does_not_throw_when_the_note_is_not_in_the_trash()
    {
        await _store.PurgeAsync(_userId, Guid.NewGuid());
    }

    [Fact]
    public async Task ReadRawAsync_returns_the_exact_written_text()
    {
        var note = NewNote();
        await _store.WriteAsync(_userId, note);

        var raw = await _store.ReadRawAsync(_userId, note.Id);

        Assert.Equal(await File.ReadAllTextAsync(NotePath(note.Id)), raw);
        Assert.Equal(new NoteMarkdownSerializer().Serialize(note), raw);
    }

    [Fact]
    public async Task ReadRawAsync_throws_NoteNotFoundException_when_no_file_exists()
    {
        await Assert.ThrowsAsync<NoteNotFoundException>(() => _store.ReadRawAsync(_userId, Guid.NewGuid()));
    }

    [Fact]
    public async Task ReplaceIfUnchangedAsync_writes_the_replacement_when_the_text_is_unchanged()
    {
        var serializer = new NoteMarkdownSerializer();
        var note = NewNote();
        await _store.WriteAsync(_userId, note);
        var raw = await _store.ReadRawAsync(_userId, note.Id);

        // Shorter than the original (fewer tags, shorter body), so a missing truncate would leave a stale tail behind.
        var replacement = NewNote(note.Id);
        replacement.Tags = [];
        replacement.Body = "x";

        Assert.True(await _store.ReplaceIfUnchangedAsync(_userId, note.Id, raw, replacement));

        var expectedBytes = new System.Text.UTF8Encoding(false).GetBytes(serializer.Serialize(replacement));
        Assert.Equal(expectedBytes, await File.ReadAllBytesAsync(NotePath(note.Id))); // exact, and no BOM
    }

    [Fact]
    public async Task ReplaceIfUnchangedAsync_leaves_the_file_alone_when_the_text_differs()
    {
        var note = NewNote();
        await _store.WriteAsync(_userId, note);
        var before = await File.ReadAllBytesAsync(NotePath(note.Id));
        var replacement = NewNote(note.Id);
        replacement.Body = "something else";

        Assert.False(await _store.ReplaceIfUnchangedAsync(_userId, note.Id, "not what is on disk", replacement));

        Assert.Equal(before, await File.ReadAllBytesAsync(NotePath(note.Id)));
    }

    [Fact]
    public async Task ReplaceIfUnchangedAsync_returns_false_when_the_file_is_missing()
    {
        var note = NewNote();

        Assert.False(await _store.ReplaceIfUnchangedAsync(_userId, note.Id, "anything", note));
        Assert.False(File.Exists(NotePath(note.Id)));
    }

    [Fact]
    public async Task ReplaceIfUnchangedAsync_only_sees_the_given_users_folder()
    {
        var note = NewNote();
        await _store.WriteAsync(_userId, note);
        var raw = await _store.ReadRawAsync(_userId, note.Id);
        var before = await File.ReadAllBytesAsync(NotePath(note.Id));

        Assert.False(await _store.ReplaceIfUnchangedAsync(Guid.NewGuid(), note.Id, raw, NewNote(note.Id)));

        Assert.Equal(before, await File.ReadAllBytesAsync(NotePath(note.Id)));
    }

    [Fact]
    public async Task ReplaceIfUnchangedAsync_throws_and_writes_nothing_while_another_handle_has_the_file_open()
    {
        var note = NewNote();
        await _store.WriteAsync(_userId, note);
        var raw = await _store.ReadRawAsync(_userId, note.Id);
        var before = await File.ReadAllBytesAsync(NotePath(note.Id));
        var replacement = NewNote(note.Id);
        replacement.Body = "changed";

        // Like an editor's in-flight autosave: a permissive handle, which FileShare.None must still refuse.
        await using (var other = new FileStream(NotePath(note.Id), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            await Assert.ThrowsAsync<IOException>(() => _store.ReplaceIfUnchangedAsync(_userId, note.Id, raw, replacement));
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(NotePath(note.Id)));
    }

    private string NotePath(Guid noteId) => Path.Combine(_tempDir, _userId.ToString(), $"{noteId}.md");
}
