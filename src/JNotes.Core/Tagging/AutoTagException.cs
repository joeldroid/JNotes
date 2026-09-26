namespace JNotes.Core.Tagging;

/// <summary>Ollama was unreachable, timed out, errored or returned garbage. Details go to the log, not the user.</summary>
public sealed class AutoTagException(string message, Exception? inner = null) : Exception(message, inner);
