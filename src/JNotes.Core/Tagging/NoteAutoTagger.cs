using JNotes.Core.Indexing;
using JNotes.Core.Models;
using JNotes.Core.Serialization;
using JNotes.Core.Storage;
using JNotes.Core.Users;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JNotes.Core.Tagging;

/// <inheritdoc cref="INoteAutoTagger"/>
/// <remarks>
/// Deliberately bypasses <c>NoteService</c> (implicit current user, and its autosave stamps <c>UpdatedAt</c>).
/// Writes follow the usual file-first order — file, then index (with the post-write mtime, so the reconciler
/// doesn't re-parse the note), then the attempt row. The file is the authority: the index only picks
/// candidates, and every note is re-read raw and checked before the model sees it. A note is rewritten only
/// if it is in the canonical format JNotes itself writes (so only the <c>tags:</c> part changes) and only if
/// its text is unchanged since that read (<see cref="INoteFileStore.ReplaceIfUnchangedAsync"/>). Never logs note
/// content, titles, tags or the Ollama URL.
/// </remarks>
public sealed class NoteAutoTagger : INoteAutoTagger
{
    private readonly INoteFileStore _fileStore;
    private readonly INoteIndexRepository _indexRepository;
    private readonly IAutoTagStateRepository _state;
    private readonly IUserRepository _userRepository;
    private readonly INoteMarkdownSerializer _serializer;
    private readonly ITagModelClient _model;
    private readonly AutoTagOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<NoteAutoTagger> _logger;

    public NoteAutoTagger(
        INoteFileStore fileStore,
        INoteIndexRepository indexRepository,
        IAutoTagStateRepository state,
        IUserRepository userRepository,
        INoteMarkdownSerializer serializer,
        ITagModelClient model,
        IOptions<AutoTagOptions> options,
        TimeProvider time,
        ILogger<NoteAutoTagger> logger)
    {
        _fileStore = fileStore;
        _indexRepository = indexRepository;
        _state = state;
        _userRepository = userRepository;
        _serializer = serializer;
        _model = model;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public async Task<AutoTagPassResult> TagAllUsersAsync(int maxNotes, TimeSpan minAge, CancellationToken cancellationToken = default)
    {
        EnsureEnabled();

        var users = (await _userRepository.GetAllUsersAsync(cancellationToken)).ToArray();
        Random.Shared.Shuffle(users); // fairness: no user is always first in line for the batch

        return await RunAsync(users.Select(u => u.Id).ToList(), maxNotes, minAge, cancellationToken);
    }

    public Task<AutoTagPassResult> TagUserNotesAsync(Guid userId, int maxNotes, TimeSpan minAge, CancellationToken cancellationToken = default)
    {
        EnsureEnabled();

        return RunAsync([userId], maxNotes, minAge, cancellationToken);
    }

    private void EnsureEnabled()
    {
        if (!_options.IsEnabled)
        {
            throw new InvalidOperationException("Auto-tagging is not enabled (OLLAMA_URL).");
        }
    }

    private async Task<AutoTagPassResult> RunAsync(IReadOnlyList<Guid> userIds, int maxNotes, TimeSpan minAge, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var perUser = new List<(Guid UserId, IReadOnlyList<Guid> NoteIds)>();
        int usersProcessed = 0, usersFailed = 0;

        foreach (var userId in userIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var noteIds = await _state.GetCandidateNoteIdsAsync(userId, now - minAge, now, maxNotes, cancellationToken);
                perUser.Add((userId, noteIds));
                usersProcessed++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Auto-tagging couldn't list candidate notes for user {UserId}: {ErrorType}", userId, ex.GetType().Name);
                usersFailed++;
            }
        }

        var work = AutoTagCandidates.Interleave(perUser, maxNotes);

        int tagged = 0, noTags = 0, skipped = 0, failed = 0, deferred = 0;
        string? unavailableReason = null;

        foreach (var (userId, noteId) in work)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                switch (await TagNoteAsync(userId, noteId, minAge, cancellationToken))
                {
                    case AutoTagNoteResult.Tagged: tagged++; break;
                    case AutoTagNoteResult.NoTags: noTags++; break;
                    case AutoTagNoteResult.Skipped: skipped++; break;
                    case AutoTagNoteResult.Failed: failed++; break;
                    default: deferred++; break;
                }
            }
            catch (AutoTagException ex) when (ex.Kind == AutoTagFailureKind.Unavailable)
            {
                // Ollama's problem, not this note's: nothing recorded, and every later note would fail the same way.
                unavailableReason = ex.Message;
                break;
            }
            catch (AutoTagException ex) when (ex.Kind == AutoTagFailureKind.Timeout)
            {
                // Charged to the note (a huge one may never fit in the timeout), and the pass stops: Ollama is
                // probably cold or busy, and one slow note mustn't hold up the queue.
                await _state.RecordFailureAsync(userId, noteId, _time.GetUtcNow(), cancellationToken);
                failed++;
                unavailableReason = ex.Message;
                break;
            }
            catch (AutoTagException)
            {
                await _state.RecordFailureAsync(userId, noteId, _time.GetUtcNow(), cancellationToken);
                failed++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NoteParseException)
            {
                _logger.LogWarning("Auto-tagging note {NoteId} of user {UserId} failed: {ErrorType}", noteId, userId, ex.GetType().Name);
                await _state.RecordFailureAsync(userId, noteId, _time.GetUtcNow(), cancellationToken);
                failed++;
            }
        }

        return new AutoTagPassResult(usersProcessed, usersFailed, tagged, noTags, skipped, failed, deferred, unavailableReason);
    }

    private async Task<AutoTagNoteResult> TagNoteAsync(Guid userId, Guid noteId, TimeSpan minAge, CancellationToken cancellationToken)
    {
        // The file, not the index, decides — the index only nominated this note.
        string raw;
        try
        {
            raw = await _fileStore.ReadRawAsync(userId, noteId, cancellationToken);
        }
        catch (NoteNotFoundException)
        {
            return AutoTagNoteResult.Deferred; // gone since the candidate query; the reconciler removes its row
        }

        Note note;
        try
        {
            note = _serializer.Deserialize(raw);
        }
        catch (NoteParseException)
        {
            await _state.RecordOutcomeAsync(userId, noteId, AutoTagOutcome.Skipped, _time.GetUtcNow(), cancellationToken);
            return AutoTagNoteResult.Skipped;
        }

        // Only rewrite what JNotes itself would have written, so a background write changes nothing but the
        // tags: a hand-edited file (extra front-matter keys, CRLF, leading blank body lines) is left alone.
        if (note.Id != noteId || !string.Equals(_serializer.Serialize(note), raw, StringComparison.Ordinal))
        {
            _logger.LogDebug("Note {NoteId} of user {UserId} is not in canonical format; skipping.", noteId, userId);
            await _state.RecordOutcomeAsync(userId, noteId, AutoTagOutcome.Skipped, _time.GetUtcNow(), cancellationToken);
            return AutoTagNoteResult.Skipped;
        }

        if (note.Tags.Count > 0)
        {
            return AutoTagNoteResult.Deferred; // tagged since it was indexed; the reconciler will catch the index up
        }

        if (string.IsNullOrWhiteSpace(note.Title) && string.IsNullOrWhiteSpace(note.Body))
        {
            return AutoTagNoteResult.Deferred;
        }

        var mtime = await _fileStore.GetLastWriteTimeUtcAsync(userId, noteId, cancellationToken);
        if (_time.GetUtcNow() - mtime < minAge)
        {
            return AutoTagNoteResult.Deferred; // still being edited, perhaps in an open editor
        }

        // The note owner's own tags only (explicit userId), so one user's spelling never reaches another's prompt.
        var vocabulary = await _indexRepository.SuggestTagsAsync(userId, null, [], AutoTagPromptBuilder.MaxVocabulary, cancellationToken);
        var prompt = AutoTagPromptBuilder.Build(note.Title, note.Body, [], vocabulary);
        var rawTags = await _model.GenerateTagsAsync(prompt, cancellationToken);

        var tags = AutoTagNormalizer.Normalize(rawTags, [], vocabulary);
        if (tags.Count == 0)
        {
            await _state.RecordOutcomeAsync(userId, noteId, AutoTagOutcome.NoTags, _time.GetUtcNow(), cancellationToken);
            return AutoTagNoteResult.NoTags;
        }

        // UpdatedAt deliberately untouched: the list order, relative-date grouping and tag recency don't move.
        note.Tags = tags;
        if (!await _fileStore.ReplaceIfUnchangedAsync(userId, noteId, raw, note, cancellationToken))
        {
            _logger.LogDebug("Note {NoteId} of user {UserId} changed during tagging; leaving it for a later pass.", noteId, userId);
            return AutoTagNoteResult.Deferred;
        }

        // File first, then index, mirroring NoteService. IndexedFileWriteTimeUtc is the post-write mtime (at index
        // resolution, like the reconciler), so the next rescan sees the file as unchanged and doesn't re-parse it.
        var writeTime = await _fileStore.GetLastWriteTimeUtcAsync(userId, noteId, cancellationToken);
        await _indexRepository.UpsertAsync(
            new NoteIndexEntry
            {
                Id = note.Id,
                UserId = userId,
                Title = note.Title,
                Body = note.Body,
                CreatedAt = note.CreatedAt,
                UpdatedAt = note.UpdatedAt,
                IndexedFileWriteTimeUtc = IndexTimestamp.ToIndexResolution(writeTime),
                FilePath = $"{userId}/{note.Id}.md",
            },
            tags,
            cancellationToken);

        await _state.RecordOutcomeAsync(userId, noteId, AutoTagOutcome.Tagged, _time.GetUtcNow(), cancellationToken);
        _logger.LogDebug("Tagged note {NoteId} of user {UserId} with {TagCount} tag(s).", noteId, userId, tags.Count);
        return AutoTagNoteResult.Tagged;
    }
}
