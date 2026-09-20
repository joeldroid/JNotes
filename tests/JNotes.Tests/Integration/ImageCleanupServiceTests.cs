using JNotes.Core.Images;
using JNotes.Core.Models;
using JNotes.Core.Settings;
using JNotes.Tests.Images;
using Microsoft.Extensions.DependencyInjection;

namespace JNotes.Tests.Integration;

public class ImageCleanupServiceTests : IAsyncLifetime
{
    private CoreServicesFixture _fixture = null!;

    public async Task InitializeAsync() => _fixture = await CoreServicesFixture.CreateAsync();

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private IImageService Images => _fixture.Scope.ServiceProvider.GetRequiredService<IImageService>();

    private IImageCleanupService Cleanup => _fixture.Scope.ServiceProvider.GetRequiredService<IImageCleanupService>();

    private ISettingsService Settings => _fixture.Scope.ServiceProvider.GetRequiredService<ISettingsService>();

    private string ImagePath(Guid userId, string fileName) => Path.Combine(_fixture.NotesFolder, userId.ToString(), "images", fileName);

    private string ImagePathForUserA(string fileName) => ImagePath(_fixture.UserAId, fileName);

    /// <summary>Saves an image as whoever the fixture is currently acting as.</summary>
    private async Task<string> SaveImageAsync(int bytes = 64)
    {
        var data = new byte[bytes];
        ImageFormatTests.Png.CopyTo(data, 0);
        return await Images.SaveAsync(new MemoryStream(data));
    }

    private Task<Note> CreateNoteWithAsync(string body) =>
        _fixture.NoteService.CreateAsync(NoteType.Text, "note", [], body);

    private Task<ImageCleanupResult> CleanUpUserAAsync(TimeSpan? minAge = null) =>
        Cleanup.CleanUpUserAsync(_fixture.UserAId, minAge ?? TimeSpan.Zero);

    [Fact]
    public async Task CleanUpUserAsync_deletes_an_image_no_note_mentions()
    {
        var orphan = await SaveImageAsync(100);

        var result = await CleanUpUserAAsync();

        Assert.Equal(new ImageCleanupResult(1, 100, 0), result);
        Assert.False(File.Exists(ImagePathForUserA(orphan)));
    }

    [Fact]
    public async Task CleanUpUserAsync_keeps_an_image_a_live_note_links_to()
    {
        var image = await SaveImageAsync();
        await CreateNoteWithAsync($"see ![shot](images/{image})");

        var result = await CleanUpUserAAsync();

        Assert.Equal(0, result.Deleted);
        Assert.True(File.Exists(ImagePathForUserA(image)));
    }

    [Fact]
    public async Task CleanUpUserAsync_keeps_an_image_used_by_a_trashed_note_until_that_note_is_purged()
    {
        var image = await SaveImageAsync();
        var note = await CreateNoteWithAsync($"![shot](images/{image})");
        await _fixture.NoteService.TrashAsync(note.Id);

        await CleanUpUserAAsync();
        Assert.True(File.Exists(ImagePathForUserA(image)), "restorable from the trash, so the image must survive");

        await _fixture.NoteService.PurgeAsync(note.Id);
        var result = await CleanUpUserAAsync();

        Assert.Equal(1, result.Deleted);
        Assert.False(File.Exists(ImagePathForUserA(image)));
    }

    [Fact]
    public async Task CleanUpUserAsync_keeps_an_image_shared_by_two_notes_when_only_one_is_purged()
    {
        var image = await SaveImageAsync();
        var kept = await CreateNoteWithAsync($"![a](images/{image})");
        var purged = await CreateNoteWithAsync($"![b](images/{image})");
        await _fixture.NoteService.TrashAsync(purged.Id);
        await _fixture.NoteService.PurgeAsync(purged.Id);

        await CleanUpUserAAsync();

        Assert.True(File.Exists(ImagePathForUserA(image)));

        await _fixture.NoteService.TrashAsync(kept.Id);
        await _fixture.NoteService.PurgeAsync(kept.Id);
        await CleanUpUserAAsync();

        Assert.False(File.Exists(ImagePathForUserA(image)));
    }

    [Fact]
    public async Task CleanUpUserAsync_deletes_an_image_whose_link_was_edited_out_of_a_live_note()
    {
        var image = await SaveImageAsync();
        var note = await CreateNoteWithAsync($"![shot](images/{image})");
        note.Body = "no image any more";
        await _fixture.NoteService.UpdateAsync(note);

        var result = await CleanUpUserAAsync();

        Assert.Equal(1, result.Deleted);
    }

    [Fact]
    public async Task CleanUpUserAsync_keeps_an_image_mentioned_in_a_note_with_corrupted_front_matter()
    {
        var image = await SaveImageAsync();
        var folder = Path.Combine(_fixture.NotesFolder, _fixture.UserAId.ToString());
        await File.WriteAllTextAsync(Path.Combine(folder, $"{Guid.NewGuid()}.md"), $"---\ntitle: [unclosed\n---\n![x](images/{image})");

        var result = await CleanUpUserAAsync();

        Assert.Equal(0, result.Deleted);
        Assert.True(File.Exists(ImagePathForUserA(image)));
    }

    [Fact]
    public async Task CleanUpUserAsync_matches_the_image_name_case_insensitively()
    {
        var image = await SaveImageAsync();
        await CreateNoteWithAsync($"![x](images/{image.ToUpperInvariant()})");

        var result = await CleanUpUserAAsync();

        Assert.Equal(0, result.Deleted);
    }

    [Fact]
    public async Task CleanUpUserAsync_spares_a_recent_image_and_reports_it_as_skipped()
    {
        await SaveImageAsync();

        var result = await CleanUpUserAAsync(TimeSpan.FromMinutes(10));

        Assert.Equal(new ImageCleanupResult(0, 0, 1), result);
    }

    [Fact]
    public async Task CleanUpUserAsync_deletes_an_unreferenced_image_once_it_is_older_than_minAge()
    {
        var image = await SaveImageAsync();
        File.SetLastWriteTimeUtc(ImagePathForUserA(image), DateTime.UtcNow.AddHours(-1));

        var result = await CleanUpUserAAsync(TimeSpan.FromMinutes(10));

        Assert.Equal(1, result.Deleted);
        Assert.Equal(0, result.SkippedRecent);
    }

    [Fact]
    public async Task CleanUpUserAsync_never_deletes_a_file_that_is_not_a_stored_image_name()
    {
        await SaveImageAsync();
        var stray = ImagePathForUserA("notes.txt");
        await File.WriteAllTextAsync(stray, "mine");

        await CleanUpUserAAsync();

        Assert.True(File.Exists(stray));
    }

    [Fact]
    public async Task CleanUpUserAsync_only_touches_the_given_users_images()
    {
        _fixture.ActAsUserB();
        var bImage = await SaveImageAsync();

        await CleanUpUserAAsync();

        Assert.True(File.Exists(ImagePath(_fixture.UserBId, bImage)));
    }

    [Fact]
    public async Task CleanUpUserAsync_with_no_images_folder_is_a_no_op()
    {
        Assert.Equal(new ImageCleanupResult(0, 0, 0), await CleanUpUserAAsync());
    }

    [Fact]
    public async Task CleanUpAllUsersAsync_cleans_every_users_orphans_but_keeps_referenced_images()
    {
        _fixture.ActAsUserA();
        var aOrphan = await SaveImageAsync(20);
        var aUsed = await SaveImageAsync();
        await CreateNoteWithAsync($"![x](images/{aUsed})");

        _fixture.ActAsUserB();
        var bOrphan = await SaveImageAsync(30);

        var result = await Cleanup.CleanUpAllUsersAsync(TimeSpan.Zero);

        Assert.Equal(new AllUsersImageCleanupResult(UsersCleaned: 2, UsersFailed: 0, Deleted: 2, BytesFreed: 50, SkippedRecent: 0), result);
        Assert.False(File.Exists(ImagePath(_fixture.UserAId, aOrphan)));
        Assert.True(File.Exists(ImagePath(_fixture.UserAId, aUsed)));
        Assert.False(File.Exists(ImagePath(_fixture.UserBId, bOrphan)));
    }

    [Fact]
    public async Task CleanUpAllUsersAsync_records_when_it_ran()
    {
        Assert.Null(await Settings.GetImageCleanupLastRunUtcAsync());
        var before = DateTimeOffset.UtcNow;

        await Cleanup.CleanUpAllUsersAsync(TimeSpan.Zero);

        var lastRun = await Settings.GetImageCleanupLastRunUtcAsync();
        Assert.NotNull(lastRun);
        Assert.InRange(lastRun.Value, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
    }
}
