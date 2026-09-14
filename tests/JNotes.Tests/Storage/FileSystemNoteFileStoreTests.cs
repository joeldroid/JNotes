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
        Type = NoteType.Text,
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
}
