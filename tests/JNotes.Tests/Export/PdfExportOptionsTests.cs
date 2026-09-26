using JNotes.Core;
using JNotes.Core.Export;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace JNotes.Tests.Export;

public class PdfExportOptionsTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("gotenberg:3000", null)]
    [InlineData("ftp://x", null)]
    [InlineData("not a url", null)]
    [InlineData("http://gotenberg:3000", "http://gotenberg:3000/")]
    [InlineData("https://h/prefix", "https://h/prefix/")]
    public void ParseBaseUri_accepts_only_absolute_http_urls_and_ends_them_with_a_slash(string? value, string? expected)
    {
        Assert.Equal(expected, PdfExportOptions.ParseBaseUri(value)?.AbsoluteUri);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("nonsense", false)]
    [InlineData("http://gotenberg:3000", true)]
    public void IsEnabled_follows_whether_the_url_is_usable(string? url, bool expected)
    {
        Assert.Equal(expected, new PdfExportOptions { GotenbergUrl = url }.IsEnabled);
    }

    [Theory]
    [InlineData("http://g:3000", true)]
    [InlineData(null, false)]
    public void AddJNotesCore_reads_GOTENBERG_URL(string? url, bool expectedEnabled)
    {
        var values = new Dictionary<string, string?>();
        if (url is not null)
        {
            values["GOTENBERG_URL"] = url;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddJNotesCore(configuration);

        using var provider = services.BuildServiceProvider();

        Assert.Equal(expectedEnabled, provider.GetRequiredService<IOptions<PdfExportOptions>>().Value.IsEnabled);
    }
}
