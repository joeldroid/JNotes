using JNotes.Core.Tagging;

namespace JNotes.Tests.Tagging;

public class AutoTagResponseParserTests
{
    [Fact]
    public void Parses_the_tags_object()
    {
        Assert.Equal(["a", "b"], AutoTagResponseParser.Parse("""{"tags":["a","b"]}"""));
    }

    [Theory]
    [InlineData("```json\n{\"tags\":[\"a\",\"b\"]}\n```")]
    [InlineData("```\n{\"tags\":[\"a\",\"b\"]}\n```")]
    [InlineData("  ```JSON {\"tags\":[\"a\",\"b\"]} ```  ")]
    public void Parses_a_fenced_reply(string content)
    {
        Assert.Equal(["a", "b"], AutoTagResponseParser.Parse(content));
    }

    [Fact]
    public void Parses_a_bare_array()
    {
        Assert.Equal(["a", "b"], AutoTagResponseParser.Parse("""["a","b"]"""));
    }

    [Fact]
    public void Skips_non_string_elements()
    {
        Assert.Equal(["a", "c"], AutoTagResponseParser.Parse("""{"tags":["a",1,null,"c",{"x":1}]}"""));
    }

    [Theory]
    [InlineData("""{"foo":1}""")]
    [InlineData("""{"tags":"a"}""")]
    [InlineData("not json")]
    [InlineData("\"just a string\"")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Anything_else_throws_AutoTagException(string? content)
    {
        var ex = Assert.Throws<AutoTagException>(() => AutoTagResponseParser.Parse(content));

        Assert.Equal(AutoTagFailureKind.BadResponse, ex.Kind);
    }
}
