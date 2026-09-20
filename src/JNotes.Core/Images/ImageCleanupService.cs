using JNotes.Core.Settings;
using JNotes.Core.Storage;
using JNotes.Core.Users;
using Microsoft.Extensions.Logging;

namespace JNotes.Core.Images;

/// <inheritdoc cref="IImageCleanupService"/>
public sealed class ImageCleanupService : IImageCleanupService
{
    private readonly IImageStore _imageStore;
    private readonly INoteFileStore _noteFileStore;
    private readonly IUserRepository _userRepository;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<ImageCleanupService> _logger;

    public ImageCleanupService(
        IImageStore imageStore,
        INoteFileStore noteFileStore,
        IUserRepository userRepository,
        ISettingsService settingsService,
        ILogger<ImageCleanupService> logger)
    {
        _imageStore = imageStore;
        _noteFileStore = noteFileStore;
        _userRepository = userRepository;
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task<ImageCleanupResult> CleanUpUserAsync(Guid userId, TimeSpan minAge, CancellationToken cancellationToken = default)
    {
        // Listing before scanning means an image uploaded mid-scan is never a candidate at all.
        var images = await _imageStore.ListAsync(userId, cancellationToken);

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

            await _imageStore.DeleteAsync(userId, image.FileName, cancellationToken);
            deleted++;
            bytesFreed += image.SizeBytes;
        }

        return new ImageCleanupResult(deleted, bytesFreed, skippedRecent);
    }

    public async Task<AllUsersImageCleanupResult> CleanUpAllUsersAsync(TimeSpan minAge, CancellationToken cancellationToken = default)
    {
        var users = await _userRepository.GetAllUsersAsync(cancellationToken);

        int usersCleaned = 0, usersFailed = 0, deleted = 0, skippedRecent = 0;
        long bytesFreed = 0;
        foreach (var user in users)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var result = await CleanUpUserAsync(user.Id, minAge, cancellationToken);
                deleted += result.Deleted;
                bytesFreed += result.BytesFreed;
                skippedRecent += result.SkippedRecent;
                usersCleaned++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // shutdown, not a per-user failure
            }
            catch (Exception ex)
            {
                // One user's I/O failure must not stop everyone else's cleanup.
                _logger.LogWarning(ex, "Image cleanup failed for user {UserId}; continuing with the remaining users.", user.Id);
                usersFailed++;
            }
        }

        // Recorded even if some users failed: the next attempt is a full interval away either way, and
        // re-running immediately on every poll would just hammer whatever is failing.
        await _settingsService.SetImageCleanupLastRunUtcAsync(DateTimeOffset.UtcNow, cancellationToken);

        return new AllUsersImageCleanupResult(usersCleaned, usersFailed, deleted, bytesFreed, skippedRecent);
    }
}
