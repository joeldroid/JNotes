using JNotes.Core;
using JNotes.Core.Data;
using JNotes.Core.Models;
using JNotes.Core.Storage;
using JNotes.Core.Users;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JNotes.Tests.Users;

public class UserManagementServiceTests : IAsyncLifetime
{
    private readonly string _rootDir = Path.Combine(Path.GetTempPath(), "jnotes-tests-" + Guid.NewGuid());
    private string _notesFolder = "";
    private ServiceProvider _provider = null!;
    private IUserRepository _userRepository = null!;
    private IUserManagementService _userManagementService = null!;
    private INoteFileStore _fileStore = null!;

    public async Task InitializeAsync()
    {
        _notesFolder = Path.Combine(_rootDir, "notes");

        var services = new ServiceCollection();
        services.AddJNotesCore(new ConfigurationBuilder().Build());
        services.Configure<JNotesOptions>(o =>
        {
            o.NotesFolderPath = _notesFolder;
            o.DbPath = Path.Combine(_rootDir, "jnotes.db");
        });
        _provider = services.BuildServiceProvider();
        await _provider.MigrateJNotesDatabaseAsync();

        _userRepository = _provider.GetRequiredService<IUserRepository>();
        _userManagementService = _provider.GetRequiredService<IUserManagementService>();
        _fileStore = _provider.GetRequiredService<INoteFileStore>();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        if (Directory.Exists(_rootDir))
        {
            Directory.Delete(_rootDir, recursive: true);
        }
    }

    private static Note NewNote() => new()
    {
        Id = Guid.NewGuid(),
        Title = "Test note",
        Type = NoteType.Text,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Body = "hello",
    };

    [Fact]
    public async Task DeleteUserAsync_moves_the_notes_folder_to_deleted_and_removes_the_account_row()
    {
        var admin = await _userRepository.CreateUserAsync("admin", UserRole.Admin, null, "password123");
        var member = await _userRepository.CreateUserAsync("member", UserRole.Member, null, "password123");
        var note = NewNote();
        await _fileStore.WriteAsync(member.Id, note);

        await _userManagementService.DeleteUserAsync(member.Id, admin.Id);

        Assert.Null(await _userRepository.GetUserByIdAsync(member.Id));
        Assert.False(Directory.Exists(Path.Combine(_notesFolder, member.Id.ToString())));
        Assert.True(File.Exists(Path.Combine(_notesFolder, "deleted", member.Id.ToString(), $"{note.Id}.md")));
    }

    [Fact]
    public async Task DeleteUserAsync_is_idempotent_when_the_folder_was_already_archived()
    {
        var admin = await _userRepository.CreateUserAsync("admin", UserRole.Admin, null, "password123");
        var member = await _userRepository.CreateUserAsync("member", UserRole.Member, null, "password123");
        await _fileStore.WriteAsync(member.Id, NewNote());

        // Simulates a retry after the DB-delete step failed on a first attempt: the file move
        // already happened, the account row is still there.
        await _fileStore.ArchiveUserFolderAsync(member.Id);

        await _userManagementService.DeleteUserAsync(member.Id, admin.Id);

        Assert.Null(await _userRepository.GetUserByIdAsync(member.Id));
    }

    [Fact]
    public async Task DeleteUserAsync_throws_CannotDeleteSelfException_and_leaves_the_account_untouched()
    {
        var admin = await _userRepository.CreateUserAsync("admin", UserRole.Admin, null, "password123");

        await Assert.ThrowsAsync<CannotDeleteSelfException>(
            () => _userManagementService.DeleteUserAsync(admin.Id, admin.Id));

        Assert.NotNull(await _userRepository.GetUserByIdAsync(admin.Id));
    }

    [Fact]
    public async Task DeleteUserAsync_throws_CannotDeleteLastAdminException_and_leaves_the_account_untouched()
    {
        var admin = await _userRepository.CreateUserAsync("admin", UserRole.Admin, null, "password123");
        var member = await _userRepository.CreateUserAsync("member", UserRole.Member, null, "password123");

        await Assert.ThrowsAsync<CannotDeleteLastAdminException>(
            () => _userManagementService.DeleteUserAsync(admin.Id, member.Id));

        Assert.NotNull(await _userRepository.GetUserByIdAsync(admin.Id));
    }

    [Fact]
    public async Task DeleteUserAsync_allows_deleting_an_admin_when_another_admin_remains()
    {
        var adminA = await _userRepository.CreateUserAsync("admin-a", UserRole.Admin, null, "password123");
        var adminB = await _userRepository.CreateUserAsync("admin-b", UserRole.Admin, null, "password123");

        await _userManagementService.DeleteUserAsync(adminB.Id, adminA.Id);

        Assert.Null(await _userRepository.GetUserByIdAsync(adminB.Id));
    }

    [Fact]
    public async Task UpdateProfileAsync_lets_a_member_change_their_own_name()
    {
        var member = await _userRepository.CreateUserAsync("member", UserRole.Member, null, "password123");

        await _userManagementService.UpdateProfileAsync(member.Id, member.Id, "Mem", "Ber", null);

        var reloaded = await _userRepository.GetUserByIdAsync(member.Id);
        Assert.Equal("Mem", reloaded!.FirstName);
        Assert.Equal("Ber", reloaded.LastName);
    }

    [Fact]
    public async Task UpdateProfileAsync_refuses_a_member_changing_their_own_email()
    {
        var member = await _userRepository.CreateUserAsync("member", UserRole.Member, "member@example.com", "password123");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _userManagementService.UpdateProfileAsync(member.Id, member.Id, "Mem", null, "new@example.com"));

        var reloaded = await _userRepository.GetUserByIdAsync(member.Id);
        Assert.Equal("member@example.com", reloaded!.Email);
        Assert.Null(reloaded.FirstName); // the whole call is refused — nothing partially applied
    }

    [Fact]
    public async Task UpdateProfileAsync_allows_a_member_saving_their_own_email_back_unchanged()
    {
        var member = await _userRepository.CreateUserAsync("member", UserRole.Member, "member@example.com", "password123");

        await _userManagementService.UpdateProfileAsync(member.Id, member.Id, "Mem", null, "member@example.com");

        var reloaded = await _userRepository.GetUserByIdAsync(member.Id);
        Assert.Equal("Mem", reloaded!.FirstName);
    }

    [Fact]
    public async Task UpdateProfileAsync_lets_an_admin_change_their_own_email()
    {
        var admin = await _userRepository.CreateUserAsync("admin", UserRole.Admin, "admin@example.com", "password123");

        await _userManagementService.UpdateProfileAsync(admin.Id, admin.Id, null, null, "new-admin@example.com");

        var reloaded = await _userRepository.GetUserByIdAsync(admin.Id);
        Assert.Equal("new-admin@example.com", reloaded!.Email);
    }

    [Fact]
    public async Task UpdateProfileAsync_lets_an_admin_edit_another_users_full_profile()
    {
        var admin = await _userRepository.CreateUserAsync("admin", UserRole.Admin, null, "password123");
        var member = await _userRepository.CreateUserAsync("member", UserRole.Member, null, "password123");

        await _userManagementService.UpdateProfileAsync(member.Id, admin.Id, "Mem", "Ber", "member@example.com");

        var reloaded = await _userRepository.GetUserByIdAsync(member.Id);
        Assert.Equal("Mem", reloaded!.FirstName);
        Assert.Equal("member@example.com", reloaded.Email);
    }

    [Fact]
    public async Task UpdateProfileAsync_refuses_a_member_editing_someone_elses_profile()
    {
        var memberA = await _userRepository.CreateUserAsync("member-a", UserRole.Member, null, "password123");
        var memberB = await _userRepository.CreateUserAsync("member-b", UserRole.Member, null, "password123");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _userManagementService.UpdateProfileAsync(memberB.Id, memberA.Id, "Hacked", null, null));

        Assert.Null((await _userRepository.GetUserByIdAsync(memberB.Id))!.FirstName);
    }
}
