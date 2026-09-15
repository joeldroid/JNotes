using JNotes.Core;
using JNotes.Core.Data;
using JNotes.Core.Users;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JNotes.Tests.Users;

public class EfUserRepositoryTests : IAsyncLifetime
{
    private readonly string _rootDir = Path.Combine(Path.GetTempPath(), "jnotes-tests-" + Guid.NewGuid());
    private ServiceProvider _provider = null!;
    private IUserRepository _repository = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddJNotesCore(new ConfigurationBuilder().Build());
        services.Configure<JNotesOptions>(o =>
        {
            o.NotesFolderPath = Path.Combine(_rootDir, "notes");
            o.DbPath = Path.Combine(_rootDir, "jnotes.db");
        });
        _provider = services.BuildServiceProvider();
        await _provider.MigrateJNotesDatabaseAsync();
        _repository = _provider.GetRequiredService<IUserRepository>();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        if (Directory.Exists(_rootDir))
        {
            Directory.Delete(_rootDir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateUserAsync_returns_a_user_with_a_new_id_and_the_given_username_and_role()
    {
        var user = await _repository.CreateUserAsync("alice", UserRole.Admin, email: null, password: "password123");

        Assert.NotEqual(Guid.Empty, user.Id);
        Assert.Equal("alice", user.Username);
        Assert.Equal(UserRole.Admin, user.Role);
        Assert.NotNull(user.PasswordHash);
    }

    [Fact]
    public async Task CreateUserAsync_leaves_PasswordHash_null_when_no_password_is_given()
    {
        var user = await _repository.CreateUserAsync("alice", UserRole.Member, email: "alice@example.com", password: null);

        Assert.Null(user.PasswordHash);
        Assert.Equal("alice@example.com", user.Email);
    }

    [Fact]
    public async Task CreateUserAsync_throws_DuplicateUsernameException_for_an_exact_duplicate()
    {
        await _repository.CreateUserAsync("alice", UserRole.Member, null, "password123");

        await Assert.ThrowsAsync<DuplicateUsernameException>(
            () => _repository.CreateUserAsync("alice", UserRole.Member, null, "password123"));
    }

    [Theory]
    [InlineData("Alice")]
    [InlineData("ALICE")]
    public async Task CreateUserAsync_throws_DuplicateUsernameException_case_insensitively(string duplicate)
    {
        await _repository.CreateUserAsync("alice", UserRole.Member, null, "password123");

        await Assert.ThrowsAsync<DuplicateUsernameException>(
            () => _repository.CreateUserAsync(duplicate, UserRole.Member, null, "password123"));
    }

    [Fact]
    public async Task CreateUserAsync_throws_DuplicateEmailException_for_a_duplicate_email()
    {
        await _repository.CreateUserAsync("alice", UserRole.Member, "shared@example.com", "password123");

        await Assert.ThrowsAsync<DuplicateEmailException>(
            () => _repository.CreateUserAsync("bob", UserRole.Member, "shared@example.com", "password123"));
    }

    [Fact]
    public async Task GetAllUsersAsync_returns_every_user_ordered_by_CreatedAt_ascending()
    {
        var first = await _repository.CreateUserAsync("first", UserRole.Member, null, "password123");
        var second = await _repository.CreateUserAsync("second", UserRole.Member, null, "password123");

        var users = await _repository.GetAllUsersAsync();

        Assert.Equal([first.Id, second.Id], users.Select(u => u.Id));
    }

    [Fact]
    public async Task GetUserByIdAsync_returns_null_for_an_unknown_id()
    {
        Assert.Null(await _repository.GetUserByIdAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetUserByIdAsync_returns_the_matching_user()
    {
        var created = await _repository.CreateUserAsync("alice", UserRole.Member, null, "password123");

        var found = await _repository.GetUserByIdAsync(created.Id);

        Assert.NotNull(found);
        Assert.Equal("alice", found.Username);
    }

    [Fact]
    public async Task GetUserByUsernameAsync_returns_the_matching_user_case_insensitively()
    {
        await _repository.CreateUserAsync("alice", UserRole.Member, null, "password123");

        var found = await _repository.GetUserByUsernameAsync("ALICE");

        Assert.NotNull(found);
        Assert.Equal("alice", found.Username);
    }

    [Fact]
    public async Task VerifyPasswordAsync_returns_the_user_for_the_right_password()
    {
        await _repository.CreateUserAsync("alice", UserRole.Member, null, "correct-password");

        var user = await _repository.VerifyPasswordAsync("alice", "correct-password");

        Assert.NotNull(user);
        Assert.Equal("alice", user.Username);
    }

    [Fact]
    public async Task VerifyPasswordAsync_returns_null_for_the_wrong_password()
    {
        await _repository.CreateUserAsync("alice", UserRole.Member, null, "correct-password");

        Assert.Null(await _repository.VerifyPasswordAsync("alice", "wrong-password"));
    }

    [Fact]
    public async Task VerifyPasswordAsync_returns_null_for_an_unknown_username()
    {
        Assert.Null(await _repository.VerifyPasswordAsync("nobody", "whatever"));
    }

    [Fact]
    public async Task VerifyPasswordAsync_returns_null_for_an_account_with_no_password_set()
    {
        await _repository.CreateUserAsync("alice", UserRole.Member, "alice@example.com", password: null);

        Assert.Null(await _repository.VerifyPasswordAsync("alice", "anything"));
    }

    [Fact]
    public async Task GetUnlinkedUserByEmailAsync_matches_an_account_with_that_email_and_no_OidcSubject()
    {
        var created = await _repository.CreateUserAsync("alice", UserRole.Member, "alice@example.com", password: null);

        var found = await _repository.GetUnlinkedUserByEmailAsync("alice@example.com");

        Assert.NotNull(found);
        Assert.Equal(created.Id, found.Id);
    }

    [Fact]
    public async Task GetUnlinkedUserByEmailAsync_does_not_match_an_already_linked_account()
    {
        var created = await _repository.CreateUserAsync("alice", UserRole.Member, "alice@example.com", password: null);
        await _repository.LinkOidcSubjectAsync(created.Id, "some-subject");

        Assert.Null(await _repository.GetUnlinkedUserByEmailAsync("alice@example.com"));
    }

    [Fact]
    public async Task GetUserByOidcSubjectAsync_returns_the_linked_user()
    {
        var created = await _repository.CreateUserAsync("alice", UserRole.Member, "alice@example.com", password: null);
        await _repository.LinkOidcSubjectAsync(created.Id, "some-subject");

        var found = await _repository.GetUserByOidcSubjectAsync("some-subject");

        Assert.NotNull(found);
        Assert.Equal(created.Id, found.Id);
    }

    [Fact]
    public async Task SetPasswordAsync_lets_the_user_authenticate_with_the_new_password()
    {
        var created = await _repository.CreateUserAsync("alice", UserRole.Member, null, "old-password");

        await _repository.SetPasswordAsync(created.Id, "new-password");

        Assert.Null(await _repository.VerifyPasswordAsync("alice", "old-password"));
        Assert.NotNull(await _repository.VerifyPasswordAsync("alice", "new-password"));
    }

    [Fact]
    public async Task DeleteUserAsync_removes_the_user()
    {
        var created = await _repository.CreateUserAsync("alice", UserRole.Member, null, "password123");

        await _repository.DeleteUserAsync(created.Id);

        Assert.Null(await _repository.GetUserByIdAsync(created.Id));
    }

    [Fact]
    public async Task DeleteUserAsync_does_not_throw_for_an_unknown_id()
    {
        await _repository.DeleteUserAsync(Guid.NewGuid());
    }

    [Fact]
    public async Task CreateUserAsync_marks_setup_complete_immediately()
    {
        // ISetupStateProvider gates every request to /setup until this flips — must be true right
        // after creation, not just after the next restart's HostExtensions.InitializeSetupStateAsync.
        var setupState = _provider.GetRequiredService<ISetupStateProvider>();
        Assert.False(setupState.IsSetupComplete);

        await _repository.CreateUserAsync("alice", UserRole.Admin, null, "password123");

        Assert.True(setupState.IsSetupComplete);
    }
}
