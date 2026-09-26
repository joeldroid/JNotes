namespace JNotes.Core.Export;

/// <summary>The PDF service was unreachable, timed out, or returned an error. Details go to the log, not the user.</summary>
public sealed class PdfRenderingException(string message, Exception? inner = null) : Exception(message, inner);
