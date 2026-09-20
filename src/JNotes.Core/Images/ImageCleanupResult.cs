namespace JNotes.Core.Images;

/// <summary>
/// What a cleanup did for one user: how many unreferenced images it deleted and how many bytes that
/// freed, plus how many unreferenced images it left alone only because they were too new (see
/// <see cref="ImageCleanupSchedule.MinImageAge"/>).
/// </summary>
public sealed record ImageCleanupResult(int Deleted, long BytesFreed, int SkippedRecent);

/// <summary><see cref="ImageCleanupResult"/> summed over every user, plus how many users' cleanups failed.</summary>
public sealed record AllUsersImageCleanupResult(int UsersCleaned, int UsersFailed, int Deleted, long BytesFreed, int SkippedRecent);
