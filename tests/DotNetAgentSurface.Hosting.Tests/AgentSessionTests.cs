using DotNetAgentSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotNetAgentSurface.Hosting.Tests;

public sealed class AgentSessionTests : IDisposable
{
    private readonly string _stateDirectory = Path.Combine(Path.GetTempPath(), $"agent-surface-session-{Guid.NewGuid():N}");
    private readonly string _appName = "sessiontests" + Guid.NewGuid().ToString("N")[..8];

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory))
        {
            Directory.Delete(_stateDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task State_persists_across_two_separate_cli_runs()
    {
        var first = await RunAsync(["cli", "--session", "flow", "select-target", "--name", "alpha"]);
        var second = await RunAsync(["cli", "--session", "flow", "current-target"]);
        var other = await RunAsync(["cli", "--session", "elsewhere", "current-target"]);

        Assert.Equal(0, first.ExitCode);
        Assert.Equal("alpha", second.Output);
        Assert.Equal("none", other.Output);
        Assert.True(File.Exists(Path.Combine(SessionsDirectory, "flow.json")));
    }

    [Fact]
    public async Task Session_is_saved_only_after_a_successful_invocation()
    {
        var failed = await RunAsync(["cli", "--session", "flow", "select-and-fail", "--name", "alpha"]);
        var current = await RunAsync(["cli", "--session", "flow", "current-target"]);

        Assert.Equal(1, failed.ExitCode);
        Assert.Equal("none", current.Output);
    }

    [Fact]
    public async Task Session_id_prefers_the_flag_then_the_environment_variable_then_the_working_directory()
    {
        var variable = _appName.ToUpperInvariant() + "_SESSION";
        var workingDirectory = Path.Combine(_stateDirectory, "workspace");
        void Configure(AgentSurfaceApplication app) => app.ConfigureSession(options => options.WorkingDirectory = workingDirectory);

        var byDirectory = await RunAsync(["cli", "session-id"], Configure);
        Environment.SetEnvironmentVariable(variable, "from-env");
        try
        {
            var byEnvironment = await RunAsync(["cli", "session-id"], Configure);
            var byFlag = await RunAsync(["cli", "--session", "from-flag", "session-id"], Configure);

            Assert.Equal(FileAgentSession.CreateWorkingDirectoryId(workingDirectory), byDirectory.Output);
            Assert.Equal("from-env", byEnvironment.Output);
            Assert.Equal("from-flag", byFlag.Output);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void Different_working_directories_get_different_default_sessions()
    {
        Assert.NotEqual(
            FileAgentSession.CreateWorkingDirectoryId(Path.Combine(_stateDirectory, "a")),
            FileAgentSession.CreateWorkingDirectoryId(Path.Combine(_stateDirectory, "b")));
    }

    [Fact]
    public async Task Invalid_session_id_is_a_usage_error()
    {
        var result = await RunAsync(["cli", "--session", "../escape", "current-target"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("not a valid session id", result.Error);
    }

    [Fact]
    public async Task Session_show_prints_the_file_path_and_values_and_clear_resets_it()
    {
        await RunAsync(["cli", "--session", "flow", "select-target", "--name", "alpha"]);

        var shown = await RunAsync(["cli", "--session", "flow", "session", "show"]);
        var cleared = await RunAsync(["cli", "--session", "flow", "session", "clear"]);
        var after = await RunAsync(["cli", "--session", "flow", "current-target"]);
        var shownAfter = await RunAsync(["cli", "--session", "flow", "session", "show"]);

        var path = Path.Combine(SessionsDirectory, "flow.json");
        Assert.Equal(0, shown.ExitCode);
        Assert.Contains($"path: {path}", shown.Output);
        Assert.Contains("target: \"alpha\"", shown.Output);
        Assert.Equal(0, cleared.ExitCode);
        Assert.False(File.Exists(path));
        Assert.Equal("none", after.Output);
        Assert.Contains("exists: false", shownAfter.Output);
    }

    [Fact]
    public async Task Session_commands_are_found_after_leading_global_flags_and_listed_in_help()
    {
        var shown = await RunAsync(["cli", "--audit-name", "x", "--session", "flow", "session", "show"]);
        var help = await RunAsync(["cli", "--session", "flow", "--help"]);
        var operationHelp = await RunAsync(["cli", "current-target", "--help"]);

        Assert.Equal(0, shown.ExitCode);
        Assert.Contains("id: flow", shown.Output);
        Assert.Contains("session show", help.Output);
        Assert.Contains("--session", operationHelp.Output);
    }

    [Fact]
    public async Task Initializers_run_before_the_operation_and_can_rehydrate_live_state()
    {
        await RunAsync(["cli", "--session", "flow", "select-target", "--name", "alpha"]);

        var rehydrated = await RunAsync(["cli", "--session", "flow", "live-target"]);
        var fresh = await RunAsync(["cli", "--session", "empty", "live-target"]);

        Assert.Equal("live:alpha", rehydrated.Output);
        Assert.Equal("live:none", fresh.Output);
    }

    [Fact]
    public async Task A_failing_initializer_does_not_block_the_command()
    {
        var result = await RunAsync(["cli", "--session", "flow", "current-target"], app =>
            app.ConfigureServices(builder => builder.Services.AddSingleton<IAgentSessionInitializer, ThrowingInitializer>()));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("none", result.Output);
    }

    [Fact]
    public async Task Mcp_mode_uses_an_in_memory_session_and_never_touches_disk()
    {
        using var cancellation = new CancellationTokenSource();
        IAgentSession? resolved = null;
        var result = await RunCoreAsync(
            [],
            app =>
            {
                app.DefaultSurface = AgentSurface.Mcp;
                app.ConfigureServices(builder => builder.Services.AddHostedService(provider =>
                {
                    resolved = provider.GetRequiredService<IAgentSession>();
                    resolved.Set("target", "alpha");
                    return new CancelOnStart(cancellation);
                }));
            },
            cancellation.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.IsType<InMemoryAgentSession>(resolved);
        Assert.Equal("alpha", resolved!.Get<string>("target"));
        Assert.False(Directory.Exists(_stateDirectory));
    }

    [Fact]
    public async Task Concurrent_saves_are_atomic_and_leave_a_valid_file()
    {
        var directory = SessionsDirectory;
        var tasks = Enumerable.Range(0, 24).Select(index => Task.Run(() =>
        {
            for (var round = 0; round < 10; round++)
            {
                var session = FileAgentSession.Open(directory, "shared");
                session.Set($"writer-{index}", round);
                session.Set("payload", new string((char)('a' + (index % 26)), 20_000));
                session.Save();
            }
        }));
        await Task.WhenAll(tasks);

        var reopened = FileAgentSession.Open(directory, "shared");
        var payload = reopened.Get<string>("payload");
        Assert.NotNull(payload);
        Assert.Equal(20_000, payload.Length);
        Assert.Single(payload.Distinct());
        Assert.NotNull(reopened.UpdatedAt);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void Expired_sessions_are_pruned_and_the_current_one_is_kept()
    {
        var directory = SessionsDirectory;
        foreach (var id in new[] { "old", "current", "fresh" })
        {
            var session = FileAgentSession.Open(directory, id);
            session.Set("k", 1);
            session.Save();
        }

        File.SetLastWriteTimeUtc(Path.Combine(directory, "old.json"), DateTime.UtcNow.AddDays(-40));
        File.SetLastWriteTimeUtc(Path.Combine(directory, "current.json"), DateTime.UtcNow.AddDays(-40));

        FileAgentSession.PruneExpired(directory, TimeSpan.FromDays(30), keepId: "current");

        Assert.False(File.Exists(Path.Combine(directory, "old.json")));
        Assert.True(File.Exists(Path.Combine(directory, "current.json")));
        Assert.True(File.Exists(Path.Combine(directory, "fresh.json")));
    }

    [Fact]
    public void Values_round_trip_and_a_corrupt_file_yields_an_empty_session()
    {
        var directory = SessionsDirectory;
        var session = FileAgentSession.Open(directory, "typed");
        session.Set("paths", new Dictionary<string, string> { ["a"] = "C:\\x" });
        session.Set("count", 3);
        session.Remove("missing");
        session.Save();

        var reopened = FileAgentSession.Open(directory, "typed");
        Assert.Equal("C:\\x", reopened.Get<Dictionary<string, string>>("paths")!["a"]);
        Assert.Equal(3, reopened.Get<int>("count"));
        Assert.Null(reopened.Get<string>("absent"));

        File.WriteAllText(Path.Combine(directory, "typed.json"), "{ not json");
        Assert.Equal(0, FileAgentSession.Open(directory, "typed").Get<int>("count"));
    }

    [Fact]
    public void Unchanged_sessions_do_not_write_a_file()
    {
        var session = FileAgentSession.Open(SessionsDirectory, "untouched");

        Assert.False(session.Save());
        Assert.False(File.Exists(session.Path));
    }

    private string SessionsDirectory => FileAgentSession.GetSessionsDirectory(_appName, _stateDirectory);

    private Task<(int ExitCode, string Output, string Error)> RunAsync(string[] args, Action<AgentSurfaceApplication>? configure = null) =>
        RunCoreAsync(args, configure, TestContext.Current.CancellationToken);

    private async Task<(int ExitCode, string Output, string Error)> RunCoreAsync(
        string[] args,
        Action<AgentSurfaceApplication>? configure,
        CancellationToken cancellationToken)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = await AgentSurfaceApplication.RunAsync(args, app =>
        {
            app.Output = output;
            app.Error = error;
            app.HandleCancelKeyPress = false;
            app.TerminateOutputWithNewline = false;
            app.ExecutableName = _appName;
            app.ConfigureSession(options =>
            {
                options.AppName = _appName;
                options.StateDirectory = _stateDirectory;
            });
            app.ConfigureServices(builder =>
            {
                builder.Logging.ClearProviders();
                builder.Services.AddSingleton<SessionOperations>();
                builder.Services.AddSingleton<LiveTarget>();
                builder.Services.AddSingleton<IAgentSessionInitializer, TargetInitializer>();
            });
            app.ConfigureCatalog(_ => OperationCatalog.Discover(typeof(SessionOperations)));
            app.AddGlobalOption("--audit-name", injectAs: "auditName");
            configure?.Invoke(app);
        }, cancellationToken);
        return (exitCode, output.ToString(), error.ToString());
    }

    private sealed class LiveTarget
    {
        public string? Name { get; set; }
    }

    private sealed class TargetInitializer(LiveTarget live) : IAgentSessionInitializer
    {
        public ValueTask InitializeAsync(IAgentSession session, CancellationToken cancellationToken)
        {
            live.Name = session.Get<string>("target");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingInitializer : IAgentSessionInitializer
    {
        public ValueTask InitializeAsync(IAgentSession session, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("stale session");
    }

    private sealed class CancelOnStart(CancellationTokenSource cancellation) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class SessionOperations(IAgentSession session, LiveTarget live)
    {
        [AgentOperation("select-target", "Selects the default target")]
        public string SelectTarget(string name)
        {
            session.Set("target", name);
            return name;
        }

        [AgentOperation("select-and-fail", "Selects a target and then fails")]
        public string SelectAndFail(string name)
        {
            session.Set("target", name);
            throw new OperationException("boom");
        }

        [AgentOperation("current-target", "Reads the default target")]
        public string CurrentTarget() => session.Get<string>("target") ?? "none";

        [AgentOperation("live-target", "Reads the rehydrated live target")]
        public string LiveTargetName() => $"live:{live.Name ?? "none"}";

        [AgentOperation("session-id", "Returns the session id")]
        public string SessionId(string? auditName = null) => session.Id;
    }
}
