using JNotes.Core;
using JNotes.Core.Tagging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace JNotes.Tests.Tagging;

public class AutoTagOptionsTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("nope", false)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData(" 1 ", true)]
    [InlineData("yes", true)]
    [InlineData("on", true)]
    public void ParseFlag_enables_only_on_truthy_words(string? value, bool expected)
    {
        Assert.Equal(expected, AutoTagOptions.ParseFlag(value));
    }

    [Theory]
    [InlineData(null, 120)]
    [InlineData("", 120)]
    [InlineData("abc", 120)]
    [InlineData("5", 10)]
    [InlineData("9999", 600)]
    [InlineData("30", 30)]
    public void ParseTimeout_clamps_and_falls_back_to_the_default(string? value, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), AutoTagOptions.ParseTimeout(value));
    }

    [Theory]
    [InlineData(false, "http://o:11434", "phi4", false)]
    [InlineData(true, null, "phi4", false)]
    [InlineData(true, "o:11434", "phi4", false)]
    [InlineData(true, "http://o:11434", "phi4", true)]
    [InlineData(true, "http://o:11434", "  ", false)]
    public void IsEnabled_needs_the_flag_a_valid_url_and_a_model(bool enabled, string? url, string model, bool expected)
    {
        var options = new AutoTagOptions { Enabled = enabled, OllamaUrl = url, Model = model };

        Assert.Equal(expected, options.IsEnabled);
    }

    [Fact]
    public void AddJNotesCore_reads_the_env_keys()
    {
        var options = Resolve(new Dictionary<string, string?>
        {
            ["AUTO_TAG_ENABLED"] = "true",
            ["OLLAMA_URL"] = "http://o:11434",
            ["OLLAMA_MODEL"] = "phi4-mini",
            ["OLLAMA_TIMEOUT_SECONDS"] = "45",
        });

        Assert.True(options.IsEnabled);
        Assert.Equal("phi4-mini", options.Model);
        Assert.Equal("http://o:11434/", options.OllamaBaseUri?.AbsoluteUri);
        Assert.Equal(TimeSpan.FromSeconds(45), options.Timeout);
    }

    [Fact]
    public void AddJNotesCore_with_no_keys_is_disabled_with_the_default_model()
    {
        var options = Resolve([]);

        Assert.False(options.IsEnabled);
        Assert.Equal("phi4", options.Model);
        Assert.Equal(AutoTagOptions.DefaultTimeout, options.Timeout);
    }

    private static AutoTagOptions Resolve(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddJNotesCore(configuration);

        using var provider = services.BuildServiceProvider();

        return provider.GetRequiredService<IOptions<AutoTagOptions>>().Value;
    }
}
