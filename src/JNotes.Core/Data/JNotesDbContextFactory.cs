using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace JNotes.Core.Data;

/// <summary>
/// Lets `dotnet ef` commands (migrations add/update) construct a <see cref="JNotesDbContext"/>
/// standalone, without spinning up the full Web host/DI container. Only used at design time —
/// the app itself gets its connection string from <see cref="JNotesOptions"/> via DI (see
/// <c>ServiceCollectionExtensions.AddJNotesCore</c>).
/// </summary>
public sealed class JNotesDbContextFactory : IDesignTimeDbContextFactory<JNotesDbContext>
{
    public JNotesDbContext CreateDbContext(string[] args)
    {
        // `dotnet ef migrations add` never connects, so any well-formed string works; reading the
        // POSTGRES_* env vars first means `dotnet ef database update` can still reach a real database
        // if you want it to (the app migrates itself at startup, so you normally don't need to).
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "localhost",
            Port = int.TryParse(Environment.GetEnvironmentVariable("POSTGRES_PORT"), out var port) ? port : 5432,
            Database = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "jnotes",
            Username = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "jnotes",
            Password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "jnotes",
        }.ConnectionString;

        var optionsBuilder = new DbContextOptionsBuilder<JNotesDbContext>()
            .UseNpgsql(connectionString);

        return new JNotesDbContext(optionsBuilder.Options);
    }
}
