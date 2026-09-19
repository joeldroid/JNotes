using JNotes.Core.Images;
using JNotes.Core.Settings;

namespace JNotes.Core.Storage;

/// <inheritdoc cref="IImageStore"/>
public sealed class FileSystemImageStore : IImageStore
{
    private readonly ISettingsService _settingsService;

    public FileSystemImageStore(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public async Task<string> SaveAsync(Guid userId, Stream content, CancellationToken cancellationToken = default)
    {
        var header = new byte[ImageFormat.HeaderLength];
        var headerLength = await content.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        var type = ImageFormat.Detect(header.AsSpan(0, headerLength)) ?? throw new UnsupportedImageException();

        var folder = await ResolveFolderAsync(userId, cancellationToken);
        Directory.CreateDirectory(folder);
        var fileName = $"{Guid.NewGuid():N}.{type.Extension}";
        var path = Path.Combine(folder, fileName);

        try
        {
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await file.WriteAsync(header.AsMemory(0, headerLength), cancellationToken);

                // Stream the rest straight to disk, counting as we go, so an oversized upload is
                // cut off at the limit instead of being buffered whole.
                long total = headerLength;
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > ImageFormat.MaxBytes)
                    {
                        throw new ImageTooLargeException();
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
        }
        catch
        {
            File.Delete(path); // no-op if it was never created
            throw;
        }

        return fileName;
    }

    public async Task<ImageContent?> OpenReadAsync(Guid userId, string fileName, CancellationToken cancellationToken = default)
    {
        // Validating the name is the path-traversal guard: only ever a name we generated.
        var contentType = ImageFormat.ContentTypeOf(fileName);
        if (contentType is null)
        {
            return null;
        }

        var path = Path.Combine(await ResolveFolderAsync(userId, cancellationToken), fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        return new ImageContent(stream, contentType);
    }

    private async Task<string> ResolveFolderAsync(Guid userId, CancellationToken cancellationToken)
    {
        var notesRoot = await _settingsService.GetNotesFolderPathAsync(cancellationToken);
        return Path.Combine(notesRoot, userId.ToString(), ImageLinks.FolderName);
    }
}
