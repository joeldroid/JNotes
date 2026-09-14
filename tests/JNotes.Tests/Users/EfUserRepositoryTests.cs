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
    public async Task CreateUserAsync_returns_a_user_with_a_new_id_and_the_given_username()
    {
        var user = await _repository.CreateUserAsync("alice");

        Assert.NotEqual(Guid.Empty, user.Id);
        Assert.Equal("alice", user.Username);
    }

    [Fact]
    public async Task CreateUserAsync_throws_DuplicateUsernameException_for_an_exact_duplicate()
    {
        await _repository.CreateUserAsync("alice");

        await Assert.ThrowsAsync<DuplicateUsernameException>(() => _repository.CreateUserAsync("alice"));
    }

    [Theory]
    [InlineData("Alice")]
    [InlineData("ALICE")]
    public async Task CreateUserAsync_throws_DuplicateUsernameException_case_insensitively(string duplicate)
    {
        await _repository.CreateUserAsync("alice");

        await Assert.ThrowsAsync<DuplicateUsernameException>(() => _repository.CreateUserAsync(duplicate));
    }

    [Fact]
    public async Task GetAllUsersAsync_returns_every_user_ordered_by_CreatedAt_ascending()
    {
        var first = await _repository.CreateUserAsync("first");
        var second = await _repository.CreateUserAsync("second");

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
        var created = await _repository.CreateUserAsync("alice");

        var found = await _repository.GetUserByIdAsync(created.Id);

        Assert.NotNull(found);
        Assert.Equal("alice", found.Username);
    }

    [Fact]
    public async Task CreateUserAsync_registers_the_new_user_with_the_DefaultUserProvider_cache()
    {
        // CookieCurrentUserContext falls back to the default user whenever a cookie names a user
        // IsKnownUser doesn't recognize — this must stay true immediately after creation, not just
        // after the next restart, or a freshly-created user couldn't be acted-as until a restart.
        var defaultUserProvider = _provider.GetRequiredService<DefaultUserProvider>();

        var user = await _repository.CreateUserAsync("alice");

        Assert.True(defaultUserProvider.IsKnownUser(user.Id));
        Assert.False(defaultUserProvider.IsKnownUser(Guid.NewGuid()));
    }
}
