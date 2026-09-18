using JNotes.Core.Indexing;
using JNotes.Core.Models;
using JNotes.Core.Notes;
using Microsoft.Extensions.Logging.Abstractions;

namespace JNotes.Tests.Integration;

public class AllUsersIndexReconcilerTests : IAsyncLifetime
{
    private CoreServicesFixture _fixture = null!;

    public async Task InitializeAsync() => _fixture = await CoreServicesFixture.CreateAsync();

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private static Note NoteFor(string title) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Type = NoteType.Text,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Body = "hello",
    };

    [Fact]
    public async Task ReconcileAllAsync_indexes_files_for_every_user()
    {
        // Written directly through the file store (bypassing NoteService, so no index row is
        // created) to simulate files that exist for both users but aren't indexed yet.
        var noteA = NoteFor("User A's note");
        var noteB = NoteFor("User B's note");
        await _fixture.FileStore.WriteAsync(_fixture.UserAId, noteA);
        await _fixture.FileStore.WriteAsync(_fixture.UserBId, noteB);

        var result = await _fixture.AllUsersReconciler.ReconcileAllAsync();

        Assert.Equal(2, result.Added);
        Assert.Equal(2, result.UsersReconciled);
        Assert.Equal(0, result.UsersFailed);

        _fixture.ActAsUserA();
        var listedA = await _fixture.NoteService.ListAsync(new NoteSearchQuery());
        Assert.Contains(listedA, n => n.Id == noteA.Id);

        _fixture.ActAsUserB();
        var listedB = await _fixture.NoteService.ListAsync(new NoteSearchQuery());
        Assert.Contains(listedB, n => n.Id == noteB.Id);
    }

    /// <summary>Delegates to a real reconciler for every user except one, which it always fails.</summary>
    private sealed class FailingForOneUserReconciler(Guid failingUserId, INoteIndexReconciler inner) : INoteIndexReconciler
    {
        public Task<ReconciliationResult> RescanAsync(Guid userId, CancellationToken cancellationToken = default) =>
            userId == failingUserId
                ? throw new InvalidOperationException("Simulated reconciliation failure.")
                : inner.RescanAsync(userId, cancellationToken);
    }

    [Fact]
    public async Task ReconcileAllAsync_continues_after_one_user_fails()
    {
        // A file added for each user, so a successful pass would index both.
        var noteA = NoteFor("User A's note");
        var noteB = NoteFor("User B's note");
        await _fixture.FileStore.WriteAsync(_fixture.UserAId, noteA);
        await _fixture.FileStore.WriteAsync(_fixture.UserBId, noteB);

        // Deliberately failing the first/oldest user (GetAllUsersAsync is CreatedAt-ascending, so
        // user-a comes first) proves the loop didn't abort after the first failure.
        var failingReconciler = new FailingForOneUserReconciler(_fixture.UserAId, _fixture.Reconciler);
        var allUsersReconciler = new AllUsersIndexReconciler(
            _fixture.UserRepository, failingReconciler, NullLogger<AllUsersIndexReconciler>.Instance);

        var result = await allUsersReconciler.ReconcileAllAsync();

        Assert.Equal(1, result.UsersFailed);
        Assert.Equal(1, result.UsersReconciled);
        Assert.Equal(1, result.Added);
        Assert.Single(result.Errors);
        Assert.Contains(_fixture.UserAId.ToString(), result.Errors[0]);

        _fixture.ActAsUserB();
        var listedB = await _fixture.NoteService.ListAsync(new NoteSearchQuery());
        Assert.Contains(listedB, n => n.Id == noteB.Id);
    }

    [Fact]
    public async Task ReconcileAllAsync_honours_a_cancelled_token()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _fixture.AllUsersReconciler.ReconcileAllAsync(cts.Token));
    }

    [Fact]
    public async Task ReconcileAllAsync_is_a_noop_with_no_changes()
    {
        _fixture.ActAsUserA();
        await _fixture.NoteService.CreateAsync(NoteType.Text, "User A's note", [], "body");
        _fixture.ActAsUserB();
        await _fixture.NoteService.CreateAsync(NoteType.Text, "User B's note", [], "body");

        var result = await _fixture.AllUsersReconciler.ReconcileAllAsync();

        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Removed);
        Assert.Equal(0, result.UsersFailed);
        Assert.Equal(2, result.UsersReconciled);
        Assert.Empty(result.Errors);
    }
}
