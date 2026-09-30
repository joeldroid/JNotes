using JNotes.Core.Images;
using JNotes.Core.Models;
using JNotes.Core.Notes;
using JNotes.Core.Sharing;
using JNotes.Core.Storage;
using JNotes.Tests.Images;
using JNotes.Tests.TestDoubles;
using Microsoft.Extensions.DependencyInjection;

namespace JNotes.Tests.Integration;

/// <summary>
/// Per-note sharing, end to end through <see cref="INoteService"/>/<see cref="INoteSharingService"/> against real
/// PostgreSQL and a temp notes folder. User A owns the notes, B is the usual recipient, C a second one.
/// </summary>
public class NoteSharingTests : IAsyncLifetime
{
    private const string StoredImageName = "0123456789abcdef0123456789abcdef.png";

    private CoreServicesFixture _fixture = null!;

    public async Task InitializeAsync() => _fixture = await CoreServicesFixture.CreateAsync();

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private INoteService Notes => _fixture.NoteService;

    private INoteSharingService Sharing => _fixture.NoteSharing;

    private IImageService Images => _fixture.Scope.ServiceProvider.GetRequiredService<IImageService>();

    private IImageCleanupService Cleanup => _fixture.Scope.ServiceProvider.GetRequiredService<IImageCleanupService>();

    private string NotePath(Guid ownerId, Guid noteId) => Path.Combine(_fixture.NotesFolder, ownerId.ToString(), $"{noteId}.md");

    private string ImagePath(Guid ownerId, string fileName) => Path.Combine(_fixture.NotesFolder, ownerId.ToString(), "images", fileName);

    private static MemoryStream PngStream(int bytes = 64)
    {
        var data = new byte[bytes];
        ImageFormatTests.Png.CopyTo(data, 0);
        return new MemoryStream(data);
    }

    /// <summary>The MIME type of the opened image (disposing it), or <c>null</c> when <c>OpenImageAsync</c> returns nothing.</summary>
    private async Task<string?> OpenContentTypeAsync(Guid noteId, string fileName)
    {
        var image = await Notes.OpenImageAsync(noteId, fileName);
        if (image is null)
        {
            return null;
        }

        await image.Stream.DisposeAsync();
        return image.ContentType;
    }

    /// <summary>As A, creates a note and shares it with B at <paramref name="permission"/>. Leaves the fixture acting as B.</summary>
    private async Task<Note> CreateSharedWithBAsync(
        NoteSharePermission permission,
        NoteType type = NoteType.Text,
        string title = "Shared note",
        string[]? tags = null,
        string body = "original body")
    {
        _fixture.ActAsUserA();
        var note = await Notes.CreateAsync(type, title, tags ?? ["alpha"], body);
        await Sharing.ShareAsync(note.Id, _fixture.UserBId, permission);
        _fixture.ActAsUserB();
        return note;
    }

    [Fact]
    public async Task An_unshared_foreign_note_is_not_found_and_nothing_is_created_for_the_caller()
    {
        _fixture.ActAsUserA();
        var note = await Notes.CreateAsync(NoteType.Text, "Private", [], "secret");
        var before = await File.ReadAllBytesAsync(NotePath(_fixture.UserAId, note.Id));

        _fixture.ActAsUserB();
        var attempt = new Note
        {
            Id = note.Id,
            Title = "Hijack",
            Type = NoteType.Text,
            CreatedAt = note.CreatedAt,
            UpdatedAt = note.UpdatedAt,
            Body = "overwritten",
        };

        await Assert.ThrowsAsync<NoteNotFoundException>(() => Notes.GetAsync(note.Id));
        await Assert.ThrowsAsync<NoteNotFoundException>(() => Notes.GetWithAccessAsync(note.Id));
        await Assert.ThrowsAsync<NoteNotFoundException>(() => Notes.OpenImageAsync(note.Id, StoredImageName));
        await Assert.ThrowsAsync<NoteNotFoundException>(() => Notes.AutosaveAsync(attempt, NoteSaveScope.ContentOnly));
        await Assert.ThrowsAsync<NoteNotFoundException>(() => Notes.AutosaveAsync(attempt, NoteSaveScope.Metadata));

        Assert.False(File.Exists(NotePath(_fixture.UserBId, note.Id)));
        Assert.Equal(before, await File.ReadAllBytesAsync(NotePath(_fixture.UserAId, note.Id)));
    }

    [Fact]
    public async Task A_read_share_resolves_to_Read_access_with_the_owner_and_their_display_name()
    {
        var note = await CreateSharedWithBAsync(NoteSharePermission.Read);

        var result = await Notes.GetWithAccessAsync(note.Id);

        Assert.Equal(NoteAccessLevel.Read, result.Access.Level);
        Assert.False(result.Access.IsOwner);
        Assert.False(result.Access.CanEditBody);
        Assert.Equal(_fixture.UserAId, result.Access.OwnerUserId);
        Assert.Equal("user-a", result.Access.OwnerDisplayName);
        Assert.Equal("Shared note", result.Note.Title);
        Assert.Equal("original body", (await Notes.GetAsync(note.Id)).Body);
    }

    [Fact]
    public async Task The_owner_resolves_to_Owner_access_with_no_display_name()
    {
        _fixture.ActAsUserA();
        var note = await Notes.CreateAsync(NoteType.Text, "Mine", [], "x");

        var result = await Notes.GetWithAccessAsync(note.Id);

        Assert.True(result.Access.IsOwner);
        Assert.True(result.Access.CanEditBody);
        Assert.Equal(_fixture.UserAId, result.Access.OwnerUserId);
        Assert.Null(result.Access.OwnerDisplayName);
    }

    [Fact]
    public async Task A_read_recipient_cannot_write_and_the_owners_file_is_unchanged()
    {
        var note = await CreateSharedWithBAsync(NoteSharePermission.Read, NoteType.Checklist, body: "- [ ] milk\n- [ ] eggs");
        var before = await File.ReadAllBytesAsync(NotePath(_fixture.UserAId, note.Id));
        var mine = await Notes.GetAsync(note.Id);
        mine.Body = "- [ ] hacked";

        await Assert.ThrowsAsync<NoteAccessDeniedException>(() => Notes.AutosaveAsync(mine, NoteSaveScope.ContentOnly));
        await Assert.ThrowsAsync<NoteAccessDeniedException>(() => Notes.AutosaveAsync(mine, NoteSaveScope.Metadata));
        await Assert.ThrowsAsync<NoteAccessDeniedException>(() => Notes.UpdateAsync(mine));
        await Assert.ThrowsAsync<NoteAccessDeniedException>(() => Notes.SetChecklistItemCheckedAsync(note.Id, 0, true));
        await Assert.ThrowsAsync<NoteAccessDeniedException>(() => Notes.AddChecklistItemAsync(note.Id, "bread"));
        await Assert.ThrowsAsync<NoteAccessDeniedException>(() => Notes.AddChecklistCategoryAsync(note.Id, "Dairy"));
        await Assert.ThrowsAsync<NoteAccessDeniedException>(() => Notes.SaveImageAsync(note.Id, PngStream()));

        Assert.Equal(before, await File.ReadAllBytesAsync(NotePath(_fixture.UserAId, note.Id)));
        Assert.False(File.Exists(NotePath(_fixture.UserBId, note.Id)));
        Assert.False(Directory.Exists(Path.Combine(_fixture.NotesFolder, _fixture.UserAId.ToString(), "images")));
    }

    [Fact]
    public async Task An_edit_recipients_content_save_writes_the_owners_file_and_leaves_the_index_alone()
    {
        var note = await CreateSharedWithBAsync(NoteSharePermission.Edit, body: "original body");
        _fixture.ActAsUserA();
        var indexedBefore = Assert.Single(await Notes.ListAsync(new NoteSearchQuery()));
        _fixture.ActAsUserB();
        await Task.Delay(20);

        var mine = await Notes.GetAsync(note.Id);
        mine.Body = "edited by bob";
        await Notes.AutosaveAsync(mine, NoteSaveScope.ContentOnly);

        Assert.Equal("edited by bob", (await _fixture.FileStore.ReadAsync(_fixture.UserAId, note.Id)).Body);
        Assert.False(File.Exists(NotePath(_fixture.UserBId, note.Id)));

        // ContentOnly never touches the index: A's row still has the old UpdatedAt and the old body.
        _fixture.ActAsUserA();
        var indexedAfter = Assert.Single(await Notes.ListAsync(new NoteSearchQuery()));
        Assert.Equal(indexedBefore.UpdatedAt, indexedAfter.UpdatedAt);
        Assert.Empty(await Notes.ListAsync(new NoteSearchQuery("edited by bob")));
        Assert.Single(await Notes.ListAsync(new NoteSearchQuery("original body")));
    }

    [Fact]
    public async Task An_edit_recipients_metadata_save_keeps_the_owners_title_and_tags_but_takes_the_body_and_reindexes()
    {
        var note = await CreateSharedWithBAsync(NoteSharePermission.Edit, title: "Original", tags: ["alpha"], body: "original body");

        var mine = await Notes.GetAsync(note.Id);
        mine.Title = "Bob's title";
        mine.Tags = ["bobtag"];
        mine.Body = "quokka sighting";
        var returned = await Notes.AutosaveAsync(mine, NoteSaveScope.Metadata);

        var onDisk = await _fixture.FileStore.ReadAsync(_fixture.UserAId, note.Id);
        Assert.Equal("Original", onDisk.Title);
        Assert.Equal(["alpha"], onDisk.Tags);
        Assert.Equal("quokka sighting", onDisk.Body);
        Assert.Equal("Original", returned.Title);
        Assert.Equal(["alpha"], returned.Tags);

        _fixture.ActAsUserA();
        var found = Assert.Single(await Notes.ListAsync(new NoteSearchQuery("quokka")));
        Assert.Equal(note.Id, found.Id);
        Assert.Equal("Original", found.Title);

        _fixture.ActAsUserB();
        Assert.Empty(await Notes.SuggestTagsAsync(null, []));
        Assert.Empty(await Notes.GetDistinctTagsAsync());
    }

    [Fact]
    public async Task An_edit_recipient_cannot_change_the_note_type()
    {
        var note = await CreateSharedWithBAsync(NoteSharePermission.Edit);
        var before = await File.ReadAllBytesAsync(NotePath(_fixture.UserAId, note.Id));
        var mine = await Notes.GetAsync(note.Id);
        mine.Type = NoteType.Checklist;

        await Assert.ThrowsAsync<NoteAccessDeniedException>(() => Notes.AutosaveAsync(mine, NoteSaveScope.ContentOnly));

        Assert.Equal(before, await File.ReadAllBytesAsync(NotePath(_fixture.UserAId, note.Id)));
    }

    [Fact]
    public async Task An_edit_recipient_can_toggle_and_add_checklist_items_and_checked_items_sink_in_the_owners_file()
    {
        var note = await CreateSharedWithBAsync(NoteSharePermission.Edit, NoteType.Checklist, body: "- [ ] milk\n- [ ] eggs");

        await Notes.SetChecklistItemCheckedAsync(note.Id, lineIndex: 0, isChecked: true);
        await Notes.AddChecklistItemAsync(note.Id, "bread");

        var body = (await _fixture.FileStore.ReadAsync(_fixture.UserAId, note.Id)).Body;
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(["- [ ] eggs", "- [ ] bread", "- [x] milk"], lines);
        Assert.False(File.Exists(NotePath(_fixture.UserBId, note.Id)));
    }

    [Fact]
    public async Task A_recipient_trashing_restoring_or_purging_someone_elses_note_changes_nothing()
    {
        var note = await CreateSharedWithBAsync(NoteSharePermission.Edit);

        await Notes.TrashAsync(note.Id);
        await Assert.ThrowsAsync<NoteNotFoundException>(() => Notes.RestoreAsync(note.Id));
        await Notes.PurgeAsync(note.Id);

        Assert.True(File.Exists(NotePath(_fixture.UserAId, note.Id)));
        Assert.False(File.Exists(Path.Combine(_fixture.NotesFolder, _fixture.UserAId.ToString(), "trash", $"{note.Id}.md")));
        Assert.Single(await _fixture.ShareRepository.ListForNoteAsync(_fixture.UserAId, note.Id));
        _fixture.ActAsUserA();
        Assert.Single(await Notes.ListAsync(new NoteSearchQuery()));
    }

    [Fact]
    public async Task A_recipient_cannot_manage_shares()
    {
        var note = await CreateSharedWithBAsync(NoteSharePermission.Edit);
        var carol = await _fixture.CreateUserAsync("carol");

        await Assert.ThrowsAsync<NoteAccessDeniedException>(() => Sharing.ShareAsync(note.Id, carol, NoteSharePermission.Read));
        await Assert.ThrowsAsync<NoteAccessDeniedException>(() => Sharing.UnshareAsync(note.Id, _fixture.UserBId));
        await Assert.ThrowsAsync<NoteAccessDeniedException>(() => Sharing.ListSharesAsync(note.Id));
        await Assert.ThrowsAsync<NoteAccessDeniedException>(() => Sharing.SearchRecipientsAsync(note.Id, "carol"));

        // Someone with no access at all isn't even told the note exists.
        _fixture.ActAs(carol);
        await Assert.ThrowsAsync<NoteNotFoundException>(() => Sharing.ListSharesAsync(note.Id));
        await Assert.ThrowsAsync<NoteNotFoundException>(() => Sharing.ShareAsync(note.Id, carol, NoteSharePermission.Read));

        Assert.Single(await _fixture.ShareRepository.ListForNoteAsync(_fixture.UserAId, note.Id));
    }

    [Fact]
    public async Task Sharing_with_yourself_or_an_unknown_user_is_refused()
    {
        _fixture.ActAsUserA();
        var note = await Notes.CreateAsync(NoteType.Text, "Mine", [], "x");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Sharing.ShareAsync(note.Id, _fixture.UserAId, NoteSharePermission.Read));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sharing.ShareAsync(note.Id, Guid.NewGuid(), NoteSharePermission.Read));

        Assert.Empty(await Sharing.ListSharesAsync(note.Id));
    }

    [Fact]
    public async Task Sharing_twice_changes_the_permission_and_leaves_one_row()
    {
        var note = await CreateSharedWithBAsync(NoteSharePermission.Read);

        _fixture.ActAsUserA();
        await Sharing.ShareAsync(note.Id, _fixture.UserBId, NoteSharePermission.Edit);

        var shares = await Sharing.ListSharesAsync(note.Id);
        var entry = Assert.Single(shares);
        Assert.Equal(_fixture.UserBId, entry.UserId);
        Assert.Equal("user-b", entry.Username);
        Assert.Equal("user-b", entry.DisplayName);
        Assert.Equal(NoteSharePermission.Edit, entry.Permission);

        _fixture.ActAsUserB();
        Assert.Equal(NoteAccessLevel.Edit, (await Notes.GetWithAccessAsync(note.Id)).Access.Level);
    }

    [Fact]
    public async Task Unsharing_takes_effect_on_the_recipients_next_call()
    {
        var note = await CreateSharedWithBAsync(NoteSharePermission.Edit);
        var mine = await Notes.GetAsync(note.Id);
        mine.Body = "still here?";

        _fixture.ActAsUserA();
        await Sharing.UnshareAsync(note.Id, _fixture.UserBId);

        _fixture.ActAsUserB();
        await Assert.ThrowsAsync<NoteNotFoundException>(() => Notes.GetAsync(note.Id));
        await Assert.ThrowsAsync<NoteNotFoundException>(() => Notes.AutosaveAsync(mine, NoteSaveScope.ContentOnly));
        Assert.False(File.Exists(NotePath(_fixture.UserBId, note.Id)));
        Assert.Equal("original body", (await _fixture.FileStore.ReadAsync(_fixture.UserAId, note.Id)).Body);
    }

    [Fact]
    public async Task Leaving_removes_only_the_leavers_share()
    {
        var note = await CreateSharedWithBAsync(NoteSharePermission.Read);
        var carol = await _fixture.CreateUserAsync("carol");
        _fixture.ActAsUserA();
        await Sharing.ShareAsync(note.Id, carol, NoteSharePermission.Read);

        _fixture.ActAsUserB();
        await Sharing.LeaveAsync(note.Id);
        await Sharing.LeaveAsync(note.Id); // idempotent

        await Assert.ThrowsAsync<NoteNotFoundException>(() => Notes.GetAsync(note.Id));
        _fixture.ActAs(carol);
        Assert.Equal("Shared note", (await Notes.GetAsync(note.Id)).Title);
        _fixture.ActAsUserA();
        Assert.Equal(carol, Assert.Single(await Sharing.ListSharesAsync(note.Id)).UserId);
        Assert.Equal(note.Id, Assert.Single(await Notes.ListAsync(new NoteSearchQuery())).Id);
    }

    [Fact]
    public async Task Trashing_hides_the_note_from_recipients_and_restoring_brings_it_back_and_purging_removes_the_shares()
    {
        var note = await CreateSharedWithBAsync(NoteSharePermission.Edit);
        var mine = await Notes.GetAsync(note.Id);
        mine.Body = "edit after trash";

        _fixture.ActAsUserA();
        await Notes.TrashAsync(note.Id);

        _fixture.ActAsUserB();
        Assert.Empty(await Notes.ListAsync(new NoteSearchQuery()));
        await Assert.ThrowsAsync<NoteNotFoundException>(() => Notes.GetAsync(note.Id));
        await Assert.ThrowsAsync<NoteNotFoundException>(() => Notes.AutosaveAsync(mine, NoteSaveScope.ContentOnly));
        await Assert.ThrowsAsync<NoteNotFoundException>(() => Notes.AutosaveAsync(mine, NoteSaveScope.Metadata));
        Assert.False(File.Exists(NotePath(_fixture.UserAId, note.Id)));
        Assert.False(File.Exists(NotePath(_fixture.UserBId, note.Id)));

        _fixture.ActAsUserA();
        await Notes.RestoreAsync(note.Id);

        _fixture.ActAsUserB();
        Assert.Equal(NoteAccessLevel.Edit, (await Notes.GetWithAccessAsync(note.Id)).Access.Level);
        Assert.Equal(note.Id, Assert.Single(await Notes.ListAsync(new NoteSearchQuery())).Id);

        _fixture.ActAsUserA();
        await Notes.TrashAsync(note.Id);
        Assert.Single(await _fixture.ShareRepository.ListForNoteAsync(_fixture.UserAId, note.Id));
        await Notes.PurgeAsync(note.Id);
        Assert.Empty(await _fixture.ShareRepository.ListForNoteAsync(_fixture.UserAId, note.Id));
    }

    [Theory]
    [InlineData(NoteSharePermission.Read)]
    [InlineData(NoteSharePermission.Edit)]
    public async Task A_note_the_owner_trashed_is_not_in_the_recipients_trash_and_the_recipient_cannot_restore_it(NoteSharePermission permission)
    {
        var note = await CreateSharedWithBAsync(permission);

        _fixture.ActAsUserA();
        await Notes.TrashAsync(note.Id);

        _fixture.ActAsUserB();
        Assert.Empty(await Notes.ListTrashAsync());
        await Assert.ThrowsAsync<NoteNotFoundException>(() => Notes.RestoreAsync(note.Id));
        Assert.Empty(await Notes.ListAsync(new NoteSearchQuery()));
        Assert.Empty(await Notes.ListTrashAsync());

        // Still in the owner's trash, untouched by the recipient's attempt.
        _fixture.ActAsUserA();
        Assert.Equal(note.Id, Assert.Single(await Notes.ListTrashAsync()).Id);
    }

    [Fact]
    public async Task Listing_as_a_recipient_merges_own_and_shared_notes_newest_first_with_share_info()
    {
        var shared = await CreateSharedWithBAsync(NoteSharePermission.Read, title: "From A");
        await Task.Delay(20);
        var own = await Notes.CreateAsync(NoteType.Text, "From B", [], "b body");

        var listed = await Notes.ListAsync(new NoteSearchQuery());

        Assert.Equal([own.Id, shared.Id], listed.Select(n => n.Id));
        var ownRow = listed[0];
        Assert.Null(ownRow.SharedWithMe);
        Assert.Equal(_fixture.UserBId, ownRow.OwnerUserId);
        Assert.Equal(0, ownRow.OutgoingShareCount);
        var sharedRow = listed[1];
        Assert.Equal(_fixture.UserAId, sharedRow.OwnerUserId);
        Assert.Equal(new SharedNoteInfo(_fixture.UserAId, "user-a", NoteSharePermission.Read), sharedRow.SharedWithMe);
        Assert.Equal(0, sharedRow.OutgoingShareCount);

        // A bumps their note: it now sorts ahead of B's, across owners.
        await Task.Delay(20);
        _fixture.ActAsUserA();
        var bumped = await Notes.GetAsync(shared.Id);
        await Notes.UpdateAsync(bumped);
        _fixture.ActAsUserB();
        Assert.Equal([shared.Id, own.Id], (await Notes.ListAsync(new NoteSearchQuery())).Select(n => n.Id));
    }

    [Fact]
    public async Task Listing_as_the_owner_shows_the_outgoing_share_count_and_no_incoming_info()
    {
        var note = await CreateSharedWithBAsync(NoteSharePermission.Read);
        var carol = await _fixture.CreateUserAsync("carol");
        _fixture.ActAsUserA();
        await Sharing.ShareAsync(note.Id, carol, NoteSharePermission.Edit);

        var row = Assert.Single(await Notes.ListAsync(new NoteSearchQuery()));

        Assert.Equal(2, row.OutgoingShareCount);
        Assert.Null(row.SharedWithMe);
        Assert.Equal(_fixture.UserAId, row.OwnerUserId);
    }

    [Fact]
    public async Task The_SharedWithMe_scope_returns_only_incoming_notes()
    {
        var shared = await CreateSharedWithBAsync(NoteSharePermission.Read);
        await Notes.CreateAsync(NoteType.Text, "B's own", [], "x");
        _fixture.ActAsUserA();
        await Notes.CreateAsync(NoteType.Text, "A's unshared", [], "x");

        _fixture.ActAsUserB();
        var incoming = await Notes.ListAsync(new NoteSearchQuery(Scope: NoteListScope.SharedWithMe));
        Assert.Equal(shared.Id, Assert.Single(incoming).Id);
        Assert.Equal(2, (await Notes.ListAsync(new NoteSearchQuery())).Count);

        // The owner has nothing "shared with" them.
        _fixture.ActAsUserA();
        Assert.Empty(await Notes.ListAsync(new NoteSearchQuery(Scope: NoteListScope.SharedWithMe)));
    }

    [Fact]
    public async Task Search_and_filters_cover_shared_notes()
    {
        var note = await CreateSharedWithBAsync(NoteSharePermission.Read, title: "Garden plan", tags: ["alpha"], body: "plant tulips");
        _fixture.ActAsUserA();
        var updated = await Notes.GetAsync(note.Id);
        updated.Body = "plant quokkas";
        await Notes.UpdateAsync(updated); // Metadata save reindexes the body

        _fixture.ActAsUserB();
        Assert.Equal(note.Id, Assert.Single(await Notes.ListAsync(new NoteSearchQuery("garden"))).Id);
        Assert.Equal(note.Id, Assert.Single(await Notes.ListAsync(new NoteSearchQuery("quokkas"))).Id);
        Assert.Equal(note.Id, Assert.Single(await Notes.ListAsync(new NoteSearchQuery("alph"))).Id);
        Assert.Equal(note.Id, Assert.Single(await Notes.ListAsync(new NoteSearchQuery(Tag: "alpha"))).Id);
        Assert.Empty(await Notes.ListAsync(new NoteSearchQuery(Tag: "other")));
        Assert.Empty(await Notes.ListAsync(new NoteSearchQuery("no such text")));
    }

    [Fact]
    public async Task Deleting_a_recipient_removes_their_shares_and_lowers_the_owners_count()
    {
        var note = await CreateSharedWithBAsync(NoteSharePermission.Read);
        var carol = await _fixture.CreateUserAsync("carol");
        _fixture.ActAsUserA();
        await Sharing.ShareAsync(note.Id, carol, NoteSharePermission.Read);
        Assert.Equal(2, Assert.Single(await Notes.ListAsync(new NoteSearchQuery())).OutgoingShareCount);

        await _fixture.UserManagement.DeleteUserAsync(_fixture.UserBId, _fixture.UserAId);

        Assert.Equal(carol, Assert.Single(await _fixture.ShareRepository.ListForNoteAsync(_fixture.UserAId, note.Id)).UserId);
        Assert.Equal(1, Assert.Single(await Notes.ListAsync(new NoteSearchQuery())).OutgoingShareCount);
        Assert.Null(await _fixture.ShareRepository.FindAsync(note.Id, _fixture.UserBId));
    }

    [Fact]
    public async Task Deleting_an_owner_removes_their_shares_and_their_note_disappears_for_recipients()
    {
        var dave = await _fixture.CreateUserAsync("dave");
        _fixture.ActAs(dave);
        var note = await Notes.CreateAsync(NoteType.Text, "Dave's note", [], "x");
        await Sharing.ShareAsync(note.Id, _fixture.UserBId, NoteSharePermission.Read);
        _fixture.ActAsUserB();
        Assert.Single(await Notes.ListAsync(new NoteSearchQuery()));

        await _fixture.UserManagement.DeleteUserAsync(dave, _fixture.UserAId);

        Assert.Empty(await Notes.ListAsync(new NoteSearchQuery()));
        Assert.Null(await _fixture.ShareRepository.FindAsync(note.Id, _fixture.UserBId));
        await Assert.ThrowsAsync<NoteNotFoundException>(() => Notes.GetAsync(note.Id));
    }

    [Fact]
    public async Task Opening_images_follows_the_access_level_and_the_notes_text()
    {
        _fixture.ActAsUserA();
        var mentioned = await Images.SaveAsync(PngStream());
        var unmentioned = await Images.SaveAsync(PngStream());
        var readNote = await Notes.CreateAsync(NoteType.Text, "Read", [], $"![a](images/{mentioned})");
        var editNote = await Notes.CreateAsync(NoteType.Text, "Edit", [], "no images written yet");
        await Sharing.ShareAsync(readNote.Id, _fixture.UserBId, NoteSharePermission.Read);
        await Sharing.ShareAsync(editNote.Id, _fixture.UserBId, NoteSharePermission.Edit);

        // Owner: any image in their folder.
        Assert.Equal("image/png", await OpenContentTypeAsync(readNote.Id, unmentioned));

        _fixture.ActAsUserB();

        // Read: only what the note mentions.
        Assert.Equal("image/png", await OpenContentTypeAsync(readNote.Id, mentioned));
        Assert.Null(await OpenContentTypeAsync(readNote.Id, unmentioned));

        // Edit: skips the mention check.
        Assert.Equal("image/png", await OpenContentTypeAsync(editNote.Id, unmentioned));

        // Invalid names and missing files.
        Assert.Null(await OpenContentTypeAsync(readNote.Id, "../secret.png"));
        Assert.Null(await OpenContentTypeAsync(editNote.Id, StoredImageName));

        // No access at all.
        var carol = await _fixture.CreateUserAsync("carol");
        _fixture.ActAs(carol);
        await Assert.ThrowsAsync<NoteNotFoundException>(() => Notes.OpenImageAsync(readNote.Id, mentioned));
    }

    [Fact]
    public async Task An_edit_recipients_pasted_image_lives_in_the_owners_folder_and_survives_the_owners_cleanup()
    {
        var note = await CreateSharedWithBAsync(NoteSharePermission.Edit);

        var stored = await Notes.SaveImageAsync(note.Id, PngStream());

        Assert.True(File.Exists(ImagePath(_fixture.UserAId, stored)));
        Assert.False(File.Exists(ImagePath(_fixture.UserBId, stored)));

        var mine = await Notes.GetAsync(note.Id);
        mine.Body = $"see ![shot](images/{stored})";
        await Notes.AutosaveAsync(mine, NoteSaveScope.ContentOnly);

        var ownerResult = await Cleanup.CleanUpUserAsync(_fixture.UserAId, TimeSpan.Zero);
        var recipientResult = await Cleanup.CleanUpUserAsync(_fixture.UserBId, TimeSpan.Zero);

        Assert.Equal(0, ownerResult.Deleted);
        Assert.Equal(0, recipientResult.Deleted);
        Assert.True(File.Exists(ImagePath(_fixture.UserAId, stored)));
    }

    [Fact]
    public async Task A_read_recipient_can_export_a_pdf_that_includes_the_owners_image()
    {
        _fixture.ActAsUserA();
        var stored = await Images.SaveAsync(PngStream(64));
        var note = await Notes.CreateAsync(NoteType.Text, "With picture", [], $"![a](images/{stored})");
        await Sharing.ShareAsync(note.Id, _fixture.UserBId, NoteSharePermission.Read);

        _fixture.ActAsUserB();
        var pdf = await _fixture.NoteExportService.ExportPdfAsync(note.Id);

        Assert.Equal(RecordingPdfRenderer.Output, pdf.Content);
        Assert.Equal(1, _fixture.PdfRenderer.CallCount);
        Assert.Equal(64, _fixture.PdfRenderer.AssetBytes[stored].Length);
    }

    [Fact]
    public async Task Exporting_an_unshared_note_is_still_not_found()
    {
        _fixture.ActAsUserA();
        var note = await Notes.CreateAsync(NoteType.Text, "Private", [], "secret");

        _fixture.ActAsUserB();

        await Assert.ThrowsAsync<NoteNotFoundException>(() => _fixture.NoteExportService.ExportPdfAsync(note.Id));
        Assert.Equal(0, _fixture.PdfRenderer.CallCount);
    }

    [Fact]
    public async Task An_owner_autosave_after_trashing_the_note_is_not_found_and_does_not_recreate_a_live_file()
    {
        _fixture.ActAsUserA();
        var note = await Notes.CreateAsync(NoteType.Text, "Doomed", [], "body");
        await Notes.TrashAsync(note.Id);
        note.Body = "typed in a stale tab";

        await Assert.ThrowsAsync<NoteNotFoundException>(() => Notes.AutosaveAsync(note, NoteSaveScope.ContentOnly));
        await Assert.ThrowsAsync<NoteNotFoundException>(() => Notes.AutosaveAsync(note, NoteSaveScope.Metadata));

        Assert.False(File.Exists(NotePath(_fixture.UserAId, note.Id)));
        Assert.Empty(await Notes.ListAsync(new NoteSearchQuery()));
    }

    [Fact]
    public async Task An_edit_recipients_save_fails_without_writing_when_the_file_is_locked()
    {
        var note = await CreateSharedWithBAsync(NoteSharePermission.Edit);
        var path = NotePath(_fixture.UserAId, note.Id);
        var before = await File.ReadAllTextAsync(path);
        var mine = await Notes.GetAsync(note.Id);
        mine.Body = "edited while locked";

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => Notes.AutosaveAsync(mine, NoteSaveScope.ContentOnly));
        }

        Assert.Equal(before, await File.ReadAllTextAsync(path));
    }
}
