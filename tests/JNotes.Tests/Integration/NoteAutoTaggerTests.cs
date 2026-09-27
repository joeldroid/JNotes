using JNotes.Core.Data;
using JNotes.Core.Models;
using JNotes.Core.Notes;
using JNotes.Core.Tagging;
using JNotes.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JNotes.Tests.Integration;

/// <summary>
/// Pins the background tagger against a real PostgreSQL and a temp notes folder, with a recording stub for
/// Ollama and a movable clock: only untagged notes are touched, only the tags change, and a note is written
/// only if nobody changed it meanwhile.
/// </summary>
public class NoteAutoTaggerTests : IAsyncLifetime
{
    private CoreServicesFixture _fixture = null!;

    public async Task InitializeAsync() => _fixture = await CoreServicesFixture.CreateAsync();

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private INoteAutoTagger Tagger => _fixture.NoteAutoTagger;

    private RecordingTagModelClient Model => _fixture.TagModel;

    private Task<Note> CreateAsync(string title, string body = "some body text", params string[] tags) =>
        _fixture.NoteService.CreateAsync(NoteType.Text, title, tags, body);

    // Notes are only candidates once they're older than minAge; the tests pass minAge = 0 and step the clock past creation.
    private Task<AutoTagPassResult> TagAAsync(int maxNotes = 10, TimeSpan? minAge = null)
    {
        _fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        return Tagger.TagUserNotesAsync(_fixture.UserAId, maxNotes, minAge ?? TimeSpan.Zero);
    }

    private Task<Note> ReadAsync(Guid userId, Guid noteId) => _fixture.FileStore.ReadAsync(userId, noteId);

    private Task<string> RawAsync(Guid userId, Guid noteId) => _fixture.FileStore.ReadRawAsync(userId, noteId);

    [Fact]
    public async Task Tags_an_untagged_note_without_touching_UpdatedAt_or_the_reconciler()
    {
        var note = await CreateAsync("Intro to neural networks");
        var before = await ReadAsync(_fixture.UserAId, note.Id);
        Model.NextTags = ["Machine Learning", "#AI", "note"];

        var result = await TagAAsync();

        Assert.Equal(1, result.Tagged);
        Assert.Null(result.UnavailableReason);
        var after = await ReadAsync(_fixture.UserAId, note.Id);
        Assert.Equal(["machine-learning", "ai"], after.Tags);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);

        var listed = Assert.Single(await _fixture.NoteService.ListAsync(new NoteSearchQuery()));
        Assert.Equal(["machine-learning", "ai"], listed.Tags);
        Assert.Equal(JNotes.Core.Indexing.IndexTimestamp.ToIndexResolution(before.UpdatedAt), listed.UpdatedAt); // the index keeps microseconds
        Assert.Equal(["machine-learning"], await _fixture.NoteService.SuggestTagsAsync("machine", []));

        // The index already knows the file's new mtime, so a rescan doesn't re-parse it.
        Assert.Equal(0, (await _fixture.Reconciler.RescanAsync(_fixture.UserAId)).Updated);

        var attempt = await _fixture.AutoTagState.GetAsync(_fixture.UserAId, note.Id);
        Assert.Equal(AutoTagOutcome.Tagged, attempt!.Outcome);
        Assert.Equal(0, attempt.FailureCount);
        Assert.Null(attempt.RetryAfterUtc);
    }

    [Fact]
    public async Task Only_the_tags_lines_of_the_file_change()
    {
        var note = await CreateAsync("Groceries", "Buy milk\n\n- eggs\n- bread\n");
        var before = await RawAsync(_fixture.UserAId, note.Id);
        Model.NextTags = ["shopping", "food"];

        await TagAAsync();

        var after = await RawAsync(_fixture.UserAId, note.Id);
        Assert.NotEqual(before, after);

        var (beforeFrontMatter, beforeRest) = SplitAtClosingDelimiter(before);
        var (afterFrontMatter, afterRest) = SplitAtClosingDelimiter(after);
        Assert.Equal(beforeRest, afterRest); // closing delimiter and body, byte for byte
        Assert.Equal(WithoutTags(beforeFrontMatter), WithoutTags(afterFrontMatter));
        Assert.Contains("shopping", afterFrontMatter);
    }

    [Fact]
    public async Task A_note_that_already_has_tags_is_never_touched()
    {
        var note = await CreateAsync("Tagged by hand", "body", "x");
        var before = await RawAsync(_fixture.UserAId, note.Id);
        Model.NextTags = ["other"];

        var result = await TagAAsync();

        Assert.Equal(0, Model.CallCount);
        Assert.Equal(0, result.Tagged);
        Assert.Equal(before, await RawAsync(_fixture.UserAId, note.Id));
    }

    [Fact]
    public async Task The_prompt_only_carries_the_note_owners_vocabulary()
    {
        _fixture.ActAsUserB();
        var bTagged = await CreateAsync("B tagged", "body", "secret-b");
        var bUntagged = await CreateAsync("B untagged");
        var bBefore = await RawAsync(_fixture.UserBId, bUntagged.Id);
        _fixture.ActAsUserA();
        await CreateAsync("A tagged", "body", "alpha");
        var aUntagged = await CreateAsync("A untagged");
        Model.NextTags = ["fresh"];

        var result = await TagAAsync();

        Assert.Equal(1, result.Tagged);
        Assert.Equal(1, Model.CallCount);
        Assert.DoesNotContain("secret-b", Model.LastPrompt!.UserMessage);
        Assert.Contains("alpha", Model.LastPrompt.UserMessage);
        Assert.Equal(["fresh"], (await ReadAsync(_fixture.UserAId, aUntagged.Id)).Tags);
        Assert.Equal(bBefore, await RawAsync(_fixture.UserBId, bUntagged.Id));
        Assert.Equal(["secret-b"], (await ReadAsync(_fixture.UserBId, bTagged.Id)).Tags);
    }

    [Fact]
    public async Task All_users_are_tagged_each_with_their_own_tag_spelling()
    {
        _fixture.ActAsUserA();
        await CreateAsync("A work", "body", "Work");
        var aUntagged = await CreateAsync("A untagged");
        _fixture.ActAsUserB();
        await CreateAsync("B work", "body", "work");
        var bUntagged = await CreateAsync("B untagged");
        Model.NextTags = ["work"];
        _fixture.Clock.Advance(TimeSpan.FromMinutes(1));

        var result = await Tagger.TagAllUsersAsync(10, TimeSpan.Zero);

        Assert.Equal(2, result.UsersProcessed);
        Assert.Equal(2, result.Tagged);
        Assert.Equal(["Work"], (await ReadAsync(_fixture.UserAId, aUntagged.Id)).Tags);
        Assert.Equal(["work"], (await ReadAsync(_fixture.UserBId, bUntagged.Id)).Tags);
    }

    [Fact]
    public async Task An_existing_spelling_from_the_users_vocabulary_is_reused()
    {
        await CreateAsync("Has a tag", "body", "Work");
        var untagged = await CreateAsync("Untagged");
        Model.NextTags = ["work"];

        await TagAAsync();

        Assert.Equal(["Work"], (await ReadAsync(_fixture.UserAId, untagged.Id)).Tags);
    }

    [Fact]
    public async Task A_note_edited_during_the_model_call_is_left_as_edited_and_tagged_next_time()
    {
        var note = await CreateAsync("Being edited");
        Model.NextTags = ["alpha"];
        Model.OnCall = async _ =>
        {
            var edited = await ReadAsync(_fixture.UserAId, note.Id);
            edited.Body = "changed";
            await _fixture.FileStore.WriteAsync(_fixture.UserAId, edited);
        };

        var result = await TagAAsync();

        Assert.Equal(1, result.Deferred);
        Assert.Equal(0, result.Tagged);
        var afterRace = await ReadAsync(_fixture.UserAId, note.Id);
        Assert.Equal("changed", afterRace.Body);
        Assert.Empty(afterRace.Tags);
        Assert.Null(await _fixture.AutoTagState.GetAsync(_fixture.UserAId, note.Id));

        Model.OnCall = null;
        var second = await TagAAsync();

        Assert.Equal(1, second.Tagged);
        var final = await ReadAsync(_fixture.UserAId, note.Id);
        Assert.Equal("changed", final.Body);
        Assert.Equal(["alpha"], final.Tags);
    }

    [Fact]
    public async Task A_bad_response_is_recorded_and_retried_only_after_the_backoff()
    {
        var note = await CreateAsync("Confusing");
        var before = await RawAsync(_fixture.UserAId, note.Id);
        Model.Throw = new AutoTagException("x", AutoTagFailureKind.BadResponse);

        var result = await TagAAsync();

        Assert.Equal(1, result.Failed);
        Assert.Null(result.UnavailableReason);
        Assert.Equal(before, await RawAsync(_fixture.UserAId, note.Id));
        var attempt = await _fixture.AutoTagState.GetAsync(_fixture.UserAId, note.Id);
        Assert.Equal(AutoTagOutcome.Failed, attempt!.Outcome);
        Assert.Equal(1, attempt.FailureCount);
        Assert.InRange(
            (attempt.RetryAfterUtc!.Value - (_fixture.Clock.GetUtcNow() + TimeSpan.FromHours(1))).Duration(),
            TimeSpan.Zero,
            TimeSpan.FromSeconds(10));

        await TagAAsync(); // immediately again: still backing off
        Assert.Equal(1, Model.CallCount);

        _fixture.Clock.Advance(TimeSpan.FromMinutes(61));
        await TagAAsync();
        Assert.Equal(2, Model.CallCount);
        Assert.Equal(2, (await _fixture.AutoTagState.GetAsync(_fixture.UserAId, note.Id))!.FailureCount);
    }

    [Fact]
    public async Task A_note_is_given_up_on_after_five_failures_and_never_retried()
    {
        var note = await CreateAsync("Hopeless");
        Model.Throw = new AutoTagException("x", AutoTagFailureKind.BadResponse);

        for (var i = 0; i < 5; i++)
        {
            _fixture.Clock.Advance(TimeSpan.FromHours(73)); // past the longest backoff
            await TagAAsync();
        }

        Assert.Equal(5, Model.CallCount);
        var attempt = await _fixture.AutoTagState.GetAsync(_fixture.UserAId, note.Id);
        Assert.Equal(AutoTagOutcome.GaveUp, attempt!.Outcome);
        Assert.Equal(5, attempt.FailureCount);
        Assert.Null(attempt.RetryAfterUtc);

        _fixture.Clock.Advance(TimeSpan.FromDays(100));
        await TagAAsync();

        Assert.Equal(5, Model.CallCount);
    }

    [Fact]
    public async Task An_unavailable_service_stops_the_pass_and_records_nothing()
    {
        var notes = new List<Note>
        {
            await CreateAsync("One"),
            await CreateAsync("Two"),
            await CreateAsync("Three"),
        };
        Model.Throw = new AutoTagException("The tagging service couldn't be reached.", AutoTagFailureKind.Unavailable);

        var result = await TagAAsync();

        Assert.Equal(1, Model.CallCount);
        Assert.True(result.ServiceUnavailable);
        Assert.Equal("The tagging service couldn't be reached.", result.UnavailableReason);
        foreach (var note in notes)
        {
            Assert.Null(await _fixture.AutoTagState.GetAsync(_fixture.UserAId, note.Id));
        }

        Model.Throw = null;
        Model.NextTags = ["alpha"];
        var second = await TagAAsync();

        Assert.Equal(3, second.Tagged);
    }

    [Fact]
    public async Task A_timeout_is_charged_to_the_note_and_stops_the_pass()
    {
        var first = await CreateAsync("First");
        var second = await CreateAsync("Second");
        Model.Throw = new AutoTagException("The tagging service timed out.", AutoTagFailureKind.Timeout);

        var result = await TagAAsync();

        Assert.Equal(1, Model.CallCount);
        Assert.True(result.ServiceUnavailable);
        Assert.Equal(1, result.Failed);
        var attempts = new[]
        {
            await _fixture.AutoTagState.GetAsync(_fixture.UserAId, first.Id),
            await _fixture.AutoTagState.GetAsync(_fixture.UserAId, second.Id),
        };
        var recorded = Assert.Single(attempts, a => a is not null);
        Assert.Equal(AutoTagOutcome.Failed, recorded!.Outcome);
    }

    [Fact]
    public async Task A_model_with_nothing_to_add_is_recorded_and_not_asked_again()
    {
        var note = await CreateAsync("Vague");
        var before = await RawAsync(_fixture.UserAId, note.Id);
        Model.NextTags = ["note"]; // a stop-word, normalised away

        var result = await TagAAsync();

        Assert.Equal(1, result.NoTags);
        Assert.Equal(before, await RawAsync(_fixture.UserAId, note.Id));
        Assert.Equal(AutoTagOutcome.NoTags, (await _fixture.AutoTagState.GetAsync(_fixture.UserAId, note.Id))!.Outcome);

        await TagAAsync();

        Assert.Equal(1, Model.CallCount);
    }

    [Fact]
    public async Task Tags_a_user_removes_afterwards_do_not_come_back()
    {
        var note = await CreateAsync("Tag me once");
        Model.NextTags = ["alpha"];
        await TagAAsync();
        var tagged = await _fixture.NoteService.GetAsync(note.Id);
        tagged.Tags = [];
        await _fixture.NoteService.UpdateAsync(tagged);

        await TagAAsync();

        Assert.Equal(1, Model.CallCount);
        Assert.Empty((await ReadAsync(_fixture.UserAId, note.Id)).Tags);
    }

    [Fact]
    public async Task A_recently_changed_note_is_left_alone()
    {
        await CreateAsync("Just written");

        var result = await Tagger.TagUserNotesAsync(_fixture.UserAId, 10, AutoTagSchedule.MinNoteAge);

        Assert.Equal(0, Model.CallCount);
        Assert.Equal(0, result.Tagged);
    }

    [Theory]
    [InlineData("aliases")]
    [InlineData("crlf")]
    public async Task A_file_not_in_the_canonical_format_is_skipped_and_never_rewritten(string variant)
    {
        var note = await CreateAsync("Hand edited");
        var path = Path.Combine(_fixture.NotesFolder, _fixture.UserAId.ToString(), $"{note.Id}.md");
        var text = await File.ReadAllTextAsync(path);
        text = variant == "aliases" ? text.Replace("---\n", "---\naliases: [x]\n", StringComparison.Ordinal) : text.Replace("\n", "\r\n", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, text);
        await _fixture.Reconciler.RescanAsync(_fixture.UserAId);
        var before = await File.ReadAllBytesAsync(path);
        Model.NextTags = ["alpha"];

        var result = await TagAAsync();

        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, Model.CallCount);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Equal(AutoTagOutcome.Skipped, (await _fixture.AutoTagState.GetAsync(_fixture.UserAId, note.Id))!.Outcome);
    }

    [Fact]
    public async Task The_batch_size_caps_the_notes_per_pass()
    {
        await CreateAsync("One");
        await CreateAsync("Two");
        await CreateAsync("Three");
        Model.NextTags = ["alpha"];

        var result = await TagAAsync(maxNotes: 2);

        Assert.Equal(2, Model.CallCount);
        Assert.Equal(2, result.Tagged);
    }

    [Fact]
    public async Task Refuses_to_run_when_auto_tagging_is_disabled()
    {
        var tagger = new NoteAutoTagger(
            _fixture.FileStore,
            _fixture.IndexRepository,
            _fixture.AutoTagState,
            _fixture.UserRepository,
            _fixture.Serializer,
            Model,
            Options.Create(new AutoTagOptions()),
            _fixture.Clock,
            NullLogger<NoteAutoTagger>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tagger.TagAllUsersAsync(10, TimeSpan.Zero));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tagger.TagUserNotesAsync(_fixture.UserAId, 10, TimeSpan.Zero));
    }

    [Fact]
    public async Task Deleting_a_user_removes_their_attempt_rows()
    {
        _fixture.ActAsUserB();
        var note = await CreateAsync("B's note");
        Model.NextTags = ["alpha"];
        _fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await Tagger.TagUserNotesAsync(_fixture.UserBId, 10, TimeSpan.Zero);
        Assert.NotNull(await _fixture.AutoTagState.GetAsync(_fixture.UserBId, note.Id));

        await _fixture.UserRepository.DeleteUserAsync(_fixture.UserBId);

        Assert.Null(await _fixture.AutoTagState.GetAsync(_fixture.UserBId, note.Id));
    }

    // "---\n<front matter>\n" and then everything from the closing "---" on (delimiter and body).
    private static (string FrontMatter, string Tail) SplitAtClosingDelimiter(string raw)
    {
        var close = raw.IndexOf("\n---", 4, StringComparison.Ordinal);
        Assert.True(close > 0, "closing front-matter delimiter not found");
        return (raw[..(close + 1)], raw[(close + 1)..]);
    }

    // Drops the "tags:" key and its "- item" lines (the only front-matter key whose value spans lines).
    private static string[] WithoutTags(string frontMatter)
    {
        var kept = new List<string>();
        var inTags = false;
        foreach (var line in frontMatter.Split('\n'))
        {
            if (line.StartsWith("tags:", StringComparison.Ordinal))
            {
                inTags = true;
                continue;
            }

            if (inTags && line.StartsWith("- ", StringComparison.Ordinal))
            {
                continue;
            }

            inTags = false;
            kept.Add(line);
        }

        return kept.ToArray();
    }
}
