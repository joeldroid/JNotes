using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace JNotes.UiTests.Infrastructure;

/// <summary>
/// The real <c>Program.cs</c>, hosted in-process. Only logging is added here: application configuration cannot be
/// (Program reads some keys before <c>Build()</c>, which is when these callbacks run), so <see cref="UiAppFixture"/>
/// supplies it through process environment variables while the host starts.
/// </summary>
internal sealed class JNotesWebFactory(ServerErrorLog errors) : WebApplicationFactory<global::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureLogging(logging =>
        {
            // Quiet console (EF logs every query at Information); the Error-and-above provider below sees everything that matters.
            logging.SetMinimumLevel(LogLevel.Warning);
            logging.AddProvider(new ServerErrorLogProvider(errors));
        });
}
