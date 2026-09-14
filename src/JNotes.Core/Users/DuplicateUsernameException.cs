namespace JNotes.Core.Users;

/// <summary>Thrown by <see cref="IUserRepository.CreateUserAsync"/> when the username is already taken (case-insensitively).</summary>
public sealed class DuplicateUsernameException : Exception
{
    public string Username { get; }

    public DuplicateUsernameException(string username, Exception innerException)
        : base($"Username '{username}' is already taken.", innerException)
    {
        Username = username;
    }
}
