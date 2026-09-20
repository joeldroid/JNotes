using JNotes.Core.Storage;
using JNotes.Core.Users;

namespace JNotes.Core.Images;

/// <inheritdoc cref="IImageService"/>
public sealed class ImageService : IImageService
{
    private readonly IImageStore _store;
    private readonly ICurrentUserContext _currentUser;

    public ImageService(IImageStore store, ICurrentUserContext currentUser)
    {
        _store = store;
        _currentUser = currentUser;
    }

    public Task<string> SaveAsync(Stream content, CancellationToken cancellationToken = default) =>
        _store.SaveAsync(_currentUser.UserId, content, cancellationToken);

    public Task<ImageContent?> OpenAsync(string fileName, CancellationToken cancellationToken = default) =>
        _store.OpenReadAsync(_currentUser.UserId, fileName, cancellationToken);
}
