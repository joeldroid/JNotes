# Plan: Replace the editor "Auto-tag" button with an Ollama background tagger that runs only when OLLAMA_URL is set

## Goal (Summary)
The editor's **Auto-tag** button, and everything that exists only to support it, is removed. A hosted `AutoTagBackgroundService` in Web now tags notes periodically, and only when `OLLAMA_URL` is a valid absolute http(s) URL. There is no separate on/off flag.

Each pass works through notes that have **no tags at all**. It handles one note at a time, caps the number of notes per pass, and gives every note one attempt. The result is remembered in a new, non-derived `NoteAutoTagAttempts` table. The tagger never overwrites or adds to tags a user wrote. Writes follow the existing file-first-then-index path, a note is written only if its file is byte-identical to what the model saw, and `UpdatedAt` is not changed, so notes don't move in the list.

## Context found
- Existing Tagging code (commit on branch `ollama-integration`), `C:\Data\Code\JNotes\src\JNotes.Core\Tagging\`:
  - `AutoTagOptions.cs`: `Enabled`, `ParseFlag`, `ParseTimeout`, and `IsEnabled => Enabled && OllamaBaseUri != null && Model non-blank`. It reuses `PdfExportOptions.ParseBaseUri`.
  - `OllamaTagModelClient.cs`: typed HttpClient calling `POST api/chat`. It maps failures to `AutoTagException(message, inner)` and logs `LogWarning(ex, …)`. That exception text can contain host:port.
  - `AutoTagException.cs`: no failure kind.
  - `AutoTagPrompt.cs` (`AutoTagPromptBuilder.Build(type, title, body, currentTags, vocabulary)`, `MaxVocabulary = 50`), `AutoTagResponseParser.cs` and `AutoTagNormalizer.cs`: pure. **Keep them.**
  - `INoteAutoTagService.cs` / `NoteAutoTagService.cs`: implicit current user, reads vocabulary through `INoteService`. **Delete both.**
- Registration: `C:\Data\Code\JNotes\src\JNotes.Core\ServiceCollectionExtensions.cs` lines 95–117. The class doc comment (line 26) lists `INoteAutoTagService`.
- Web:
  - `C:\Data\Code\JNotes\src\JNotes.Web\Program.cs` lines 188–198 hold the auto-tag startup log and the `autoTag.Enabled` warning. Hosted services are registered at lines 28–31.
  - `C:\Data\Code\JNotes\src\JNotes.Web\Components\Pages\NoteEditor.razor`:
    - line 5 `@using Microsoft.Extensions.Options` (only used by line 8)
    - lines 7–8 injects
    - lines 39–54 markup
    - lines 115–120 fields
    - lines 148–149 `HasTaggableContent`
    - lines 156–159 resets
    - lines 224–290 `AutoTagAsync`
    - lines 417–418 CTS disposal
  - `C:\Data\Code\JNotes\src\JNotes.Web\wwwroot\css\note-editor.css` lines 50–56 `.jn-auto-tag`.
  - `_Imports.razor` line 24 `@using JNotes.Core.Tagging`. Keep it, because Settings will use `AutoTagOptions`.
- Background-service precedents:
  - `C:\Data\Code\JNotes\src\JNotes.Web\Indexing\AutoIndexBackgroundService.cs`: env interval, `PeriodicTimer`, a scope per tick, never rethrows.
  - `C:\Data\Code\JNotes\src\JNotes.Web\Images\ImageCleanupBackgroundService.cs`: a do/while loop that also checks at startup.
- Core all-users precedents, explicit `userId`, per-user failure isolation, cancellation rethrown:
  - `C:\Data\Code\JNotes\src\JNotes.Core\Images\ImageCleanupService.cs`
  - `C:\Data\Code\JNotes\src\JNotes.Core\Indexing\AllUsersIndexReconciler.cs`
  - `ImageCleanupSchedule` (pure static, `MinImageAge` = 10 min)
  - `AutoIndexInterval` (pure parse/clamp)
- Write path:
  - `NoteService.AutosaveAsync(Metadata)` → `WriteAndIndexAsync` does `_fileStore.WriteAsync`, then `GetLastWriteTimeUtcAsync`, then `_indexRepository.UpsertAsync(entry, tags)`.
  - `UpsertAsync` recomputes `NoteIndexUserTags` for old ∪ new tags in the same transaction.
  - `AutosaveAsync` always sets `UpdatedAt = UtcNow`, so the new code must **not** go through `NoteService`, which is also implicit-user.
  - `NoteIndexReconciler.RescanAsync` (lines 58–70) builds a `NoteIndexEntry` from a file. Copy that shape.
- Reconciler skip rule: `lastIndexed >= IndexTimestamp.ToIndexResolution(mtime)`. If `IndexedFileWriteTimeUtc` is set to the post-write mtime, the reconciler won't re-parse the note.
- `FileSystemNoteFileStore`: `WriteAsync` = `File.WriteAllTextAsync(serializer.Serialize(note))`, and `ReadAsync` deserializes. There is no raw-by-id read today; only `EnumerateRawContentsAsync` exists.
- `NoteMarkdownSerializer`:
  - Uses `IgnoreUnmatchedProperties`, so **any app rewrite drops unknown front-matter keys**.
  - `FrontMatterSplitter.Split` turns CRLF into LF and trims leading `\n` from the body.
  - A rewrite of a hand-edited file can therefore change bytes outside the tags. This is why the canonical-format guard below exists.
- `INoteIndexRepository.SuggestTagsAsync(Guid userId, string? term, IReadOnlyCollection<string> exclude, int limit, ct)` is the explicit-userId vocabulary source. It clamps the limit to 1..50.
- DB: `C:\Data\Code\JNotes\src\JNotes.Core\Data\JNotesDbContext.cs`.
  - `UserSetting` has an FK to `UserAccount` with cascade. `Role` uses `HasConversion<string>()`.
  - Migrations are in `src/JNotes.Core/Data/Migrations/` (latest `20260926083127_AddNoteIndexUserTags`).
- Editor concurrency: `NoteEditor` holds an in-memory `_note` loaded on open and autosaves the **whole** note. A background write to an open note can be overwritten by the editor's next save, and a background write made in the middle of an edit could overwrite the edit. `NoteView`'s checklist mutations re-read the file each time, so they are safe.
- Tests:
  - `tests/JNotes.Tests/Integration/CoreServicesFixture.cs` lines 124–131 set `o.Enabled = true` and `OllamaUrl`, register `RecordingTagModelClient`, and expose `NoteAutoTagService` (line 74).
  - `tests/JNotes.Tests/Tagging/*Tests.cs`: `OllamaTagModelClientTests.Create` sets `Enabled = true` at line 31.
  - `tests/JNotes.Tests/Integration/NoteAutoTagServiceTests.cs`
  - `tests/JNotes.Tests/Storage/FileSystemNoteFileStoreTests.cs`
- Config and docs:
  - `src/JNotes.Web/appsettings.json` lines 19–22 contain `AUTO_TAG_ENABLED`, `"OLLAMA_URL": "http://localhost:11434"`, model and timeout. With the new rule, that URL would **silently enable tagging in dev**.
  - `docker-compose.yml` lines 40–51 and 113–127.
  - README: line 17 (Features), 40 (layout), 118–121 (config table), 198–223 (section).
  - CLAUDE.md: line 43 (interface list), 536–546 (section), 569–571 (Tests).
- `Microsoft.Extensions.Http` 10.0.12 is referenced by Core, so `IHttpClientBuilder.RemoveAllLoggers()` is available (added in .NET 8). The default `System.Net.Http.HttpClient.*` Information logs print the request URL on every call, and `appsettings.json` doesn't silence them.
- Nothing in the repo uses `TimeProvider` yet.
- Ollama (docs.ollama.com):
  - `/api/chat` takes `model, messages, stream (default true), format (string|object), options, keep_alive, think`.
  - A non-streaming reply is `{model, created_at, message:{role,content}, done, done_reason, …}`, and errors are `{"error": "…"}`.
  - Models stay loaded **5 min** by default.
  - `OLLAMA_NUM_PARALLEL` defaults to 1, and the server answers **503** when overloaded (queue > `OLLAMA_MAX_QUEUE`, default 512).

## What changes vs the current implementation (delta)
- **Removed:**
  - The editor button, its state, CTS handling, the `IOptions` inject and `.jn-auto-tag` CSS.
  - `INoteAutoTagService` / `NoteAutoTagService` and their integration tests.
  - `AUTO_TAG_ENABLED`, `AutoTagOptions.Enabled` and `ParseFlag`.
  - The `OLLAMA_URL` default in `appsettings.json`.
- **Changed:**
  - `AutoTagOptions.IsEnabled` = valid URL + model.
  - `AutoTagException` gains a `Kind`.
  - `OllamaTagModelClient` classifies failures, stops logging URL-bearing exceptions, and its HttpClient loggers are removed.
- **Added:**
  - Core: `INoteAutoTagger` / `NoteAutoTagger` (explicit userId), `IAutoTagStateRepository` / `PostgresAutoTagStateRepository`, the `NoteAutoTagAttempt` entity with an EF migration, pure `AutoTagSchedule` and `AutoTagCandidates`, and two new `INoteFileStore` methods (`ReadRawAsync`, `ReplaceIfUnchangedAsync`).
  - Web: `AutoTagBackgroundService`, an `AutoTagStatus` singleton, and a read-only "Auto-tagging" card in Settings → Administration.
  - Env vars `AUTO_TAG_INTERVAL_MINUTES` and `AUTO_TAG_BATCH_SIZE`.
  - `TimeProvider.System` registration.
- **Unchanged:** `AutoTagPromptBuilder`, `AutoTagResponseParser` (except the exception kind), `AutoTagNormalizer`, `ITagModelClient`, `NoteService`, `NoteIndexReconciler`, `TagInput`, `TagList`, and the compose `ollama` service and profile.

## Decisions made
1. **Enable rule.**
   - Enabled iff `OLLAMA_URL` parses via `PdfExportOptions.ParseBaseUri` and `OLLAMA_MODEL` isn't blank. There is no flag.
   - `AUTO_TAG_ENABLED` is ignored. If it's present in configuration, `Program.cs` logs one warning: "AUTO_TAG_ENABLED is no longer used; auto-tagging runs whenever OLLAMA_URL is set." Anyone who set it to false and expects "off" gets told.
   - To turn the feature off, leave `OLLAMA_URL` unset. There is deliberately no second switch.
2. **Env vars.** All are flat keys read in `AddJNotesCore`.

   | Variable | Default | Rule |
   |---|---|---|
   | `OLLAMA_URL` | unset | Enables the feature |
   | `OLLAMA_MODEL` | `phi4` | Must not be blank |
   | `OLLAMA_TIMEOUT_SECONDS` | 120 | Clamped 10..600, unchanged |
   | `AUTO_TAG_INTERVAL_MINUTES` | 10 | Clamped 1..1440; unparseable or ≤0 → default |
   | `AUTO_TAG_BATCH_SIZE` | 10 | Maximum notes per pass across all users; clamped 1..100; unparseable or ≤0 → default |

   The fixed constants are not env vars: `AutoTagSchedule.MinNoteAge` = 10 min and the Web `InitialDelay` = 1 min.
3. **Which notes (Q1).**
   - Only live, indexed notes with **zero tags**, a non-blank title or body, and index `UpdatedAt` older than `MinNoteAge`.
   - A note is excluded if it has an attempt row, unless that row is `Failed` and its `RetryAfterUtc` has passed.
   - Newest first. One SQL query per user over `NoteIndexEntries` with `NOT EXISTS` on `NoteIndexTags` and on `NoteAutoTagAttempts`.
   - The index is only used to pick candidates. Before the model call, the file is the authority: it is re-read raw, and the note is skipped if the file already has tags or is too recent by mtime.
   - Tagged notes are never touched, and the tagger never adds to existing tags.
4. **Remembering attempts (Q2): a small non-derived DB table, not front matter or a hash.**
   - A front-matter marker would clutter portable `.md` files (Obsidian shows it as a property). It would also need `Note` and `NoteFrontMatter` model changes, because every editor save drops unknown keys.
   - Pure mtime or hash comparison has no memory, so failures and "model returned nothing" would be retried forever.
   - Table `NoteAutoTagAttempts`, PK `(UserId, NoteId)`:
     - `Outcome` stored as a string: `Tagged | NoTags | Skipped | Failed | GaveUp`
     - `FailureCount` int
     - `LastAttemptUtc` timestamptz
     - `RetryAfterUtc` timestamptz, nullable
     - FK to `Users` with cascade (like `UserSettings`), so deleted accounts clean up
   - The name deliberately does **not** use the `NoteIndex*` prefix, because it isn't rebuildable from files. Truncating it only means every untagged note gets one more attempt. Document that.
   - A note gets one final attempt ever (`Tagged`/`NoTags`/`Skipped`). So tags a user later removes from an auto-tagged note never come back, and "nothing useful" isn't retried.
   - Backoff (`AutoTagSchedule`): after failure n = 1, 2, 3, 4, retry after 1 h, 6 h, 24 h, 72 h. At n = 5 the outcome becomes `GaveUp`.
5. **Failure classification (Q2/Q4).** New `AutoTagFailureKind { Unavailable, Timeout, BadResponse }`.
   - `Unavailable` covers connection failure, 5xx including 503 overloaded, 429, and 404 "model not found". The **pass stops immediately** and **no per-note record** is written, because it isn't the note's fault.
   - `Timeout` records a per-note failure with backoff **and** stops the pass. This stops one huge note from blocking the queue forever, and Ollama is probably cold or busy anyway.
   - `BadResponse` covers unparseable output, a 200 with `error`, and other 4xx. It records a per-note failure with backoff and the pass **continues**.
   - Any other per-note exception (IO, parse) is logged with note and user ids only, recorded as a failure, and the pass continues. If the DB itself is down, recording throws and the background service's catch-all logs one error.
   - Cooldown while Ollama is down is simply the next interval tick: one cheap probe per interval. Logging happens on state transitions only: Warning once when it becomes unavailable, Information when it recovers, Debug otherwise.
6. **Concurrency (Q3).**
   - (a) Skip notes whose file mtime is within `MinNoteAge`. The SQL also filters on index `UpdatedAt`, so recently edited notes don't use up batch slots.
   - (b) Read the raw text before the model call.
   - (c) Write with `INoteFileStore.ReplaceIfUnchangedAsync(userId, noteId, expectedContent, replacement)`. This opens the file `FileShare.None`, reads it, compares ordinal-exact, then truncates and writes in the same handle, and returns false if the content changed or the file is missing. If it returns false, nothing is written or recorded, and the note is reconsidered in a later pass.
   - (d) **Canonical guard:** only rewrite when `serializer.Serialize(serializer.Deserialize(raw)) == raw`. Otherwise record `Skipped`. This way a background write only ever changes the `tags:` part of the front matter. Hand-edited files with extra keys, CRLF or leading blank body lines are left alone.
   - (e) Keep `UpdatedAt` unchanged: the list order, relative-date grouping and `NoteIndexUserTags.LastUsedAt` don't move.
   - (f) Order is file first, then `UpsertAsync` with `IndexedFileWriteTimeUtc` = post-write mtime (so the reconciler skips it), then the attempt row.
   - Accepted limitation: an editor left open **idle for more than 10 minutes** on an untagged note, then edited, overwrites the auto-tags. The note stays `Tagged` and isn't redone. This is consistent with the documented last-writer-wins behaviour.
7. **Lifecycle (Q4).**
   - The service is always registered and returns immediately when disabled.
   - It waits `InitialDelay` (1 min), then runs a pass, then uses a `PeriodicTimer(interval)` do/while like `ImageCleanupBackgroundService`.
   - Passes are sequential, one note at a time, up to `BatchSize` notes per pass across all users.
   - Fairness: users are shuffled per pass (`Random.Shared.Shuffle`), candidates are fetched per user (limit = batch), then merged round-robin (`AutoTagCandidates.Interleave`).
   - `stoppingToken` flows into the HTTP call, and `OperationCanceledException` on shutdown is rethrown and not treated as a failure.
   - A long pass just delays the next tick (`PeriodicTimer` never queues more than one tick).
   - A cold model load (Ollama's `keep_alive` default is 5 min, so with a 10-min interval each pass starts cold) counts toward `OLLAMA_TIMEOUT_SECONDS`. `keep_alive` is deliberately not sent, so a 9 GB model isn't pinned in RAM.
   - **First run: backfill existing untagged notes**, newest first, throttled by batch and interval (default at most 60 notes an hour). The "never-run schedule starts its clock" precedent exists because image cleanup *deletes*. Here the operation only adds tags to untagged notes and doesn't reorder anything, and setting `OLLAMA_URL` is the opt-in. See Open question 1.
8. **Tags (Q5).**
   - Normalisation is unchanged.
   - Vocabulary = `INoteIndexRepository.SuggestTagsAsync(userId, null, [], AutoTagPromptBuilder.MaxVocabulary)`, which is the note owner's own tags.
   - Prompt built with `currentTags = []`.
   - An empty normalised result → `NoTags`.
9. **UI (Q6).**
   - Nothing in the editor or the note view.
   - One **read-only** card in Settings → Administration, inside the existing Admin `AuthorizeView`, after "Images". It shows On/Off (Off: "set OLLAMA_URL"), the model, the interval and batch size, the last pass time and the number tagged, the total tagged since startup, and "Ollama unavailable since … : <safe reason>".
   - The data comes from an in-memory Web singleton, so it resets on restart.
   - No "Run now" button: a pass can run for minutes and would tie up a circuit.
   - The URL is never shown.
10. **Logging.**
    - Never log note content, titles, tags or the URL.
    - `.RemoveAllLoggers()` on the Ollama HttpClient.
    - In the client, log connection failures at Debug with `ex.HttpRequestError` only, not the exception object.
    - The pass summary is logged at Information only when something was tagged, NoTags, or failed.

## Files to create/modify/delete
- **Delete:**
  - `src/JNotes.Core/Tagging/INoteAutoTagService.cs`
  - `src/JNotes.Core/Tagging/NoteAutoTagService.cs`
  - `tests/JNotes.Tests/Integration/NoteAutoTagServiceTests.cs`
- **Create (Core):** in `src/JNotes.Core/Tagging/`:
  - `AutoTagFailureKind.cs`
  - `AutoTagSchedule.cs`
  - `AutoTagCandidates.cs`
  - `AutoTagOutcome.cs`
  - `AutoTagResults.cs` (records)
  - `IAutoTagStateRepository.cs`
  - `PostgresAutoTagStateRepository.cs`
  - `INoteAutoTagger.cs`
  - `NoteAutoTagger.cs`
  - `src/JNotes.Core/Data/NoteAutoTagAttempt.cs`
  - The migration `src/JNotes.Core/Data/Migrations/*_AddNoteAutoTagAttempts*.cs`, generated.
- **Create (Web):** `src/JNotes.Web/Tagging/AutoTagBackgroundService.cs`, `src/JNotes.Web/Tagging/AutoTagStatus.cs`.
- **Create (tests):**
  - `tests/JNotes.Tests/Tagging/AutoTagScheduleTests.cs`
  - `tests/JNotes.Tests/Tagging/AutoTagCandidatesTests.cs`
  - `tests/JNotes.Tests/Integration/NoteAutoTaggerTests.cs`
  - `tests/JNotes.Tests/TestDoubles/TestTimeProvider.cs`
- **Modify:**
  - Core: `AutoTagOptions.cs`, `AutoTagException.cs`, `AutoTagResponseParser.cs`, `OllamaTagModelClient.cs`, `ServiceCollectionExtensions.cs`, `Data/JNotesDbContext.cs`, `Storage/INoteFileStore.cs`, `Storage/FileSystemNoteFileStore.cs`, `Indexing/INoteIndexRepository.cs` (doc comment only: add `NoteAutoTagger` to the callers).
  - Web: `Program.cs`, `NoteEditor.razor`, `wwwroot/css/note-editor.css`, `Pages/Settings.razor`, `appsettings.json`.
  - Root: `docker-compose.yml`, `README.md`, `CLAUDE.md`, `docs/plans/ollama-auto-tagging.md` (a superseded note only).
  - Tests: `CoreServicesFixture.cs`, `RecordingTagModelClient.cs`, `AutoTagOptionsTests.cs`, `OllamaTagModelClientTests.cs`, `AutoTagResponseParserTests.cs`, `FileSystemNoteFileStoreTests.cs`.

## Steps

### Phase 1: remove the button path (solution builds and tests pass after this phase)
1. `NoteEditor.razor`:
   - Delete line 5 (`@using Microsoft.Extensions.Options`), lines 7–8 (the two auto-tag injects), and the whole `@if (AutoTagOptions.Value.IsEnabled) { … }` block (39–54), keeping `<div class="mt-2"><TagInput …/></div>`.
   - Delete the fields and comment at 115–120 and `HasTaggableContent` (148–149).
   - Delete the four resets in `OnParametersSetAsync` (156–159), `AutoTagAsync` and its doc comment (224–290), and the two `_autoTagCts` lines in `DisposeAsync` (417–418).
   - Grep the file for `autoTag|AutoTag|TagList` afterwards. `TagList` should no longer appear.
2. `note-editor.css`: delete the `.jn-auto-tag { … }` rule (lines 50–56).
3. Delete `INoteAutoTagService.cs`, `NoteAutoTagService.cs` and `NoteAutoTagServiceTests.cs`. In `ServiceCollectionExtensions.cs`, delete `services.AddScoped<INoteAutoTagService, NoteAutoTagService>();` and remove `INoteAutoTagService` from the doc comment on line 26.
4. `AutoTagOptions.cs`:
   - Remove `Enabled` and `ParseFlag`, and set `IsEnabled => OllamaBaseUri is not null && !string.IsNullOrWhiteSpace(Model);`.
   - Add constants `DefaultIntervalMinutes = 10`, `MaxIntervalMinutes = 1440`, `DefaultBatchSize = 10`, `MaxBatchSize = 100`, properties `int IntervalMinutes = DefaultIntervalMinutes` and `int BatchSize = DefaultBatchSize`, and `public static int ParseIntervalMinutes(string?)` / `ParseBatchSize(string?)`. The rule for both: `int.TryParse` with InvariantCulture on the trimmed value; fail or ≤0 → default; else `Math.Clamp(v, 1, max)`.
   - Rewrite the XML remarks: the service is the background tagger and Program/Settings read `IsEnabled`; never log `OllamaUrl`.
5. `ServiceCollectionExtensions.cs`, in the `AutoTagOptions` configure block:
   - Drop the `AUTO_TAG_ENABLED` line.
   - Add `options.IntervalMinutes = AutoTagOptions.ParseIntervalMinutes(configuration["AUTO_TAG_INTERVAL_MINUTES"]);` and `options.BatchSize = AutoTagOptions.ParseBatchSize(configuration["AUTO_TAG_BATCH_SIZE"]);`.
   - Update the comment to "off unless OLLAMA_URL is a valid http(s) URL".
   - Append `.RemoveAllLoggers()` to `AddHttpClient<ITagModelClient, OllamaTagModelClient>(…)`, with a comment that the default handler logs the request URL on every call.
6. `Program.cs`: replace lines 188–198 with:
   - if `autoTag.IsEnabled`: `LogInformation("Auto-tagging is enabled: Ollama model {Model}, every {IntervalMinutes} minute(s), up to {BatchSize} note(s) per pass.", …)`
   - else if `!string.IsNullOrWhiteSpace(autoTag.OllamaUrl)`: `LogWarning("OLLAMA_URL is set but isn't an absolute http(s) URL — auto-tagging is disabled.")`
   - then, independently, if `builder.Configuration["AUTO_TAG_ENABLED"] is not null`: `LogWarning("AUTO_TAG_ENABLED is no longer used; auto-tagging runs whenever OLLAMA_URL is set.")`
   - Never log the URL.
7. Tests for Phase 1:
   - `CoreServicesFixture.cs`: remove `o.Enabled = true;`, the `NoteAutoTagService` property (line 74), and update the `TagModel` doc to mention `INoteAutoTagger`.
   - `OllamaTagModelClientTests.Create`: drop `Enabled = true`.
   - `AutoTagOptionsTests`: delete the `ParseFlag` theory, rewrite the `IsEnabled` theory as (url, model, expected), and remove `AUTO_TAG_ENABLED` from `AddJNotesCore_reads_the_env_keys`. The full rework is in Tests.
8. `appsettings.json`: delete `"AUTO_TAG_ENABLED": false` and change `"OLLAMA_URL"` to `""`. An empty value means disabled, and keeping the key makes it discoverable. Keep `OLLAMA_MODEL` and `OLLAMA_TIMEOUT_SECONDS`, and add `"AUTO_TAG_INTERVAL_MINUTES": 10, "AUTO_TAG_BATCH_SIZE": 10`.
9. Run `dotnet build JNotes.slnx` and `dotnet test tests/JNotes.Tests --filter "FullyQualifiedName~Tagging"`.

### Phase 2: Core model-client and pure pieces
10. `AutoTagFailureKind.cs`: `public enum AutoTagFailureKind { Unavailable, Timeout, BadResponse }`, with an XML doc per member describing the pass behaviour from Decision 5.
11. `AutoTagException.cs`: `public sealed class AutoTagException(string message, AutoTagFailureKind kind, Exception? inner = null) : Exception(message, inner) { public AutoTagFailureKind Kind { get; } = kind; }`. `AutoTagResponseParser` throws with `BadResponse`.
12. `OllamaTagModelClient.cs`: pass kinds as follows.
    - 404 containing "model" → `Unavailable` (keep the pull-hint message).
    - `(int)status >= 500` or 429 → `Unavailable`.
    - Other non-2xx → `BadResponse`.
    - 200 with `error` → `BadResponse`.
    - `JsonException` → `BadResponse`.
    - `HttpRequestException ex` → `Unavailable`. Change its log to `logger.LogDebug("Ollama couldn't be reached ({Error}).", ex.HttpRequestError)`, with **no exception object**, because its message includes host:port.
    - Timeout → `Timeout`, logged at Debug without the exception.
    - Keep the non-2xx `LogWarning` with status and the error snippet, but lower it to `LogDebug`. The background service logs the state transition instead, which avoids a warning per note.
    - Update the class doc: it's a background caller, and logs are Debug level.
13. `AutoTagOutcome.cs`: `public enum AutoTagOutcome { Tagged, NoTags, Skipped, Failed, GaveUp }`.
14. `AutoTagSchedule.cs` (pure static, doc like `ImageCleanupSchedule`):
    - `public static readonly TimeSpan MinNoteAge = TimeSpan.FromMinutes(10);` (reason: an editor autosaves the whole in-memory note, so a note must be quiet before it's written behind the user's back)
    - `public const int MaxFailures = 5;`
    - `public static TimeSpan RetryDelay(int failureCount)` → 1 → 1h, 2 → 6h, 3 → 24h, ≥4 → 72h
    - `public static (AutoTagOutcome Outcome, DateTimeOffset? RetryAfterUtc) AfterFailure(int newFailureCount, DateTimeOffset nowUtc)` → `newFailureCount >= MaxFailures ? (GaveUp, null) : (Failed, nowUtc + RetryDelay(newFailureCount))`
15. `AutoTagCandidates.cs`: `public static IReadOnlyList<(Guid UserId, Guid NoteId)> Interleave(IReadOnlyList<(Guid UserId, IReadOnlyList<Guid> NoteIds)> perUser, int max)`. Round-robin, taking the first of each user, then the second of each, and so on, stopping at `max`. The input order is preserved; the shuffle happens in the caller.
16. `AutoTagResults.cs`:
    - `public sealed record AutoTagPassResult(int UsersProcessed, int UsersFailed, int Tagged, int NoTags, int Skipped, int Failed, int Deferred, string? UnavailableReason) { public bool ServiceUnavailable => UnavailableReason is not null; }`
    - `internal enum AutoTagNoteResult { Tagged, NoTags, Skipped, Failed, Deferred }`. Deferred covers too recent, changed, already tagged, not found, and blank.

### Phase 3: storage, table, repository, tagger
17. `INoteFileStore.cs`:
    - `Task<string> ReadRawAsync(Guid userId, Guid noteId, CancellationToken ct = default);` throws `NoteNotFoundException`.
    - `Task<bool> ReplaceIfUnchangedAsync(Guid userId, Guid noteId, string expectedContent, Note replacement, CancellationToken ct = default);`, documented as a compare-and-swap for background writers: false if the file is missing or its text differs from `expectedContent`, holding an exclusive handle for the read-compare-write.
18. `FileSystemNoteFileStore.cs`:
    - `ReadRawAsync`: same as `ReadAsync` but returns `File.ReadAllTextAsync` without deserializing.
    - `ReplaceIfUnchangedAsync`:
      1. Return false if `!File.Exists`.
      2. `await using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, useAsync: true)`.
      3. Read with `StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true)`; if the text differs ordinally, return false.
      4. Otherwise `stream.SetLength(0)`, then write `_serializer.Serialize(replacement)` through a `StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)`. That matches what `File.WriteAllTextAsync` writes: UTF-8 with no BOM.
      5. Flush and return true.
      - Catch `FileNotFoundException` → false.
      - Let `IOException` (sharing violation) propagate; the tagger treats it as a per-note failure.
19. `Data/NoteAutoTagAttempt.cs`: `public sealed class NoteAutoTagAttempt { required Guid UserId; required Guid NoteId; required AutoTagOutcome Outcome; int FailureCount; required DateTimeOffset LastAttemptUtc; DateTimeOffset? RetryAfterUtc; }`. The doc comment says: not derived, not rebuildable, safe but not free to truncate.
20. `JNotesDbContext.cs`:
    - `public DbSet<NoteAutoTagAttempt> NoteAutoTagAttempts => Set<NoteAutoTagAttempt>();`
    - In `OnModelCreating`: `ToTable("NoteAutoTagAttempts")`, `HasKey(e => new { e.UserId, e.NoteId })`, `Property(e => e.Outcome).HasConversion<string>()`, `HasOne<UserAccount>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade)`.
    - Comment: deliberately no FK to `NoteIndexEntries` (the index is truncatable) and no `NoteIndex*` prefix.
21. Migration: `dotnet tool restore`, then `dotnet ef migrations add AddNoteAutoTagAttempts --project src/JNotes.Core --startup-project src/JNotes.Core -o Data/Migrations`. Check that the generated `Up` creates only this table and FK, and `Down` drops it.
22. `IAutoTagStateRepository.cs` and `PostgresAutoTagStateRepository.cs`. It is a singleton over `IDbContextFactory<JNotesDbContext>`, like `PostgresNoteIndexRepository`. Methods:
    - `Task<IReadOnlyList<Guid>> GetCandidateNoteIdsAsync(Guid userId, DateTimeOffset updatedBeforeUtc, DateTimeOffset nowUtc, int limit, CancellationToken ct = default)`. The query:
      ```csharp
      db.NoteIndex.AsNoTracking()
        .Where(e => e.UserId == userId && e.UpdatedAt < updatedBeforeUtc)
        .Where(e => e.Title.Trim() != "" || e.Body.Trim() != "")
        .Where(e => !db.NoteIndexTags.Any(t => t.NoteIndexEntryId == e.Id))
        .Where(e => !db.NoteAutoTagAttempts.Any(a => a.UserId == userId && a.NoteId == e.Id
              && (a.Outcome != AutoTagOutcome.Failed || a.RetryAfterUtc > nowUtc)))
        .OrderByDescending(e => e.UpdatedAt).Select(e => e.Id).Take(limit)
      ```
    - `Task RecordOutcomeAsync(Guid userId, Guid noteId, AutoTagOutcome outcome, DateTimeOffset nowUtc, CancellationToken ct = default)`. For final outcomes, upsert with `RetryAfterUtc = null`, keeping `FailureCount`.
    - `Task RecordFailureAsync(Guid userId, Guid noteId, DateTimeOffset nowUtc, CancellationToken ct = default)`. Read or create the row, `FailureCount++`, then apply `AutoTagSchedule.AfterFailure`. Read-modify-write is fine because a single hosted service is the only writer.
    - `Task<NoteAutoTagAttempt?> GetAsync(Guid userId, Guid noteId, CancellationToken ct = default)`, used by tests and diagnostics.
    - The doc explains why it reads the `NoteIndex*` tables: an efficient `NOT EXISTS` in one query.
    - All `DateTimeOffset` values must have a UTC offset (Npgsql timestamptz rejects others). `TimeProvider.GetUtcNow()` satisfies this.
23. `INoteAutoTagger.cs`. Doc: explicit `userId` for the same reason as `INoteIndexReconciler`/`IImageCleanupService`, because the caller is a background service with no current user.
    - `Task<AutoTagPassResult> TagAllUsersAsync(int maxNotes, TimeSpan minAge, CancellationToken ct = default);`
    - `Task<AutoTagPassResult> TagUserNotesAsync(Guid userId, int maxNotes, TimeSpan minAge, CancellationToken ct = default);`
24. `NoteAutoTagger.cs`, scoped. Constructor: `INoteFileStore`, `INoteIndexRepository`, `IAutoTagStateRepository`, `IUserRepository`, `INoteMarkdownSerializer`, `ITagModelClient`, `IOptions<AutoTagOptions>`, `TimeProvider`, `ILogger<NoteAutoTagger>`.
    - Both public methods throw `InvalidOperationException` if `!options.IsEnabled`.
    - `TagAllUsersAsync`: get all users, then `Random.Shared.Shuffle` a copy. For each user (`ct.ThrowIfCancellationRequested()`), in try/catch: `GetCandidateNoteIdsAsync(user.Id, now - minAge, now, maxNotes)`; on a non-cancellation exception, log a Warning with the user id and increment `UsersFailed`. Then `Interleave`, then the shared `ProcessAsync(list)`.
    - `TagUserNotesAsync`: the same for one user.
    - `ProcessAsync`: for each (userId, noteId), call `TagNoteAsync` and tally the counts. Error handling:
      - `catch (AutoTagException ex) when (ex.Kind == Unavailable)`: set `UnavailableReason = ex.Message` and **break**.
      - `when (ex.Kind == Timeout)`: `RecordFailureAsync`, set the reason, **break**.
      - `when (BadResponse)`: `RecordFailureAsync`, count Failed, continue.
      - `catch (OperationCanceledException) when (ct.IsCancellationRequested)`: rethrow.
      - `catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NoteParseException)`: log a Warning with note and user ids only (`LogWarning("Auto-tagging note {NoteId} of user {UserId} failed: {ErrorType}", …, ex.GetType().Name)`), then `RecordFailureAsync` and continue.
    - `TagNoteAsync(userId, noteId, minAge, ct)` returns `AutoTagNoteResult`:
      1. `raw = ReadRawAsync`. On `NoteNotFoundException` → Deferred.
      2. `note = serializer.Deserialize(raw)`. On `NoteParseException`, record `Skipped` and return Skipped.
      3. If `note.Id != noteId` or `serializer.Serialize(note) != raw`, record `Skipped` (log at Debug: "not in canonical format") and return Skipped.
      4. If `note.Tags.Count > 0` → Deferred. The index is stale and the reconciler will catch it up.
      5. If the title and body are both blank → Deferred.
      6. `mtime = GetLastWriteTimeUtcAsync`. If `now - mtime < minAge` → Deferred.
      7. `vocabulary = indexRepository.SuggestTagsAsync(userId, null, [], AutoTagPromptBuilder.MaxVocabulary, ct)`.
      8. `prompt = AutoTagPromptBuilder.Build(note.Type, note.Title, note.Body, [], vocabulary)`, then `rawTags = model.GenerateTagsAsync(prompt, ct)` (exceptions bubble to `ProcessAsync`).
      9. `tags = AutoTagNormalizer.Normalize(rawTags, [], vocabulary)`. If empty, record `NoTags` and return.
      10. `note.Tags = tags;`. **Do not touch `UpdatedAt`.** Then `if (!await fileStore.ReplaceIfUnchangedAsync(userId, noteId, raw, note, ct)) return Deferred;`. Log at Debug: "changed during tagging".
      11. `writeTime = GetLastWriteTimeUtcAsync`, then `indexRepository.UpsertAsync(new NoteIndexEntry { … same fields as NoteIndexReconciler lines 58–69, UpdatedAt = note.UpdatedAt, IndexedFileWriteTimeUtc = writeTime }, tags, ct)`.
      12. Record `Tagged`, and log at Debug with the note id, user id and tag count (never the tag text).
25. `ServiceCollectionExtensions.cs`:
    - `services.TryAddSingleton(TimeProvider.System);` (add `using Microsoft.Extensions.DependencyInjection.Extensions;`)
    - `services.AddSingleton<IAutoTagStateRepository, PostgresAutoTagStateRepository>();`
    - `services.AddScoped<INoteAutoTagger, NoteAutoTagger>();`, with a comment: scoped like `IImageCleanupService`; the singleton hosted service makes a scope per pass.
    - Add `INoteAutoTagger` to the doc comment's list.
26. `INoteIndexRepository.cs`: update the doc sentence "NoteService and NoteIndexReconciler are the only callers" to include `NoteAutoTagger` (explicit userId).

### Phase 4: Web background service and status card
27. `src/JNotes.Web/Tagging/AutoTagStatus.cs`, a singleton that is thread-safe (lock) and holds an immutable snapshot:
    - `public sealed record AutoTagStatusSnapshot(bool IsRunning, DateTimeOffset? LastPassFinishedUtc, int LastPassTagged, int TaggedSinceStartup, string? UnavailableReason, DateTimeOffset? UnavailableSinceUtc);`
    - Methods `Snapshot`, `PassStarted()`, `PassFinished(AutoTagPassResult result, DateTimeOffset nowUtc)`. `PassFinished` sets or clears unavailability, keeping `UnavailableSinceUtc` from the first failure, and returns the previous unavailable state so the service can log transitions.
28. `src/JNotes.Web/Tagging/AutoTagBackgroundService.cs` : `BackgroundService`. Constructor: `IServiceScopeFactory`, `IOptions<AutoTagOptions>`, `AutoTagStatus`, `ILogger`. Doc mirrors the two existing services. `ExecuteAsync`:
    1. If `!options.IsEnabled`, return. `Program.cs` already logged the state.
    2. `private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);`, then `await Task.Delay(InitialDelay, stoppingToken)`.
    3. `using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.IntervalMinutes));`, then a do/while exactly like `ImageCleanupBackgroundService` (catch `OperationCanceledException` when stopping → break; catch `Exception` → `LogError("Auto-tagging pass failed; will retry on the next interval.")`, never rethrow). The outer try catches `OperationCanceledException` for shutdown.
    4. Per pass: `status.PassStarted()`, create an async scope, `INoteAutoTagger.TagAllUsersAsync(options.BatchSize, AutoTagSchedule.MinNoteAge, stoppingToken)`, then `status.PassFinished(result, UtcNow)` in a finally block that also covers exceptions.
    5. Logging:
       - Newly unavailable → `LogWarning("Auto-tagging paused: {Reason} Retrying every {IntervalMinutes} minute(s).", result.UnavailableReason, …)`. The reason texts are user-safe and URL-free.
       - Still unavailable → Debug.
       - Recovered → `LogInformation("Auto-tagging resumed.")`.
       - `Tagged + NoTags + Failed + Skipped > 0` or `UsersFailed > 0` → `LogInformation("Auto-tag: {Tagged} tagged, {NoTags} without suggestions, {Skipped} skipped, {Failed} failed, {Deferred} deferred; {UsersFailed} user(s) failed.")`.
       - Otherwise Debug.
29. `Program.cs`:
    - After line 31: `builder.Services.AddSingleton<AutoTagStatus>();` and `builder.Services.AddHostedService<AutoTagBackgroundService>();`, with the comment "Background Ollama auto-tagging — runs only when OLLAMA_URL is set (AutoTagOptions.IsEnabled)."
    - Add `using JNotes.Web.Tagging;`.
30. `Settings.razor`:
    - Add `@using JNotes.Web.Tagging`, `@inject IOptions<AutoTagOptions> AutoTagOptionsAccessor` and `@inject AutoTagStatus AutoTagStatus`.
    - After the Images `MudPaper` (inside the Admin `AuthorizeView`), add a `MudPaper Outlined="true" Class="jn-settings-card"` titled "Auto-tagging" (`Typo.subtitle1`, `HtmlTag="h3"`) containing:
      - If disabled: body2 secondary text "Off. Set OLLAMA_URL to tag untagged notes automatically with a local Ollama model."
      - Else: "Every @IntervalMinutes minute(s), up to @BatchSize untagged note(s) per pass are sent to the Ollama model <code class="jn-code">@Model</code> and tagged. Notes changed in the last 10 minutes wait. Tags you've written are never changed, and each note is tried once."
      - "Last pass:" `ToAppTimeZone(...).ToString("f")`, or "not yet" (if running: "running now").
      - "Tagged since startup: N".
      - If `UnavailableReason` is set: `<MudAlert Severity="Severity.Warning" Dense="true">` "Ollama unavailable since {time}: {reason}".
    - In `OnInitializedAsync`'s admin branch, take `_autoTagStatus = AutoTagStatus.Snapshot;` so the value is only read for admins, matching the existing rule.
    - No buttons. Grep `MudBlazor.xml` if unsure of any parameter.
31. Run `dotnet build JNotes.slnx`. `ValidateOnBuild` in Development must resolve the hosted service and `NoteAutoTagger`.

### Phase 5: config and docs
32. `docker-compose.yml`, `jnotes.environment`:
    - Remove the `AUTO_TAG_ENABLED` line.
    - Rewrite the comment: "Optional background AI auto-tagging with Ollama. Runs only when OLLAMA_URL is set: untagged notes are tagged every AUTO_TAG_INTERVAL_MINUTES, up to AUTO_TAG_BATCH_SIZE per pass. Ollama on the host: OLLAMA_URL=http://host.docker.internal:11434. Bundled service: COMPOSE_PROFILES=ollama and OLLAMA_URL=http://ollama:11434. See README 'Auto-tagging'."
    - Add `- AUTO_TAG_INTERVAL_MINUTES=${AUTO_TAG_INTERVAL_MINUTES:-10}` and `- AUTO_TAG_BATCH_SIZE=${AUTO_TAG_BATCH_SIZE:-10}`.
    - Keep `extra_hosts`. In the `ollama` service comment, change "used only by auto-tagging" to background auto-tagging.
33. README, CLAUDE.md, and the superseded note: see Docs.

## Tests to add or update
Unit tests need no DB: `dotnet test tests/JNotes.Tests --filter "FullyQualifiedName~Tagging"`.
- `AutoTagOptionsTests`:
  - `IsEnabled` theory: (null, phi4) → false; ("o:11434", phi4) → false; ("http://o:11434", phi4) → true; ("http://o:11434", "  ") → false; ("ftp://o", phi4) → false.
  - `ParseIntervalMinutes`: null/""/"abc"/"0"/"-5" → 10; "1" → 1; "30" → 30; "5000" → 1440.
  - `ParseBatchSize`: null/"0" → 10; "3" → 3; "500" → 100.
  - The existing `ParseTimeout` test.
  - `AddJNotesCore_reads_the_env_keys`: only `OLLAMA_URL`, model, timeout, interval and batch set → enabled with those values.
  - `AUTO_TAG_ENABLED_is_ignored`: `AUTO_TAG_ENABLED=false` plus a valid URL → `IsEnabled` true.
  - No keys → disabled, phi4, 10/10.
- `OllamaTagModelClientTests`: assert `Kind`:
  - 404 model → `Unavailable` (message still contains `ollama pull phi4`)
  - 500 and 503 → `Unavailable`
  - 400 → `BadResponse`
  - `HttpRequestException` → `Unavailable`
  - timeout → `Timeout`
  - garbage content and 200+`error` → `BadResponse`
  - cancelled caller → `OperationCanceledException` unwrapped (existing)
- `AutoTagResponseParserTests`: the failure theory also asserts `Kind == BadResponse`.
- `AutoTagScheduleTests`: `RetryDelay(1..4)` = 1h/6h/24h/72h; `AfterFailure(1, now)` = (Failed, now+1h); `AfterFailure(4, now)` = (Failed, now+72h); `AfterFailure(5, now)` = (GaveUp, null); `MinNoteAge` = 10 min.
- `AutoTagCandidatesTests`: [A:a1,a2,a3], [B:b1] with max 10 → a1,b1,a2,a3; max 2 → a1,b1; empty input → empty; a user with no ids is skipped; max 0 → empty.
- `FileSystemNoteFileStoreTests` (existing class):
  - `ReadRawAsync` returns the exact written text; a missing note → `NoteNotFoundException`.
  - `ReplaceIfUnchangedAsync`: unchanged → true and the file equals `Serialize(replacement)`; different expected text → false and the file bytes are untouched; missing file → false; another user's id → false (the path is per user).

Integration (PostgreSQL): `tests/JNotes.Tests/Integration/NoteAutoTaggerTests.cs : IAsyncLifetime`, one fixture per class.
- Fixture changes (`CoreServicesFixture`):
  - Register `var clock = new TestTimeProvider(); services.AddSingleton<TimeProvider>(clock);` after `AddJNotesCore`; the last registration wins over `TryAdd`.
  - Expose `Clock` and `public INoteAutoTagger NoteAutoTagger => _scope.ServiceProvider.GetRequiredService<INoteAutoTagger>();` and `public IAutoTagStateRepository AutoTagState => …`.
  - Keep `OllamaUrl = "http://ollama.test:11434"`.
- `TestDoubles/TestTimeProvider.cs`: `internal sealed class TestTimeProvider : TimeProvider { private TimeSpan _offset; public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + _offset; public void Advance(TimeSpan by) => _offset += by; }`.
- `RecordingTagModelClient`: add `public Func<AutoTagPrompt, Task>? OnCall { get; set; }`, awaited before throw or return; add `Reset()` (clears `NextTags`, `Throw`, `OnCall`, `CallCount`); update the doc.
- Common setup: create notes with `NoteService.CreateAsync` (acting as A or B), then `Clock.Advance(TimeSpan.FromMinutes(1))`, and call `TagUserNotesAsync(userId, 10, TimeSpan.Zero)` / `TagAllUsersAsync(10, TimeSpan.Zero)`.
- Cases:
  1. **Tags an untagged note.** The model returns `["Machine Learning", "#AI", "note"]`.
     - The file has tags `machine-learning, ai`.
     - `GetAsync` `UpdatedAt` equals the value before tagging.
     - `ListAsync` shows the tags.
     - `SuggestTagsAsync("machine", …)` returns it (`NoteIndexUserTags` updated).
     - `Reconciler.RescanAsync(A)` afterwards reports `Updated == 0`.
     - The attempt row is `Tagged`, and the result has `Tagged == 1`.
  2. **Only the tags change.** The raw text after the second `---\n` delimiter is byte-identical before and after. Split the text before and after, and compare the body suffixes and the non-`tags` front-matter lines.
  3. **Tagged notes are untouched.** A note with `["x"]` → `CallCount == 0` and the file bytes are identical.
  4. **Per-user isolation.**
     - B has a note tagged `secret-b`, and A has an untagged note. `TagUserNotesAsync(A)` → `LastPrompt.UserMessage` doesn't contain `secret-b`, and B's untagged note is untouched.
     - `TagAllUsersAsync` tags both users' untagged notes, each with its own vocabulary spelling.
  5. **Vocabulary spelling.** A has a `Work` tag and the model returns `work` → the note gets `Work`.
  6. **Edited during the model call.** `OnCall` rewrites the file via `FileStore.WriteAsync(A, note with body "changed")`.
     - The file keeps "changed" with no tags, there is no attempt row, and `Deferred == 1`.
     - A second run (clear `OnCall`) tags it.
  7. **BadResponse.** `Throw = new AutoTagException("x", BadResponse)`.
     - The file is untouched and the row is `Failed` with `FailureCount == 1` and `RetryAfterUtc ≈ now+1h`.
     - An immediate rerun → `CallCount` is unchanged.
     - After `Clock.Advance(61 min)` → it is retried.
  8. **Gives up.** Five failures, advancing the clock past each delay → the row is `GaveUp`; advancing 100 days → never retried.
  9. **Unavailable.** Three candidates and `Throw` with `Unavailable` → `CallCount == 1`, `UnavailableReason` is set, and no attempt rows exist. After clearing `Throw`, a rerun tags all three.
  10. **Timeout.** Two candidates and a `Timeout` throw → `CallCount == 1`, the first note's row is `Failed`, and the pass stopped.
  11. **NoTags.** The model returns `["note"]` → the row is `NoTags`, the file is untouched, and a rerun doesn't call the model.
  12. **Removed tags stay removed.** After `Tagged`, `NoteService.UpdateAsync` with `Tags = []`; a rerun → `CallCount` unchanged and the note stays untagged.
  13. **Too recent.** `minAge` = 10 min on a just-created note (no clock advance) → not called.
  14. **Non-canonical file.** Write the file by hand with an extra front-matter line `aliases: [x]` (or CRLF line endings), then `Reconciler.RescanAsync(A)` → `Skipped`, file bytes identical, `CallCount == 0`.
  15. **Batch cap.** Three candidates with `maxNotes: 2` → `CallCount == 2`.
  16. **Disabled.** Constructing `NoteAutoTagger` with `Options.Create(new AutoTagOptions())` → `InvalidOperationException`.
  17. **Cascade.** Deleting user B through `IUserManagementService` removes B's attempt rows. Optional: only if cheap via `Scope`.

Commands:
- `dotnet test tests/JNotes.Tests --filter "FullyQualifiedName~NoteAutoTaggerTests"` (needs PostgreSQL)
- `dotnet test JNotes.slnx`
- `dotnet format JNotes.slnx --verify-no-changes`

## Docs
- `README.md`:
  - Features bullet: "**Auto-tagging (optional)** — when `OLLAMA_URL` points at an [Ollama](https://ollama.com) server, a background task tags notes that have no tags using a local model (Microsoft's `phi4` by default). It never changes tags you've written."
  - Project-layout interface list: replace `INoteAutoTagService` with `INoteAutoTagger`.
  - Configuration table: delete `AUTO_TAG_ENABLED`. The `OLLAMA_URL` row: "enables background auto-tagging; must be an absolute http(s) URL, otherwise a warning is logged and it stays off; every user's untagged note text is sent here". Add rows for `AUTO_TAG_INTERVAL_MINUTES` (10, 1–1440) and `AUTO_TAG_BATCH_SIZE` (10, 1–100, notes per pass across all users).
  - Rewrite "Auto-tagging with Ollama (optional)" to explain what gets tagged:
    - untagged notes only, 1–5 tags, reusing your existing tag spelling
    - notes left unchanged for 10 min
    - newest first; existing notes are backfilled gradually
    - each note tried once; failures retried after 1 h/6 h/24 h/72 h, then given up
    - "Last updated" and list order don't change
    - notes you edited outside JNotes in a non-standard format are skipped
    - removing auto-added tags is permanent
    - an editor left open more than 10 min on an untagged note can overwrite the new tags
  - Keep the host, bundled-container and privacy paragraphs, with `.env` samples that drop `AUTO_TAG_ENABLED`. "Running locally" becomes `$env:OLLAMA_URL="http://localhost:11434"`.
  - Mention the Settings → Administration "Auto-tagging" status card, and that Ollama unloads the model after 5 idle minutes, so each pass may start with a cold load that counts toward `OLLAMA_TIMEOUT_SECONDS`.
  - Privacy: automatic, all users, admin-controlled; there is no per-user opt-out.
- `CLAUDE.md`:
  - Line 43: replace `INoteAutoTagService` with `INoteAutoTagger`.
  - Rewrite "## Auto-tagging (optional)" (about 15 lines) with:
    - the enable rule (URL only; `AUTO_TAG_ENABLED` ignored with a warning; `appsettings.json` ships `OLLAMA_URL` empty so dev and tests never auto-enable)
    - the layering (pure helpers; `OllamaTagModelClient` with `AutoTagFailureKind` and `RemoveAllLoggers`; `NoteAutoTagger` with explicit userId driven by `Web/Tagging/AutoTagBackgroundService`, the per-pass pattern of the image cleanup)
    - the `NoteAutoTagAttempts` table: **not derived, not `NoteIndex*`**; one final attempt per note; the backoff table; failure kinds and pass abort
    - the write safety rules: `MinNoteAge`, canonical-format guard, `ReplaceIfUnchangedAsync` compare-and-swap, file then index then attempt, `UpdatedAt` preserved and `IndexedFileWriteTimeUtc` = post-write mtime so the reconciler skips it
    - the known idle-editor overwrite
    - the Settings status card (in-memory, resets on restart, no run-now)
    - the logging rules
    - compose (unchanged)
  - Tests paragraph (lines 569–571): "It likewise swaps in `RecordingTagModelClient` (with an `OnCall` hook) and a `TestTimeProvider` and sets `OLLAMA_URL`, so `NoteAutoTagger` runs without Ollama and backoff is testable. No hosted services run in tests."
  - Storage section: add a sentence that `NoteAutoTagAttempts` is the one non-`NoteIndex*` note-related table and why.
- `docs/plans/ollama-auto-tagging.md`: insert at the top: "> **Superseded** by the background auto-tagging redesign (the editor button was removed; see CLAUDE.md 'Auto-tagging'). Kept for history." Don't rewrite the rest.

## Manual verification (for the tester)
Prerequisites:
- PostgreSQL: `docker run --rm -p 5432:5432 -e POSTGRES_USER=jnotes -e POSTGRES_PASSWORD=jnotes -e POSTGRES_DB=jnotes postgres:18-alpine`
- An admin user and a Member user.
- Build and checks: `dotnet build JNotes.slnx`, `dotnet test JNotes.slnx`, `dotnet format JNotes.slnx --verify-no-changes`.

Prepare notes **at least 10 minutes before the checks that expect tagging** (the tagger ignores anything changed in the last 10 minutes):
- As admin: two untagged text notes ("Kubernetes upgrade plan" with a paragraph, and "Groceries"), one untagged checklist, one note tagged `Work`, and one note tagged `Testing`.
- As the Member: one untagged note.
- Record each note's "Last updated" and its position in `/`.
- Copy one untagged note's `.md` from `src/JNotes.Web/App_Data/notes/{userId}/` for a later byte comparison.

**Stub Ollama (no real Ollama needed).** Save as `C:\Users\Joel\AppData\Local\Temp\claude\C--Data-Code-JNotes\e19dfd44-9f52-4bcb-b73d-5f662bee57f0\scratchpad\ollama-stub.ps1` and run `powershell -File <path> -Mode ok` in its own window. Close the window to stop it.
```powershell
param([string]$Mode = "ok", [int]$Port = 11500)
$l = [System.Net.HttpListener]::new(); $l.Prefixes.Add("http://localhost:$Port/"); $l.Start()
Write-Host "Ollama stub http://localhost:$Port/ mode=$Mode"
while ($l.IsListening) {
  $c = $l.GetContext(); $null = [IO.StreamReader]::new($c.Request.InputStream).ReadToEnd()
  Write-Host "$(Get-Date -Format T) $($c.Request.HttpMethod) $($c.Request.Url.AbsolutePath)"
  $s = 200
  switch ($Mode) {
    "ok"      { $b = '{"model":"phi4","created_at":"2026-01-01T00:00:00Z","message":{"role":"assistant","content":"{\"tags\":[\"Stub Topic\",\"#Testing\",\"note\"]}"},"done":true,"done_reason":"stop"}' }
    "empty"   { $b = '{"model":"phi4","message":{"role":"assistant","content":"{\"tags\":[]}"},"done":true}' }
    "garbage" { $b = '{"model":"phi4","message":{"role":"assistant","content":"This note is about cats."},"done":true}' }
    "error500"{ $s = 500; $b = '{"error":"llama runner process has terminated"}' }
    "nomodel" { $s = 404; $b = '{"error":"model \"phi4\" not found, try pulling it first"}' }
    "slow"    { Start-Sleep -Seconds 30; $b = '{"model":"phi4","message":{"role":"assistant","content":"{\"tags\":[\"slow\"]}"},"done":true}' }
  }
  $bytes = [Text.Encoding]::UTF8.GetBytes($b); $c.Response.StatusCode = $s; $c.Response.ContentType = "application/json"
  $c.Response.OutputStream.Write($bytes, 0, $bytes.Length); $c.Response.Close()
}
```
In "ok" mode, the expected normalised tags are `stub-topic` and `Testing`: the user's `Testing` spelling is reused and `note` is dropped.

Checks. Run the app with `dotnet run --project src/JNotes.Web` from PowerShell, setting env vars first.
1. **Off by default.** With no env vars:
   - The startup log has no auto-tag Info line.
   - `/notes/{id}/edit` and `/notes/new/text` show **no** Auto-tag button, and the layout under the tag input is intact.
   - Settings → Administration → "Auto-tagging" card says "Off. Set OLLAMA_URL…".
   - After 3 minutes, no note has changed.
2. **Invalid URL.** `$env:OLLAMA_URL="localhost:11500"` → warning "OLLAMA_URL is set but isn't an absolute http(s) URL…". The URL isn't printed, and the feature is off.
3. **Deprecated flag.** `$env:AUTO_TAG_ENABLED="false"; $env:OLLAMA_URL="http://localhost:11500"` → warning "AUTO_TAG_ENABLED is no longer used…", and the feature is **on** (Info line with model, interval and batch). Remove `AUTO_TAG_ENABLED` afterwards.
4. **Happy path.** Stub `-Mode ok`, `$env:OLLAMA_URL="http://localhost:11500"; $env:AUTO_TAG_INTERVAL_MINUTES="1"`, then start.
   - About 1 minute later the stub prints a `POST /api/chat` line for each untagged note, one at a time. The app log shows "Auto-tag: N tagged…" and **no** `http://localhost:11500` anywhere; grep the console output for `11500`.
   - The untagged notes (including the checklist and the Member's note) now show `stub-topic` and `Testing`.
   - The notes tagged `Work`/`Testing` are unchanged.
   - List order on `/` and each note's "Last updated" are unchanged.
   - Diff the copied `.md` with the new one: only the `tags:` lines differ, and `updated:` is identical.
   - The Tag filter lists `stub-topic`, and the tag input autocompletes it.
   - The Settings card shows the last pass time and "Tagged since startup: N".
5. **One attempt per note.** Remove the tags from an auto-tagged note in the editor, leave the editor, and wait 2+ intervals: the note stays untagged, and the stub gets no new requests for it.
6. **New notes wait.** Create a new untagged note: it isn't tagged for about 10 minutes, then gets tagged on the next pass. Editing it keeps pushing that back.
7. **Open editor.** Open an old untagged note's editor, leave it idle and let a pass tag it, then type in the body. After autosave the auto-tags are gone and the body edit is saved. No error bar or crash. This is the documented limitation.
8. **Ollama down.** Stop the stub, then create or keep untagged notes older than 10 min.
   - Exactly **one** Warning "Auto-tagging paused: The tagging service couldn't be reached. …", with no URL and no stack trace.
   - No further warnings on later passes.
   - The Settings card shows "Ollama unavailable since …".
   - Restart the stub → Info "Auto-tagging resumed.", and the notes get tagged.
9. **Error modes.** Restart the stub with each mode against fresh old untagged notes:
   - `nomodel` → a paused warning containing `ollama pull phi4`, and notes untouched.
   - `error500` → paused, notes untouched.
   - `garbage` → the pass continues note by note, the log counts "failed", and notes stay untagged. After switching to `ok`, they are not retried within 1 hour. Optional: confirm via `psql` `SELECT "Outcome","FailureCount","RetryAfterUtc" FROM "NoteAutoTagAttempts";`.
   - `empty` → counted "without suggestions" and never retried.
   - `slow` with `$env:OLLAMA_TIMEOUT_SECONDS="10"` → one paused "timed out" warning, and one note recorded as failed.
10. **Batch cap.** With `$env:AUTO_TAG_BATCH_SIZE="1"`, at most one request per pass.
11. **Hand-edited file skipped.** Add `aliases: [x]` to an untagged note's front matter on disk, then Settings → Rescan Now. That note is never sent (no stub request) and its bytes are unchanged.
12. **Shutdown.** Ctrl+C during a `slow` request: the app stops within a few seconds with no error log.
13. **Member.** Signed in as the Member, `/settings` has no Auto-tagging card.
14. **Docker (optional).**
    - `docker compose config` shows `OLLAMA_URL=`, `AUTO_TAG_INTERVAL_MINUTES=10`, `AUTO_TAG_BATCH_SIZE=10` and no `AUTO_TAG_ENABLED`.
    - With `.env` `OLLAMA_URL=http://host.docker.internal:11500` and the stub bound to all interfaces (change the prefix to `http://+:11500/`, which needs an admin shell or a urlacl), `docker compose up --build` repeats check 4.
    - Plain `docker compose up` without the profile doesn't start `ollama`.
15. **Real Ollama + phi4 (if available).** `ollama pull phi4`, `$env:OLLAMA_URL="http://localhost:11434"`.
    - The first pass may take 30–120 s per note while the model loads.
    - Tags are sensible lowercase kebab-case, at most 5, and existing tag spellings are reused.
    - No timeouts at the default 120 s on this machine; if there are some, raise `OLLAMA_TIMEOUT_SECONDS` and note it.

## Risks and edge cases
- **Idle open editor overwrites auto-tags** (check 7). The note is marked `Tagged` and isn't redone. This is documented; the 10-min quiet period keeps it rare.
- **CAS lock.** `FileShare.None` is held for milliseconds. A concurrent editor autosave in that window gets an `IOException`, shows "Autosave failed", and retries on the next edit. On Linux, .NET emulates `FileShare` with advisory locks between .NET streams, which is **UNVERIFIED** for this exact case. Either way the content compare still blocks stale writes except in a sub-millisecond window.
- **Canonical guard assumes `Serialize(Deserialize(raw)) == raw` for app-written files.** YamlDotNet DateTime round-trip formatting is **UNVERIFIED**. Integration test 1 proves or disproves it. If it fails, don't weaken the guard silently; stop and report.
- **The "user deliberately removed tags" case can't be detected** for a note that was never auto-attempted (removed before the feature existed). Such notes get tagged once.
- **Backfill cost.** Thousands of notes take hours or days at 60/hour and keep the GPU or CPU busy. Tunable via interval and batch.
- **Privacy.** Every user's untagged note text goes to `OLLAMA_URL` automatically, with no per-user opt-out.
- **Truncating `NoteAutoTagAttempts`** re-attempts every untagged note once, including ones whose tags a user removed.
- **Stale index.** A `ContentOnly` edit doesn't bump the index `UpdatedAt`. The file mtime check covers the quiet period, but a note could hold a batch slot for one pass (Deferred).
- **Ollama 503 overload** with other users of the same server pauses the pass until the next interval. That's acceptable.
- **Multiple app instances** against one DB would run parallel passes and race `RecordFailureAsync`. Single-instance is assumed (as for the other hosted services).
- Status card data is in-memory: it resets on restart and doesn't update live.

## Open questions
1. **Backfill existing untagged notes on first enable, or only notes created or edited after enabling?** Recommended default: **backfill** (throttled, newest first, additive only, no reorder). The alternative is a persisted "enabled since" watermark in `Settings`, following the image-cleanup "start the clock" precedent, which only considers notes with `UpdatedAt` after it.
2. **Per-user opt-out** (a Profile toggle) for sending note text to Ollama? Recommended default: **no**. It's admin-controlled, typically local, and documented. Add later if Members need it.
3. **Settings status card:** keep it (recommended: yes, read-only, no "Run now") or drop it for zero UI.

## Out of scope
- Do not change `NoteService`, `NoteSaveScope`, `NoteIndexReconciler`, `PostgresNoteIndexRepository` behaviour, `TagInput`, `TagList`, `AutoTagPromptBuilder`, `AutoTagNormalizer`, PDF export code, `BannedSymbols.txt`, or the `Note` / `NoteFrontMatter` models. Only the one doc sentence in `INoteIndexRepository` changes.
- No front-matter marker, no editor or note-view UI, no "Run now", no per-user settings, no admin-editable interval (env only), no `keep_alive` / streaming / auth headers for Ollama, and no GPU compose config.
- No pruning of attempt rows for purged notes (they are tiny and harmless). Cascade on user delete only.
- Don't rewrite `docs/plans/ollama-auto-tagging.md` beyond the superseded note.
