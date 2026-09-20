using JNotes.Core.Images;
using JNotes.Core.Models;
using JNotes.Tests.Images;
using Microsoft.Extensions.DependencyInjection;

namespace JNotes.Tests.Integration;

public class ImageServiceTests : IAsyncLifetime
{
    private CoreServicesFixture _fixture = null!;

    public async Task InitializeAsync() => _fixture = await CoreServicesFixture.CreateAsync();

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private IImageService Images => _fixture.Scope.ServiceProvider.GetRequiredService<IImageService>();

    private string ImagePathForUserA(string fileName) => Path.Combine(_fixture.NotesFolder, _fixture.UserAId.ToString(), "images", fileName);

    private static MemoryStream PngWithPayload(int totalBytes)
    {
        var bytes = new byte[totalBytes];
        ImageFormatTests.Png.CopyTo(bytes, 0);
        return new MemoryStream(bytes);
    }

    [Fact]
    public async Task SaveAsync_stores_the_image_under_the_users_images_folder_with_a_generated_name()
    {
        var fileName = await Images.SaveAsync(PngWithPayload(100));

        Assert.True(ImageFormat.IsStoredFileName(fileName));
        Assert.EndsWith(".png", fileName);
        Assert.Equal(100, new FileInfo(ImagePathForUserA(fileName)).Length);
    }

    [Fact]
    public async Task SaveAsync_gives_each_upload_its_own_name()
    {
        var first = await Images.SaveAsync(PngWithPayload(20));
        var second = await Images.SaveAsync(PngWithPayload(20));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task SaveAsync_throws_and_writes_nothing_for_a_non_image()
    {
        await Assert.ThrowsAsync<UnsupportedImageException>(() =>
            Images.SaveAsync(new MemoryStream("just some text, not an image"u8.ToArray())));

        Assert.False(Directory.Exists(Path.Combine(_fixture.NotesFolder, _fixture.UserAId.ToString(), "images")));
    }

    [Fact]
    public async Task SaveAsync_throws_and_leaves_no_partial_file_when_the_image_is_too_large()
    {
        await Assert.ThrowsAsync<ImageTooLargeException>(() => Images.SaveAsync(PngWithPayload(ImageFormat.MaxBytes + 1)));

        var folder = Path.Combine(_fixture.NotesFolder, _fixture.UserAId.ToString(), "images");
        Assert.Empty(Directory.EnumerateFiles(folder));
    }

    [Fact]
    public async Task SaveAsync_accepts_an_image_of_exactly_the_maximum_size()
    {
        var fileName = await Images.SaveAsync(PngWithPayload(ImageFormat.MaxBytes));

        Assert.Equal(ImageFormat.MaxBytes, new FileInfo(ImagePathForUserA(fileName)).Length);
    }

    [Fact]
    public async Task OpenAsync_returns_the_saved_bytes_with_the_right_content_type()
    {
        var fileName = await Images.SaveAsync(PngWithPayload(64));

        var image = await Images.OpenAsync(fileName);

        Assert.NotNull(image);
        await using var stream = image.Stream;
        Assert.Equal("image/png", image.ContentType);
        Assert.Equal(64, stream.Length);
    }

    [Fact]
    public async Task OpenAsync_returns_null_for_another_users_image()
    {
        _fixture.ActAsUserA();
        var fileName = await Images.SaveAsync(PngWithPayload(64));

        _fixture.ActAsUserB();

        Assert.Null(await Images.OpenAsync(fileName));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("0123456789abcdef0123456789abcdef.png")] // valid shape, doesn't exist
    [InlineData("")]
    public async Task OpenAsync_returns_null_for_an_invalid_or_missing_name(string fileName)
    {
        Assert.Null(await Images.OpenAsync(fileName));
    }

    [Fact]
    public async Task The_images_folder_does_not_disturb_note_listing()
    {
        await Images.SaveAsync(PngWithPayload(64));

        var ids = await _fixture.FileStore.ListIdsAsync(_fixture.UserAId);

        Assert.Empty(ids);
    }

    // --- CleanUpUnusedAsync ---

    private async Task<string> SaveImageAsync(int bytes = 64) => await Images.SaveAsync(PngWithPayload(bytes));

    private Task<Note> CreateNoteWithAsync(string body) =>
        _fixture.NoteService.CreateAsync(NoteType.Text, "note", [], body);

    [Fact]
    public async Task CleanUpUnusedAsync_deletes_an_image_no_note_mentions()
    {
        var orphan = await SaveImageAsync(100);

        var result = await Images.CleanUpUnusedAsync(TimeSpan.Zero);

        Assert.Equal(new ImageCleanupResult(1, 100, 0), result);
        Assert.False(File.Exists(ImagePathForUserA(orphan)));
    }

    [Fact]
    public async Task CleanUpUnusedAsync_keeps_an_image_a_live_note_links_to()
    {
        var image = await SaveImageAsync();
        await CreateNoteWithAsync($"see ![shot](images/{image})");

        var result = await Images.CleanUpUnusedAsync(TimeSpan.Zero);

        Assert.Equal(0, result.Deleted);
        Assert.True(File.Exists(ImagePathForUserA(image)));
    }

    [Fact]
    public async Task CleanUpUnusedAsync_keeps_an_image_used_by_a_trashed_note_until_that_note_is_purged()
    {
        var image = await SaveImageAsync();
        var note = await CreateNoteWithAsync($"![shot](images/{image})");
        await _fixture.NoteService.TrashAsync(note.Id);

        await Images.CleanUpUnusedAsync(TimeSpan.Zero);
        Assert.True(File.Exists(ImagePathForUserA(image)), "restorable from the trash, so the image must survive");

        await _fixture.NoteService.PurgeAsync(note.Id);
        var result = await Images.CleanUpUnusedAsync(TimeSpan.Zero);

        Assert.Equal(1, result.Deleted);
        Assert.False(File.Exists(ImagePathForUserA(image)));
    }

    [Fact]
    public async Task CleanUpUnusedAsync_keeps_an_image_shared_by_two_notes_when_only_one_is_purged()
    {
        var image = await SaveImageAsync();
        var kept = await CreateNoteWithAsync($"![a](images/{image})");
        var purged = await CreateNoteWithAsync($"![b](images/{image})");
        await _fixture.NoteService.TrashAsync(purged.Id);
        await _fixture.NoteService.PurgeAsync(purged.Id);

        await Images.CleanUpUnusedAsync(TimeSpan.Zero);

        Assert.True(File.Exists(ImagePathForUserA(image)));

        await _fixture.NoteService.TrashAsync(kept.Id);
        await _fixture.NoteService.PurgeAsync(kept.Id);
        await Images.CleanUpUnusedAsync(TimeSpan.Zero);

        Assert.False(File.Exists(ImagePathForUserA(image)));
    }

    [Fact]
    public async Task CleanUpUnusedAsync_deletes_an_image_whose_link_was_edited_out_of_a_live_note()
    {
        var image = await SaveImageAsync();
        var note = await CreateNoteWithAsync($"![shot](images/{image})");
        note.Body = "no image any more";
        await _fixture.NoteService.UpdateAsync(note);

        var result = await Images.CleanUpUnusedAsync(TimeSpan.Zero);

        Assert.Equal(1, result.Deleted);
    }

    [Fact]
    public async Task CleanUpUnusedAsync_keeps_an_image_mentioned_in_a_note_with_corrupted_front_matter()
    {
        var image = await SaveImageAsync();
        var folder = Path.Combine(_fixture.NotesFolder, _fixture.UserAId.ToString());
        await File.WriteAllTextAsync(Path.Combine(folder, $"{Guid.NewGuid()}.md"), $"---\ntitle: [unclosed\n---\n![x](images/{image})");

        var result = await Images.CleanUpUnusedAsync(TimeSpan.Zero);

        Assert.Equal(0, result.Deleted);
        Assert.True(File.Exists(ImagePathForUserA(image)));
    }

    [Fact]
    public async Task CleanUpUnusedAsync_matches_the_image_name_case_insensitively()
    {
        var image = await SaveImageAsync();
        await CreateNoteWithAsync($"![x](images/{image.ToUpperInvariant()})");

        var result = await Images.CleanUpUnusedAsync(TimeSpan.Zero);

        Assert.Equal(0, result.Deleted);
    }

    [Fact]
    public async Task CleanUpUnusedAsync_spares_a_recent_image_and_reports_it_as_skipped()
    {
        await SaveImageAsync();

        var result = await Images.CleanUpUnusedAsync(TimeSpan.FromMinutes(10));

        Assert.Equal(new ImageCleanupResult(0, 0, 1), result);
    }

    [Fact]
    public async Task CleanUpUnusedAsync_deletes_an_unreferenced_image_once_it_is_older_than_minAge()
    {
        var image = await SaveImageAsync();
        File.SetLastWriteTimeUtc(ImagePathForUserA(image), DateTime.UtcNow.AddHours(-1));

        var result = await Images.CleanUpUnusedAsync(TimeSpan.FromMinutes(10));

        Assert.Equal(1, result.Deleted);
        Assert.Equal(0, result.SkippedRecent);
    }

    [Fact]
    public async Task CleanUpUnusedAsync_never_deletes_a_file_that_is_not_a_stored_image_name()
    {
        await SaveImageAsync();
        var stray = Path.Combine(_fixture.NotesFolder, _fixture.UserAId.ToString(), "images", "notes.txt");
        await File.WriteAllTextAsync(stray, "mine");

        await Images.CleanUpUnusedAsync(TimeSpan.Zero);

        Assert.True(File.Exists(stray));
    }

    [Fact]
    public async Task CleanUpUnusedAsync_only_touches_the_current_users_images()
    {
        _fixture.ActAsUserB();
        var bImage = await SaveImageAsync();
        var bPath = Path.Combine(_fixture.NotesFolder, _fixture.UserBId.ToString(), "images", bImage);

        _fixture.ActAsUserA();
        await Images.CleanUpUnusedAsync(TimeSpan.Zero);

        Assert.True(File.Exists(bPath));
    }

    [Fact]
    public async Task CleanUpUnusedAsync_with_no_images_folder_is_a_no_op()
    {
        var result = await Images.CleanUpUnusedAsync(TimeSpan.Zero);

        Assert.Equal(new ImageCleanupResult(0, 0, 0), result);
    }
}
