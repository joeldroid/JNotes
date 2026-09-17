namespace JNotes.Core.Users;

/// <summary>Thrown by <see cref="IUserRepository.CreateUserAsync"/> when the email doesn't look like a valid address.</summary>
public sealed class InvalidEmailException : Exception
{
    public string Email { get; }

    public InvalidEmailException(string email)
        : base($"'{email}' is not a valid email address.")
    {
        Email = email;
    }
}
