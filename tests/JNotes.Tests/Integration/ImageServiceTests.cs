using JNotes.Core.Images;
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
}
