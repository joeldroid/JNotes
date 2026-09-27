using JNotes.Core;
using JNotes.Core.Data;
using JNotes.Core.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace JNotes.Tests.Startup;

/// <summary>Pure unit tests (no database): how environment-style flat keys become <see cref="JNotesOptions"/>.</summary>
public class JNotesOptionsTests
{
    [Fact]
    public void All_postgres_settings_build_a_connection_string_with_the_default_port()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["POSTGRES_HOST"] = "h",
            ["POSTGRES_DB"] = "d",
            ["POSTGRES_USER"] = "u",
            ["POSTGRES_PASSWORD"] = "p",
        });

        var options = provider.GetRequiredService<IOptions<JNotesOptions>>().Value;

        Assert.Empty(options.GetMissingRequiredPostgresSettings());
        Assert.NotNull(options.PostgresConnectionString);
        Assert.Contains("Host=h", options.PostgresConnectionString);
        Assert.Contains("Database=d", options.PostgresConnectionString);
        Assert.Contains("Username=u", options.PostgresConnectionString);
        Assert.Contains("Port=5432", options.PostgresConnectionString);
    }

    [Fact]
    public void Missing_database_leaves_no_connection_string_and_is_reported()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["POSTGRES_HOST"] = "h",
            ["POSTGRES_USER"] = "u",
        });

        var options = provider.GetRequiredService<IOptions<JNotesOptions>>().Value;

        Assert.Null(options.PostgresConnectionString);
        Assert.Equal(["POSTGRES_DB"], options.GetMissingRequiredPostgresSettings());
    }

    [Fact]
    public void Whitespace_only_host_counts_as_missing()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["POSTGRES_HOST"] = "  ",
            ["POSTGRES_DB"] = "d",
            ["POSTGRES_USER"] = "u",
        });

        var options = provider.GetRequiredService<IOptions<JNotesOptions>>().Value;

        Assert.Null(options.PostgresConnectionString);
        Assert.Equal(["POSTGRES_HOST"], options.GetMissingRequiredPostgresSettings());
    }

    [Fact]
    public void No_keys_gives_the_code_defaults_and_reports_every_required_setting()
    {
        using var provider = BuildProvider([]);

        var options = provider.GetRequiredService<IOptions<JNotesOptions>>().Value;

        Assert.Equal("notes", options.NotesFolderPath);
        Assert.Equal("UTC", options.TimeZoneId);
        Assert.Equal(5, options.AutoIndexIntervalMinutes);
        Assert.Equal(["POSTGRES_HOST", "POSTGRES_DB", "POSTGRES_USER"], options.GetMissingRequiredPostgresSettings());
    }

    [Fact]
    public void Blank_notes_path_and_time_zone_keep_the_defaults()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["NOTES_PATH"] = " ",
            ["TZ"] = "",
        });

        var options = provider.GetRequiredService<IOptions<JNotesOptions>>().Value;

        Assert.Equal("notes", options.NotesFolderPath);
        Assert.Equal("UTC", options.TimeZoneId);
    }

    [Fact]
    public void Blank_oidc_display_name_keeps_the_default()
    {
        using var provider = BuildProvider(new Dictionary<string, string?> { ["OIDC_DISPLAY_NAME"] = "   " });

        Assert.Equal("OIDC", provider.GetRequiredService<IOptions<OidcOptions>>().Value.DisplayName);
    }

    [Fact]
    public void Creating_a_context_without_the_required_settings_names_them()
    {
        using var provider = BuildProvider([]);

        // Thrown from the options delegate (which runs while the factory is being resolved or first used),
        // before any connection is attempted.
        var exception = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IDbContextFactory<JNotesDbContext>>().CreateDbContext());
        Assert.Contains("POSTGRES_DB", exception.Message);
        Assert.Contains("POSTGRES_USER", exception.Message);
    }

    [Fact]
    public void A_connection_string_set_directly_bypasses_the_required_settings_check()
    {
        using var provider = BuildProvider([], services =>
            services.Configure<JNotesOptions>(o => o.PostgresConnectionString = "Host=x;Database=y;Username=z"));

        var factory = provider.GetRequiredService<IDbContextFactory<JNotesDbContext>>();

        using var context = factory.CreateDbContext();
        Assert.NotNull(context);
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> values, Action<IServiceCollection>? configure = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddJNotesCore(configuration);
        configure?.Invoke(services);

        return services.BuildServiceProvider();
    }
}
