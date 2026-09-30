using JNotes.Core.Data;
using JNotes.Core.Sharing;
using Npgsql;

namespace JNotes.Tests.Integration;

public class NoteShareRepositoryTests : IAsyncLifetime
{
    private CoreServicesFixture _fixture = null!;

    public async Task InitializeAsync() => _fixture = await CoreServicesFixture.CreateAsync();

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private INoteShareRepository Repository => _fixture.ShareRepository;

    private static Task UpsertAsync(INoteShareRepository repository, Guid owner, Guid noteId, Guid recipient, NoteSharePermission permission = NoteSharePermission.Read) =>
        repository.UpsertAsync(owner, noteId, recipient, permission, DateTimeOffset.UtcNow);

    [Fact]
    public async Task The_check_constraint_rejects_a_self_share()
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => UpsertAsync(Repository, _fixture.UserAId, Guid.NewGuid(), _fixture.UserAId));

        Assert.Equal("CK_NoteShares_NotSelf", exception.ConstraintName);
    }

    [Fact]
    public async Task A_share_needs_real_accounts_on_both_sides()
    {
        await Assert.ThrowsAsync<PostgresException>(() => UpsertAsync(Repository, _fixture.UserAId, Guid.NewGuid(), Guid.NewGuid()));
        await Assert.ThrowsAsync<PostgresException>(() => UpsertAsync(Repository, Guid.NewGuid(), Guid.NewGuid(), _fixture.UserBId));
    }

    [Fact]
    public async Task Deleting_the_recipients_account_cascades_the_share()
    {
        var recipient = await _fixture.CreateUserAsync("recipient");
        var noteId = Guid.NewGuid();
        await UpsertAsync(Repository, _fixture.UserAId, noteId, recipient);
        await UpsertAsync(Repository, _fixture.UserAId, noteId, _fixture.UserBId);

        await _fixture.UserRepository.DeleteUserAsync(recipient);

        Assert.Null(await Repository.FindAsync(noteId, recipient));
        Assert.Equal(_fixture.UserBId, Assert.Single(await Repository.ListForNoteAsync(_fixture.UserAId, noteId)).UserId);
    }

    [Fact]
    public async Task Deleting_the_owners_account_cascades_the_share()
    {
        var owner = await _fixture.CreateUserAsync("owner");
        var noteId = Guid.NewGuid();
        await UpsertAsync(Repository, owner, noteId, _fixture.UserBId);
        Assert.NotNull(await Repository.FindAsync(noteId, _fixture.UserBId));

        await _fixture.UserRepository.DeleteUserAsync(owner);

        Assert.Null(await Repository.FindAsync(noteId, _fixture.UserBId));
        Assert.Empty(await Repository.ListForNoteAsync(owner, noteId));
    }

    [Fact]
    public async Task Upsert_changes_the_permission_but_never_takes_over_another_owners_row()
    {
        var noteId = Guid.NewGuid();
        await UpsertAsync(Repository, _fixture.UserAId, noteId, _fixture.UserBId, NoteSharePermission.Read);

        await UpsertAsync(Repository, _fixture.UserAId, noteId, _fixture.UserBId, NoteSharePermission.Edit);
        Assert.Equal(NoteSharePermission.Edit, (await Repository.FindAsync(noteId, _fixture.UserBId))!.Permission);

        var other = await _fixture.CreateUserAsync("other");
        await UpsertAsync(Repository, other, noteId, _fixture.UserBId, NoteSharePermission.Read);

        var grant = await Repository.FindAsync(noteId, _fixture.UserBId);
        Assert.Equal(_fixture.UserAId, grant!.OwnerUserId);
        Assert.Equal(NoteSharePermission.Edit, grant.Permission);
        Assert.Equal("user-a", grant.OwnerDisplayName);
    }

    [Fact]
    public async Task Deletes_are_scoped_to_the_owner_or_the_recipient()
    {
        var noteId = Guid.NewGuid();
        await UpsertAsync(Repository, _fixture.UserAId, noteId, _fixture.UserBId);

        Assert.False(await Repository.DeleteAsync(_fixture.UserBId, noteId, _fixture.UserBId)); // not the owner
        Assert.False(await Repository.DeleteForRecipientAsync(noteId, _fixture.UserAId)); // not the recipient
        await Repository.DeleteAllForNoteAsync(_fixture.UserBId, noteId); // not the owner
        Assert.NotNull(await Repository.FindAsync(noteId, _fixture.UserBId));

        Assert.True(await Repository.DeleteForRecipientAsync(noteId, _fixture.UserBId));
        Assert.Null(await Repository.FindAsync(noteId, _fixture.UserBId));

        await UpsertAsync(Repository, _fixture.UserAId, noteId, _fixture.UserBId);
        await Repository.DeleteAllForNoteAsync(_fixture.UserAId, noteId);
        Assert.Empty(await Repository.ListForNoteAsync(_fixture.UserAId, noteId));
    }

    [Fact]
    public async Task ListForNote_orders_by_display_name()
    {
        var zed = await _fixture.CreateUserAsync("zed");
        await _fixture.UserRepository.UpdateProfileAsync(zed, "Aaron", "Zed", email: null);
        var noteId = Guid.NewGuid();
        await UpsertAsync(Repository, _fixture.UserAId, noteId, _fixture.UserBId);
        await UpsertAsync(Repository, _fixture.UserAId, noteId, zed);

        var entries = await Repository.ListForNoteAsync(_fixture.UserAId, noteId);

        Assert.Equal(["Aaron Zed", "user-b"], entries.Select(e => e.DisplayName));
        Assert.Equal(["zed", "user-b"], entries.Select(e => e.Username));
    }

    [Fact]
    public async Task SearchCandidates_excludes_the_owner_and_existing_recipients()
    {
        var carol = await _fixture.CreateUserAsync("carol");
        var noteId = Guid.NewGuid();
        await UpsertAsync(Repository, _fixture.UserAId, noteId, _fixture.UserBId);

        var candidates = await Repository.SearchCandidatesAsync(_fixture.UserAId, noteId, term: null, limit: 10);

        Assert.Equal(carol, Assert.Single(candidates).UserId);
        Assert.Equal("carol", candidates[0].DisplayName);
    }

    [Fact]
    public async Task SearchCandidates_matches_username_and_names_case_insensitively()
    {
        var carol = await _fixture.CreateUserAsync("carol");
        await _fixture.UserRepository.UpdateProfileAsync(carol, "Carolyn", "Whitfield", email: null);
        var dave = await _fixture.CreateUserAsync("dave");
        await _fixture.UserRepository.UpdateProfileAsync(dave, null, "Carter", email: null);
        var noteId = Guid.NewGuid();

        Assert.Equal(["carol", "dave"], (await Repository.SearchCandidatesAsync(_fixture.UserAId, noteId, "CAR", 10)).Select(c => c.Username));
        Assert.Equal(["carol"], (await Repository.SearchCandidatesAsync(_fixture.UserAId, noteId, "whit", 10)).Select(c => c.Username));
        Assert.Equal(["dave"], (await Repository.SearchCandidatesAsync(_fixture.UserAId, noteId, "carter", 10)).Select(c => c.Username));
        Assert.Equal(["user-b"], (await Repository.SearchCandidatesAsync(_fixture.UserAId, noteId, "USER-B", 10)).Select(c => c.Username));
        Assert.Empty(await Repository.SearchCandidatesAsync(_fixture.UserAId, noteId, "nobody", 10));
        Assert.Equal("Carolyn Whitfield", (await Repository.SearchCandidatesAsync(_fixture.UserAId, noteId, "whit", 10))[0].DisplayName);
    }

    [Fact]
    public async Task SearchCandidates_treats_percent_and_underscore_literally()
    {
        await _fixture.CreateUserAsync("a_b");
        await _fixture.CreateUserAsync("axb");
        await _fixture.CreateUserAsync("50%off");
        var noteId = Guid.NewGuid();

        Assert.Equal(["a_b"], (await Repository.SearchCandidatesAsync(_fixture.UserAId, noteId, "a_b", 10)).Select(c => c.Username));
        Assert.Equal(["50%off"], (await Repository.SearchCandidatesAsync(_fixture.UserAId, noteId, "%", 10)).Select(c => c.Username));
    }

    [Fact]
    public async Task SearchCandidates_never_matches_on_email()
    {
        await _fixture.UserRepository.CreateUserAsync("emailonly", UserRole.Member, "findme@example.com", password: "password123");
        var noteId = Guid.NewGuid();

        Assert.Empty(await Repository.SearchCandidatesAsync(_fixture.UserAId, noteId, "findme", 10));
        Assert.Empty(await Repository.SearchCandidatesAsync(_fixture.UserAId, noteId, "example.com", 10));
        Assert.Equal(["emailonly"], (await Repository.SearchCandidatesAsync(_fixture.UserAId, noteId, "emailonly", 10)).Select(c => c.Username));
    }

    [Fact]
    public async Task SearchCandidates_clamps_the_limit()
    {
        for (var i = 0; i < 25; i++)
        {
            await _fixture.CreateUserAsync($"extra{i:00}");
        }

        var noteId = Guid.NewGuid();

        Assert.Equal(20, (await Repository.SearchCandidatesAsync(_fixture.UserAId, noteId, "extra", 100)).Count);
        Assert.Single(await Repository.SearchCandidatesAsync(_fixture.UserAId, noteId, "extra", 0));
        Assert.Equal("extra00", (await Repository.SearchCandidatesAsync(_fixture.UserAId, noteId, "extra", 0))[0].Username);
    }
}
