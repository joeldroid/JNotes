using JNotes.Core.Models;
using JNotes.Core.Notes;
using JNotes.Core.Tagging;
using Microsoft.Extensions.Options;

namespace JNotes.Tests.Integration;

public class NoteAutoTagServiceTests : IAsyncLifetime
{
    private CoreServicesFixture _fixture = null!;

    public async Task InitializeAsync() => _fixture = await CoreServicesFixture.CreateAsync();

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private Task<IReadOnlyList<string>> GenerateAsync(string title = "T", string body = "B", params string[] current) =>
        _fixture.NoteAutoTagService.GenerateTagsAsync(NoteType.Text, title, body, current);

    [Fact]
    public async Task Returns_normalised_tags()
    {
        _fixture.TagModel.NextTags = ["Machine Learning", "#AI", "note"];

        Assert.Equal(["machine-learning", "ai"], await GenerateAsync());
    }

    [Fact]
    public async Task Excludes_tags_already_on_the_note()
    {
        _fixture.TagModel.NextTags = ["work", "ai"];

        Assert.Equal(["ai"], await GenerateAsync(current: "Work"));
    }

    [Fact]
    public async Task Maps_to_the_users_existing_spelling()
    {
        await _fixture.NoteService.CreateAsync(NoteType.Text, "existing", ["Work"], "body");
        _fixture.TagModel.NextTags = ["work"];

        Assert.Equal(["Work"], await GenerateAsync());
    }

    [Fact]
    public async Task The_vocabulary_is_scoped_to_the_current_user()
    {
        _fixture.ActAsUserB();
        await _fixture.NoteService.CreateAsync(NoteType.Text, "b-note", ["secret-b"], "body");
        await _fixture.NoteService.CreateAsync(NoteType.Text, "b-note-2", ["secret-b"], "body");

        _fixture.ActAsUserA();
        await _fixture.NoteService.CreateAsync(NoteType.Text, "a-note", ["mine-a"], "body");
        _fixture.TagModel.NextTags = ["x"];

        await GenerateAsync();

        Assert.Contains("mine-a", _fixture.TagModel.LastPrompt!.UserMessage);
        Assert.DoesNotContain("secret-b", _fixture.TagModel.LastPrompt.UserMessage);
    }

    [Fact]
    public async Task A_blank_title_and_body_returns_nothing_without_calling_the_model()
    {
        var result = await GenerateAsync(title: " ", body: "");

        Assert.Empty(result);
        Assert.Equal(0, _fixture.TagModel.CallCount);
    }

    [Fact]
    public async Task Writes_nothing()
    {
        var note = await _fixture.NoteService.CreateAsync(NoteType.Text, "Untouched", ["keep"], "body");
        var before = await _fixture.NoteService.GetAsync(note.Id);
        _fixture.TagModel.NextTags = ["new-tag"];

        var suggested = await _fixture.NoteAutoTagService.GenerateTagsAsync(before.Type, before.Title, before.Body, before.Tags);

        Assert.Equal(["new-tag"], suggested);
        var after = await _fixture.NoteService.GetAsync(note.Id);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(["keep"], after.Tags);
        var listed = Assert.Single(await _fixture.NoteService.ListAsync(new NoteSearchQuery()));
        Assert.Equal(["keep"], listed.Tags);
    }

    [Fact]
    public async Task An_AutoTagException_from_the_model_propagates()
    {
        _fixture.TagModel.Throw = new AutoTagException("boom");

        var ex = await Assert.ThrowsAsync<AutoTagException>(() => GenerateAsync());

        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task When_disabled_it_throws_InvalidOperationException()
    {
        var service = new NoteAutoTagService(_fixture.NoteService, _fixture.TagModel, Options.Create(new AutoTagOptions()));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateTagsAsync(NoteType.Text, "T", "B", []));
    }
}
