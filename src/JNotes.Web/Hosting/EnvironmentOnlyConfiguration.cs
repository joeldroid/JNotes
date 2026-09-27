using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.Memory;

namespace JNotes.Web.Hosting;

/// <summary>
/// Makes environment variables the only configuration source. <c>WebApplication.CreateBuilder</c> would
/// otherwise silently load any <c>appsettings*.json</c> it finds (including stale copies in <c>bin/</c>, a
/// re-added file, or user secrets) and command-line args; removing those sources turns "environment only"
/// from a convention into a guarantee.
/// <para>
/// Deliberately <b>not</b> <c>Sources.Clear()</c>: <c>CreateBuilder</c> also registers the <c>DOTNET_</c>- and
/// <c>ASPNETCORE_</c>-prefixed environment providers (which supply <c>urls</c>/<c>http_ports</c>, so
/// <c>ASPNETCORE_URLS</c>, the base image's <c>ASPNETCORE_HTTP_PORTS</c> and a launch profile's
/// <c>applicationUrl</c> all work) plus an in-memory host-defaults source. Clearing and re-adding a bare
/// <c>AddEnvironmentVariables()</c> would lose the prefix stripping and break Kestrel binding in Docker.
/// So only <see cref="FileConfigurationSource"/> (JSON files, user secrets) and
/// <see cref="CommandLineConfigurationSource"/> are removed, leaving the environment providers in place.
/// </para>
/// <para>
/// The logging defaults move into code as the <b>lowest-priority</b> source (index 0), not
/// <c>builder.Logging.AddFilter</c>: code filters are applied after the configuration rules and would beat an
/// environment override such as <c>Logging__LogLevel__Microsoft.AspNetCore=Information</c>.
/// </para>
/// </summary>
internal static class EnvironmentOnlyConfiguration
{
    public static WebApplicationBuilder UseEnvironmentVariablesOnly(this WebApplicationBuilder builder)
    {
        var fileAndCommandLineSources = builder.Configuration.Sources
            .Where(source => source is FileConfigurationSource or CommandLineConfigurationSource)
            .ToList();
        foreach (var source in fileAndCommandLineSources)
        {
            builder.Configuration.Sources.Remove(source);
        }

        builder.Configuration.Sources.Insert(0, new MemoryConfigurationSource
        {
            InitialData = new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Information",
                ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
            },
        });

        return builder;
    }
}
