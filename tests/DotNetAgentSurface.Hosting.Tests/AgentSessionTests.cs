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
    public async Task Global_options_are_accepted_after_the_session_subcommands()
    {
        await RunAsync(["cli", "--session", "flow", "select-target", "--name", "alpha"]);
        await RunAsync(["cli", "--session", "other", "select-target", "--name", "beta"]);

        var shown = await RunAsync(["cli", "session", "show", "--session", "flow"]);
        var leading = await RunAsync(["cli", "--session", "other", "session", "clear"]);
        var trailing = await RunAsync(["cli", "session", "clear", "--session", "flow", "--audit-name", "x", "--format", "json"]);

        Assert.Equal(0, shown.ExitCode);
        Assert.Contains("id: flow", shown.Output);
        Assert.Equal(0, leading.ExitCode);
        Assert.Equal(0, trailing.ExitCode);
        Assert.False(File.Exists(Path.Combine(SessionsDirectory, "flow.json")));
        Assert.False(File.Exists(Path.Combine(SessionsDirectory, "other.json")));
    }

    [Fact]
    public async Task Global_options_are_accepted_after_the_skill_subcommands()
    {
        var directory = Path.Combine(_stateDirectory, "skill-out");

        var check = await RunAsync(["cli", "skill", "check", "--out-dir", directory, "--session", "x"]);
        var generate = await RunAsync(["cli", "skill", "generate", "--out-dir", directory, "--audit-name=x", "--session=x"]);
        var current = await RunAsync(["cli", "--session", "x", "skill", "check", "--out-dir", directory]);

        Assert.Equal(1, check.ExitCode);
        Assert.Contains("missing or stale", check.Error + check.Output);
        Assert.Equal(0, generate.ExitCode);
        Assert.Equal(0, current.ExitCode);
    }

    [Fact]
    public async Task Unknown_trailing_flags_on_builtin_subcommands_still_exit_with_a_usage_error()
    {
        var session = await RunAsync(["cli", "session", "clear", "--bogus", "x"]);
        var skill = await RunAsync(["cli", "skill", "check", "--bogus", "x"]);

        Assert.Equal(2, session.ExitCode);
        Assert.Contains("Usage: session", session.Error + session.Output);
        Assert.Equal(2, skill.ExitCode);
        Assert.Contains("Skill reference commands", skill.Error + skill.Output);
    }

    [Fact]
    public async Task Session_and_skill_help_list_the_global_options()
    {
        var session = await RunAsync(["cli", "session", "--help"]);
        var skill = await RunAsync(["cli", "skill", "--help"]);

        foreach (var help in new[] { session.Output, skill.Output })
        {
            Assert.Contains("--session", help);
            Assert.Contains("--audit-name", help);
            Assert.Contains("--fields", help);
        }
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
    public async Task Parallel_updates_to_one_dictionary_key_all_survive()
    {
        const int writers = 24;
        var directory = SessionsDirectory;
        await Task.WhenAll(Enumerable.Range(0, writers).Select(index => Task.Run(() =>
        {
            var session = FileAgentSession.Open(directory, "shared");
            session.Update<Dictionary<string, int>>("targets", targets =>
            {
                targets ??= [];
                targets[$"target-{index}"] = index;
                return targets;
            });
        })));

        var targets = FileAgentSession.Open(directory, "shared").Get<Dictionary<string, int>>("targets");
        Assert.NotNull(targets);
        Assert.Equal(writers, targets.Count);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task Parallel_get_or_add_returns_one_value_to_every_caller()
    {
        var directory = SessionsDirectory;
        var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(() =>
            FileAgentSession.Open(directory, "shared").GetOrAdd("cursorKey", () => Guid.NewGuid().ToString("N")))));

        Assert.Single(results.Distinct());
        Assert.Equal(results[0], FileAgentSession.Open(directory, "shared").Get<string>("cursorKey"));
    }

    [Fact]
    public void Get_or_add_and_update_persist_immediately_without_save()
    {
        var directory = SessionsDirectory;
        var session = FileAgentSession.Open(directory, "immediate");

        var created = session.GetOrAdd("count", () => 1);
        var again = session.GetOrAdd("count", () => 2);
        var updated = session.Update<int>("count", current => current + 10);

        Assert.Equal(1, created);
        Assert.Equal(1, again);
        Assert.Equal(11, updated);
        Assert.False(session.Save());
        Assert.Equal(11, FileAgentSession.Open(directory, "immediate").Get<int>("count"));
    }

    [Fact]
    public void Save_merges_only_the_changed_keys_into_the_current_file()
    {
        var directory = SessionsDirectory;
        var seed = FileAgentSession.Open(directory, "merge");
        seed.Set("shared", "seed");
        seed.Set("removed", 1);
        seed.Save();

        var first = FileAgentSession.Open(directory, "merge");
        var second = FileAgentSession.Open(directory, "merge");
        first.Set("a", 1);
        first.Remove("removed");
        second.Set("b", 2);
        first.Save();
        second.Save();

        var merged = FileAgentSession.Open(directory, "merge");
        Assert.Equal(1, merged.Get<int>("a"));
        Assert.Equal(2, merged.Get<int>("b"));
        Assert.Equal("seed", merged.Get<string>("shared"));
        Assert.Equal(0, merged.Get<int>("removed"));
        Assert.Equal(2, second.Get<int>("b"));
        Assert.Equal(1, second.Get<int>("a"));
    }

    [Fact]
    public void Save_after_update_keeps_both_changes_and_leaves_no_pending_state()
    {
        var directory = SessionsDirectory;
        var session = FileAgentSession.Open(directory, "mixed");
        session.Set("local", "x");
        session.Update<int>("counter", current => current + 1);

        Assert.True(session.Save());
        Assert.False(session.Save());
        var reopened = FileAgentSession.Open(directory, "mixed");
        Assert.Equal("x", reopened.Get<string>("local"));
        Assert.Equal(1, reopened.Get<int>("counter"));
    }

    [Fact]
    public void In_memory_get_or_add_and_update_are_atomic_in_process()
    {
        IAgentSession session = new InMemoryAgentSession();
        var values = Enumerable.Range(0, 32).AsParallel().Select(_ => session.GetOrAdd("k", () => Guid.NewGuid().ToString("N"))).ToList();
        Enumerable.Range(0, 32).AsParallel().ForAll(_ => session.Update<int>("n", current => current + 1));

        Assert.Single(values.Distinct());
        Assert.Equal(32, session.Get<int>("n"));
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
    public void Touching_a_read_only_session_protects_it_from_pruning()
    {
        var directory = SessionsDirectory;
        var session = FileAgentSession.Open(directory, "active");
        session.Set("k", 1);
        session.Save();
        File.SetLastWriteTimeUtc(session.Path, DateTime.UtcNow.AddDays(-40));

        FileAgentSession.Open(directory, "active").Touch();
        FileAgentSession.PruneExpired(directory, TimeSpan.FromDays(30));

        Assert.True(File.Exists(session.Path));
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
