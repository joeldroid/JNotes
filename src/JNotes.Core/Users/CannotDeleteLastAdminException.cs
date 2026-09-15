namespace JNotes.Core.Users;

/// <summary>Thrown by <see cref="IUserManagementService.DeleteUserAsync"/> when the target is the only remaining Admin — deleting them would lock everyone out of user management.</summary>
public sealed class CannotDeleteLastAdminException : Exception
{
    public CannotDeleteLastAdminException()
        : base("You can't delete the last remaining admin.")
    {
    }
}
