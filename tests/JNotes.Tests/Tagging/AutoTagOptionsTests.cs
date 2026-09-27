using JNotes.Core;
using JNotes.Core.Tagging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace JNotes.Tests.Tagging;

public class AutoTagOptionsTests
{
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
    [InlineData(null, 10)]
    [InlineData("", 10)]
    [InlineData("abc", 10)]
    [InlineData("0", 10)]
    [InlineData("-5", 10)]
    [InlineData("1", 1)]
    [InlineData("30", 30)]
    [InlineData(" 30 ", 30)]
    [InlineData("5000", 1440)]
    public void ParseIntervalMinutes_clamps_and_falls_back_to_the_default(string? value, int expected)
    {
        Assert.Equal(expected, AutoTagOptions.ParseIntervalMinutes(value));
    }

    [Theory]
    [InlineData(null, 10)]
    [InlineData("", 10)]
    [InlineData("abc", 10)]
    [InlineData("0", 10)]
    [InlineData("3", 3)]
    [InlineData("500", 100)]
    public void ParseBatchSize_clamps_and_falls_back_to_the_default(string? value, int expected)
    {
        Assert.Equal(expected, AutoTagOptions.ParseBatchSize(value));
    }

    [Theory]
    [InlineData(null, "phi4", false)]
    [InlineData("o:11434", "phi4", false)]
    [InlineData("http://o:11434", "phi4", true)]
    [InlineData("http://o:11434", "  ", false)]
    [InlineData("ftp://o", "phi4", false)]
    public void IsEnabled_needs_a_valid_url_and_a_model(string? url, string model, bool expected)
    {
        var options = new AutoTagOptions { OllamaUrl = url, Model = model };

        Assert.Equal(expected, options.IsEnabled);
    }

    [Fact]
    public void AddJNotesCore_reads_the_env_keys()
    {
        var options = Resolve(new Dictionary<string, string?>
        {
            ["OLLAMA_URL"] = "http://o:11434",
            ["OLLAMA_MODEL"] = "phi4-mini",
            ["OLLAMA_TIMEOUT_SECONDS"] = "45",
            ["AUTO_TAG_INTERVAL_MINUTES"] = "30",
            ["AUTO_TAG_BATCH_SIZE"] = "3",
        });

        Assert.True(options.IsEnabled);
        Assert.Equal("phi4-mini", options.Model);
        Assert.Equal("http://o:11434/", options.OllamaBaseUri?.AbsoluteUri);
        Assert.Equal(TimeSpan.FromSeconds(45), options.Timeout);
        Assert.Equal(30, options.IntervalMinutes);
        Assert.Equal(3, options.BatchSize);
    }

    [Fact]
    public void AUTO_TAG_ENABLED_is_ignored()
    {
        var options = Resolve(new Dictionary<string, string?>
        {
            ["AUTO_TAG_ENABLED"] = "false",
            ["OLLAMA_URL"] = "http://o:11434",
        });

        Assert.True(options.IsEnabled);
    }

    [Fact]
    public void AddJNotesCore_with_no_keys_is_disabled_with_the_defaults()
    {
        var options = Resolve([]);

        Assert.False(options.IsEnabled);
        Assert.Equal("phi4", options.Model);
        Assert.Equal(AutoTagOptions.DefaultTimeout, options.Timeout);
        Assert.Equal(10, options.IntervalMinutes);
        Assert.Equal(10, options.BatchSize);
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
