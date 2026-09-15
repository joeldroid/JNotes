namespace JNotes.Core.Users;

/// <summary>Thrown by <see cref="IUserRepository.CreateUserAsync"/> when the email is already linked to another account.</summary>
public sealed class DuplicateEmailException : Exception
{
    public string Email { get; }

    public DuplicateEmailException(string email, Exception? innerException = null)
        : base($"Email '{email}' is already in use by another account.", innerException)
    {
        Email = email;
    }
}
