using JNotes.Core.Data;
using JNotes.Core.Models;
using JNotes.Core.Notes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JNotes.Tests.Integration;

/// <summary>
/// Pins the per-user tag suggestions and their backing NoteIndexUserTags projection — kept in sync
/// inside <c>PostgresNoteIndexRepository</c>'s own transactions and rebuilt by every rescan.
/// </summary>
public class TagSuggestionTests : IAsyncLifetime
{
    private CoreServicesFixture _fixture = null!;

    public async Task InitializeAsync() => _fixture = await CoreServicesFixture.CreateAsync();

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private IDbContextFactory<JNotesDbContext> DbFactory =>
        _fixture.Scope.ServiceProvider.GetRequiredService<IDbContextFactory<JNotesDbContext>>();

    private Task<Note> CreateAsync(string title, params string[] tags) =>
        _fixture.NoteService.CreateAsync(title, tags, "body");

    private async Task<List<NoteIndexUserTag>> UserTagRowsAsync(Guid userId)
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        return await db.NoteIndexUserTags.AsNoTracking().Where(u => u.UserId == userId).ToListAsync();
    }

    [Fact]
    public async Task Suggestions_are_isolated_per_user()
    {
        _fixture.ActAsUserA();
        await CreateAsync("a1", "alpha", "shared");
        _fixture.ActAsUserB();
        await CreateAsync("b1", "alphabet", "shared");

        _fixture.ActAsUserA();
        Assert.Equal(["alpha"], await _fixture.NoteService.SuggestTagsAsync("alp", []));
        _fixture.ActAsUserB();
        Assert.Equal(["alphabet"], await _fixture.NoteService.SuggestTagsAsync("alp", []));

        _fixture.ActAsUserA();
        Assert.Equal(["alpha", "shared"], (await _fixture.NoteService.SuggestTagsAsync("", [])).Order().ToList());
        _fixture.ActAsUserB();
        Assert.Equal(["alphabet", "shared"], (await _fixture.NoteService.SuggestTagsAsync("", [])).Order().ToList());

        var repoForB = await _fixture.IndexRepository.SuggestTagsAsync(_fixture.UserBId, "", [], 50);
        Assert.DoesNotContain("alpha", repoForB);
        var shared = Assert.Single(await UserTagRowsAsync(_fixture.UserBId), r => r.Tag == "shared");
        Assert.Equal(1, shared.NoteCount);
    }

    [Fact]
    public async Task Deleting_through_another_users_id_leaves_the_tags_intact()
    {
        _fixture.ActAsUserA();
        var note = await CreateAsync("a1", "alpha");

        await _fixture.IndexRepository.DeleteAsync(_fixture.UserBId, note.Id);

        Assert.Equal(["alpha"], await _fixture.NoteService.SuggestTagsAsync("alp", []));
        Assert.Single(await _fixture.NoteService.ListAsync(new NoteSearchQuery()));
    }

    [Fact]
    public async Task Creating_a_note_makes_its_tags_suggestible_and_more_used_tags_rank_first()
    {
        await CreateAsync("n1", "work");
        Assert.Equal(["work"], await _fixture.NoteService.SuggestTagsAsync("wo", []));

        await CreateAsync("n2", "work");
        await CreateAsync("n3", "wont");

        Assert.Equal(["work", "wont"], await _fixture.NoteService.SuggestTagsAsync("wo", []));
    }

    [Fact]
    public async Task A_metadata_update_swaps_the_suggested_tags_and_keeps_shared_ones_with_a_lower_count()
    {
        var first = await CreateAsync("n1", "old");
        await CreateAsync("n2", "old");
        var solo = await CreateAsync("n3", "gone");

        first.Tags = ["new"];
        await _fixture.NoteService.UpdateAsync(first);
        solo.Tags = ["fresh"];
        await _fixture.NoteService.UpdateAsync(solo);

        var suggestions = await _fixture.NoteService.SuggestTagsAsync("", []);
        Assert.Contains("new", suggestions);
        Assert.Contains("fresh", suggestions);
        Assert.Contains("old", suggestions);
        Assert.DoesNotContain("gone", suggestions);

        var oldRow = Assert.Single(await UserTagRowsAsync(_fixture.UserAId), r => r.Tag == "old");
        Assert.Equal(1, oldRow.NoteCount);
    }

    [Fact]
    public async Task A_content_only_save_does_not_change_suggestions_until_a_rescan()
    {
        var note = await CreateAsync("n1", "before");

        note.Tags = ["after"];
        await _fixture.NoteService.AutosaveAsync(note, NoteSaveScope.ContentOnly);

        Assert.Equal(["before"], await _fixture.NoteService.SuggestTagsAsync("", []));

        await Task.Delay(1100); // let the file mtime pass the indexed one
        await _fixture.Reconciler.RescanAsync(_fixture.UserAId);

        Assert.Equal(["after"], await _fixture.NoteService.SuggestTagsAsync("", []));
    }

    [Fact]
    public async Task Trashing_removes_a_tag_only_that_note_used_and_restoring_brings_it_back()
    {
        var solo = await CreateAsync("solo", "only-here");
        await CreateAsync("other", "kept");

        await _fixture.NoteService.TrashAsync(solo.Id);
        Assert.Equal(["kept"], await _fixture.NoteService.SuggestTagsAsync("", []));

        await _fixture.NoteService.RestoreAsync(solo.Id);
        Assert.Contains("only-here", await _fixture.NoteService.SuggestTagsAsync("", []));

        await _fixture.NoteService.TrashAsync(solo.Id);
        await _fixture.NoteService.PurgeAsync(solo.Id);
        Assert.Equal(["kept"], await _fixture.NoteService.SuggestTagsAsync("", []));
    }

    [Fact]
    public async Task A_rescan_picks_up_externally_added_and_removed_files()
    {
        var now = DateTimeOffset.UtcNow;
        var external = new Note
        {
            Id = Guid.NewGuid(),
            Title = "External",
            Tags = ["external"],
            CreatedAt = now,
            UpdatedAt = now,
            Body = "b",
        };
        await _fixture.FileStore.WriteAsync(_fixture.UserAId, external);
        await _fixture.Reconciler.RescanAsync(_fixture.UserAId);
        Assert.Equal(["external"], await _fixture.NoteService.SuggestTagsAsync("ext", []));

        await _fixture.FileStore.DeleteAsync(_fixture.UserAId, external.Id);
        await _fixture.Reconciler.RescanAsync(_fixture.UserAId);
        Assert.Empty(await _fixture.NoteService.SuggestTagsAsync("ext", []));
    }

    [Fact]
    public async Task A_rescan_rebuilds_a_truncated_table_even_when_every_file_is_unchanged()
    {
        await CreateAsync("n1", "alpha");
        await CreateAsync("n2", "beta");

        await using (var db = await DbFactory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("TRUNCATE \"NoteIndexUserTags\"");
        }

        Assert.Empty(await _fixture.NoteService.SuggestTagsAsync("", []));

        var result = await _fixture.Reconciler.RescanAsync(_fixture.UserAId);

        Assert.Equal(0, result.Updated);
        Assert.Equal(["alpha", "beta"], (await _fixture.NoteService.SuggestTagsAsync("", [])).Order().ToList());
    }

    [Fact]
    public async Task Tags_differing_only_in_case_stay_distinct_and_are_excluded_case_insensitively()
    {
        var upper = await CreateAsync("n1", "Work");
        await CreateAsync("n2", "work");

        var suggestions = await _fixture.NoteService.SuggestTagsAsync("WOR", []);
        Assert.Equal(2, suggestions.Count);
        Assert.Contains("Work", suggestions);
        Assert.Contains("work", suggestions);

        Assert.Equal(["Work", "work"], await _fixture.NoteService.GetDistinctTagsAsync());
        Assert.Empty(await _fixture.NoteService.SuggestTagsAsync("WOR", ["WORK"]));

        var filtered = await _fixture.NoteService.ListAsync(new NoteSearchQuery(Tag: "Work"));
        Assert.Equal(upper.Id, Assert.Single(filtered).Id);
    }

    [Fact]
    public async Task Prefix_matches_beat_count_and_an_empty_term_orders_by_count_and_honours_the_limit()
    {
        for (var i = 0; i < 3; i++)
        {
            await CreateAsync($"h{i}", "homework");
        }

        await CreateAsync("w", "work");

        Assert.Equal(["work", "homework"], await _fixture.NoteService.SuggestTagsAsync("work", []));

        await CreateAsync("x1", "rare");
        Assert.Equal(["homework"], await _fixture.NoteService.SuggestTagsAsync("", [], limit: 1));
        var all = await _fixture.NoteService.SuggestTagsAsync("", []);
        Assert.Equal("homework", all[0]);
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public async Task Like_wildcards_in_the_term_are_matched_literally()
    {
        await CreateAsync("n1", "100%", "1000", "snake_case", "snakeXcase");

        Assert.Equal(["100%"], await _fixture.NoteService.SuggestTagsAsync("100%", []));
        Assert.Equal(["snake_case"], await _fixture.NoteService.SuggestTagsAsync("snake_", []));
    }

    [Fact]
    public async Task A_tag_repeated_within_one_note_counts_once()
    {
        await CreateAsync("n1", "x", "x");

        var row = Assert.Single(await UserTagRowsAsync(_fixture.UserAId));
        Assert.Equal("x", row.Tag);
        Assert.Equal(1, row.NoteCount);
    }
}
