namespace JNotes.Core.Users;

/// <summary>Thrown by <see cref="IUserManagementService.DeleteUserAsync"/> when the requesting user tries to delete their own account.</summary>
public sealed class CannotDeleteSelfException : Exception
{
    public CannotDeleteSelfException()
        : base("You can't delete your own account.")
    {
    }
}
