using JNotes.Core.Storage;
using JNotes.Core.Users;

namespace JNotes.Core.Images;

/// <inheritdoc cref="IImageService"/>
public sealed class ImageService : IImageService
{
    private readonly IImageStore _store;
    private readonly INoteFileStore _noteFileStore;
    private readonly ICurrentUserContext _currentUser;

    public ImageService(IImageStore store, INoteFileStore noteFileStore, ICurrentUserContext currentUser)
    {
        _store = store;
        _noteFileStore = noteFileStore;
        _currentUser = currentUser;
    }

    public Task<string> SaveAsync(Stream content, CancellationToken cancellationToken = default) =>
        _store.SaveAsync(_currentUser.UserId, content, cancellationToken);

    public Task<ImageContent?> OpenAsync(string fileName, CancellationToken cancellationToken = default) =>
        _store.OpenReadAsync(_currentUser.UserId, fileName, cancellationToken);

    public async Task<ImageCleanupResult> CleanUpUnusedAsync(TimeSpan minAge, CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.UserId;

        // Listing before scanning means an image uploaded mid-scan is never a candidate at all.
        var images = await _store.ListAsync(userId, cancellationToken);

        // Read every note before deleting anything: if a read throws, no image has been touched.
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var content in _noteFileStore.EnumerateRawContentsAsync(userId, cancellationToken))
        {
            referenced.UnionWith(ImageFormat.FindStoredFileNames(content));
        }

        var cutoff = DateTimeOffset.UtcNow - minAge;
        int deleted = 0, skippedRecent = 0;
        long bytesFreed = 0;
        foreach (var image in images.Where(i => !referenced.Contains(i.FileName)))
        {
            if (image.LastWriteUtc > cutoff)
            {
                skippedRecent++;
                continue;
            }

            await _store.DeleteAsync(userId, image.FileName, cancellationToken);
            deleted++;
            bytesFreed += image.SizeBytes;
        }

        return new ImageCleanupResult(deleted, bytesFreed, skippedRecent);
    }
}
