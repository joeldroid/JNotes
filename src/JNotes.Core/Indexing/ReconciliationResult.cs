namespace JNotes.Core.Indexing;

public sealed record ReconciliationResult(int Added, int Updated, int Removed, IReadOnlyList<string> Errors);
