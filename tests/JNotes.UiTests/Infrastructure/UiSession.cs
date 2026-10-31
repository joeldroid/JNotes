using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace JNotes.UiTests.Infrastructure;

/// <summary>
/// One signed-in user in one test: its own browser context (cookie from the fixture's sign-in), a page, a trace, and a
/// watch on everything that should never happen in a healthy app - console errors, uncaught page errors, failed
/// requests, HTTP 4xx/5xx, server-side Error logs and a visible <c>#blazor-error-ui</c>. <see cref="RunAsync"/> turns
/// those into a test failure and, on any failure, saves a screenshot, trace and problem list.
/// </summary>
public sealed class UiSession : IAsyncDisposable
{
    // Problem lines look like "console.error: ...", "pageerror: ...", "requestfailed GET url net::ERR_X" and
    // "http 404: GET url"; an allow-list entry is a regex matched against one whole line.
    private static readonly string[] DefaultAllowed =
    [
        // A navigation cancels requests that were still in flight (e.g. the login forceLoad).
        @"requestfailed .* net::ERR_ABORTED",
    ];

    private readonly UiAppFixture _app;
    private readonly ITestOutputHelper _output;
    private readonly string _testName;
    private readonly int _serverErrorMark;
    private readonly List<string> _problems = [];
    private readonly List<Regex> _allowed = [.. DefaultAllowed.Select(p => new Regex(p, RegexOptions.CultureInvariant))];
    private readonly object _gate = new();

    private UiSession(UiAppFixture app, UiUser user, ITestOutputHelper output, string testName, IBrowserContext context, IPage page)
    {
        _app = app;
        User = user;
        _output = output;
        _testName = testName;
        Context = context;
        Page = page;
        _serverErrorMark = app.ServerErrors.Count;
    }

    public UiUser User { get; }

    public IBrowserContext Context { get; }

    public IPage Page { get; }

    public static async Task<UiSession> StartAsync(
        UiAppFixture app,
        UiUser user,
        ITestOutputHelper output,
        ViewportSize? viewport = null,
        [CallerMemberName] string testName = "")
    {
        var context = await app.Browser.NewContextAsync(new()
        {
            BaseURL = app.BaseUrl,
            StorageState = user.StorageState,
            ViewportSize = viewport,
        });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true });

        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(15_000);
        page.SetDefaultNavigationTimeout(30_000);

        var session = new UiSession(app, user, output, testName, context, page);
        session.Watch();
        return session;
    }

    private void Watch()
    {
        Page.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                Record($"console.error: {message.Text}");
            }
        };
        Page.PageError += (_, error) => Record($"pageerror: {error}");
        Page.RequestFailed += (_, request) => Record($"requestfailed {request.Method} {request.Url} {request.Failure}");
        Page.Response += (_, response) =>
        {
            if (response.Status >= 400)
            {
                Record($"http {response.Status}: {response.Request.Method} {response.Url}");
            }
        };
    }

    private void Record(string problem)
    {
        lock (_gate)
        {
            _problems.Add(problem);
        }
    }

    /// <summary>Tolerates a problem line (regex, matched against the whole line) for this test only.</summary>
    public void Allow(string regex)
    {
        lock (_gate)
        {
            _allowed.Add(new Regex(regex, RegexOptions.CultureInvariant));
        }
    }

    private List<string> UnexpectedProblems()
    {
        lock (_gate)
        {
            return _problems
                .Concat(_app.ServerErrors.Since(_serverErrorMark).Select(e => "server error log: " + e))
                .Where(p => !_allowed.Any(a => a.IsMatch(p)))
                .ToList();
        }
    }

    /// <summary>
    /// After a full document load, waits for the Blazor circuit to attach before the test clicks an @onclick handler
    /// (a click on a prerendered but not yet interactive page does nothing). Client-side navigations afterwards reuse
    /// the live circuit and need no wait.
    /// </summary>
    public async Task GotoInteractiveAsync(string path)
    {
        var frames = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = 0;
        var socket = Page.WaitForWebSocketAsync(new()
        {
            Predicate = ws =>
            {
                if (!ws.Url.Contains("/_blazor", StringComparison.Ordinal))
                {
                    return false;
                }

                // Subscribed in the predicate, which runs when the socket is created, so no frame can be missed.
                ws.FrameReceived += (_, _) =>
                {
                    if (Interlocked.Increment(ref received) >= 2)
                    {
                        frames.TrySetResult(); // handshake ack, then the first circuit traffic (render batch)
                    }
                };
                return true;
            },
        });

        await Page.GotoAsync(path);
        await socket;
        await frames.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    /// <summary>
    /// Runs the test body, then asserts every session stayed healthy. Any failure (body or health check) saves
    /// artifacts for each session and is rethrown.
    /// </summary>
    public static async Task RunAsync(Func<Task> body, params UiSession[] sessions)
    {
        try
        {
            await body();
            foreach (var session in sessions)
            {
                await session.AssertHealthyAsync();
            }

            foreach (var session in sessions)
            {
                await session.Context.Tracing.StopAsync(); // no path: the trace is discarded
            }
        }
        catch
        {
            await SaveArtifactsAsync(sessions);
            throw;
        }
    }

    private async Task AssertHealthyAsync()
    {
        await Assertions.Expect(Page.Locator("#blazor-error-ui")).ToBeHiddenAsync();

        var problems = UnexpectedProblems();
        Assert.True(problems.Count == 0, $"{User.Username}'s browser session hit problems:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }

    private static async Task SaveArtifactsAsync(UiSession[] sessions)
    {
        if (sessions.Length == 0)
        {
            return;
        }

        var first = sessions[0];
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var directory = Path.Combine(UiSettings.ArtifactsPath, $"{first._testName}-{stamp}");
        Directory.CreateDirectory(directory);

        foreach (var session in sessions)
        {
            var screenshot = Path.Combine(directory, $"{session.User.Username}-screenshot.png");
            var trace = Path.Combine(directory, $"{session.User.Username}-trace.zip");
            var problemsFile = Path.Combine(directory, $"{session.User.Username}-problems.txt");
            try
            {
                await session.Page.ScreenshotAsync(new() { Path = screenshot, FullPage = true });
            }
            catch (Exception ex)
            {
                session._output.WriteLine($"(no screenshot for {session.User.Username}: {ex.Message})");
            }

            try
            {
                await session.Context.Tracing.StopAsync(new() { Path = trace });
            }
            catch (Exception ex)
            {
                session._output.WriteLine($"(no trace for {session.User.Username}: {ex.Message})");
            }

            var text = new StringBuilder();
            text.AppendLine($"url: {session.Page.Url}");
            foreach (var problem in session.UnexpectedProblems())
            {
                text.AppendLine(problem);
            }

            await File.WriteAllTextAsync(problemsFile, text.ToString());
            session._output.WriteLine($"{session.User.Username} artifacts: {screenshot} | {trace} | {problemsFile}");
        }
    }

    public async ValueTask DisposeAsync() => await Context.CloseAsync();
}
