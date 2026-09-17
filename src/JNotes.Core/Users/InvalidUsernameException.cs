namespace JNotes.Core.Users;

/// <summary>Thrown by <see cref="IUserRepository.CreateUserAsync"/> when the username isn't all lowercase.</summary>
public sealed class InvalidUsernameException : Exception
{
    public string Username { get; }

    public InvalidUsernameException(string username)
        : base($"Username '{username}' must be lowercase.")
    {
        Username = username;
    }
}
