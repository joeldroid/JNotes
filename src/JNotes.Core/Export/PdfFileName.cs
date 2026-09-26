using System.Text;

namespace JNotes.Core.Export;

/// <summary>Turns a note title into a safe <c>.pdf</c> file name for the download/save dialog.</summary>
public static class PdfFileName
{
    private const int MaxStemLength = 100;
    private const string Fallback = "Untitled note";
    private const string InvalidChars = "\\/:*?\"<>|";

    public static string FromTitle(string? title)
    {
        var builder = new StringBuilder(title?.Length ?? 0);
        foreach (var c in title ?? "")
        {
            builder.Append(InvalidChars.Contains(c) || char.IsControl(c) ? ' ' : c);
        }

        var stem = Tidy(CollapseWhitespace(builder.ToString()));
        if (stem.Length > MaxStemLength)
        {
            stem = stem[..MaxStemLength];
            if (char.IsHighSurrogate(stem[^1]))
            {
                stem = stem[..^1];
            }

            stem = Tidy(stem);
        }

        return (stem.Length == 0 ? Fallback : stem) + ".pdf";
    }

    private static string Tidy(string value) => value.Trim(' ', '.');

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasSpace = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!previousWasSpace)
                {
                    builder.Append(' ');
                }

                previousWasSpace = true;
            }
            else
            {
                builder.Append(c);
                previousWasSpace = false;
            }
        }

        return builder.ToString();
    }
}
