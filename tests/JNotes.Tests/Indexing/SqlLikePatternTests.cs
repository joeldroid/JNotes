using JNotes.Core.Indexing;

namespace JNotes.Tests.Indexing;

public class SqlLikePatternTests
{
    [Theory]
    [InlineData("needle", "%needle%")]
    [InlineData("100%", "%100\\%%")]
    [InlineData("snake_case", "%snake\\_case%")]
    [InlineData("back\\slash", "%back\\\\slash%")]
    [InlineData("a\\%b", "%a\\\\\\%b%")] // backslash escaped first — the ordering guard
    [InlineData("", "%%")]
    [InlineData("  spaced  ", "%  spaced  %")] // no trimming — Contains never trimmed
    public void ContainsPattern_escapes_wildcards_and_wraps_in_percent_signs(string input, string expected)
    {
        Assert.Equal(expected, SqlLikePattern.ContainsPattern(input));
    }
}
