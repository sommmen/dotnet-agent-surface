using DotNetAgentSurface.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotNetAgentSurface.Hosting.Tests;

public sealed class AgentSurfaceApplicationTests
{
    [Fact]
    public async Task Cli_mode_disposes_async_only_singletons_and_does_not_start_hosted_services()
    {
        var probe = new Probe();
        var result = await RunAsync(["cli", "ping"], probe, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("pong", result.Output);
        Assert.True(probe.AsyncDisposed);
        Assert.False(probe.PlainHostedServiceStarted);
        Assert.True(probe.CliHostedServiceStarted);
        Assert.True(probe.CliHostedServiceStopped);
    }

    [Fact]
    public async Task Cli_mode_never_feeds_operation_arguments_to_host_configuration()
    {
        var probe = new Probe();
        var result = await RunAsync(["cli", "echo-config", "--query", "fromargs"], probe, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("fromargs|none", result.Output);
    }

    [Fact]
    public async Task Global_option_with_config_key_is_applied_before_the_host_is_built()
    {
        var probe = new Probe();
        var result = await RunAsync(["cli", "--setting", "fromflag", "config"], probe, app => app.AddGlobalOption("--setting", configKey: "Setting"), TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("fromflag", result.Output);
    }

    [Fact]
    public async Task Global_option_with_inject_as_reaches_only_operations_declaring_the_parameter()
    {
        var probe = new Probe();
        var app = (Action<AgentSurfaceApplication>)(a => a.AddGlobalOption("--connection", injectAs: "connectionName"));

        var declared = await RunAsync(["cli", "--connection", "db1", "connect"], probe, app, TestContext.Current.CancellationToken);
        var ignored = await RunAsync(["cli", "--connection", "db1", "ping"], probe, app, TestContext.Current.CancellationToken);

        Assert.Equal("db1", declared.Output);
        Assert.Equal(0, ignored.ExitCode);
    }

    [Fact]
    public async Task Registered_verb_receives_remaining_arguments_and_skips_the_host()
    {
        var probe = new Probe();
        string[]? received = null;
        var result = await RunAsync(["hooks", "install", "--force"], probe, app => app.AddVerb("hooks", args =>
        {
            received = args;
            return 7;
        }), TestContext.Current.CancellationToken);

        Assert.Equal(7, result.ExitCode);
        Assert.NotNull(received);
        Assert.Equal(["install", "--force"], received);
        Assert.False(probe.AsyncDisposed);
    }

    [Fact]
    public async Task Default_surface_cli_runs_operations_without_a_verb()
    {
        var probe = new Probe();
        var result = await RunAsync(["ping"], probe, app => app.DefaultSurface = AgentSurface.Cli, TestContext.Current.CancellationToken);

        Assert.Equal("pong", result.Output);
    }

    [Fact]
    public async Task Mcp_mode_keeps_configuration_arguments_and_runs_hosted_services_until_stopped()
    {
        var probe = new Probe();
        using var cancellation = new CancellationTokenSource();
        string? setting = null;
        var result = await RunAsync(
            ["--Setting=fromargs"],
            probe,
            app => app.ConfigureServices(builder =>
            {
                setting = builder.Configuration["Setting"];
                builder.Services.AddHostedService(_ => new CancelOnStart(cancellation));
            }),
            cancellation.Token);

        Assert.Equal("fromargs", setting);
        Assert.True(probe.PlainHostedServiceStarted);
        Assert.True(probe.AsyncDisposed);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Mcp_help_lists_verbs_without_building_a_host()
    {
        var probe = new Probe();
        var result = await RunAsync(["--help"], probe, app => app.AddVerb("hooks", _ => 0), TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hooks", result.Output);
        Assert.Contains("cli", result.Output);
        Assert.False(probe.AsyncDisposed);
    }

    [Fact]
    public async Task Mcp_help_mentions_the_version_flag()
    {
        var result = await RunAsync(["--help"], new Probe(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("--version", result.Output);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    public async Task Version_flag_routes_to_a_registered_version_verb(string flag)
    {
        var probe = new Probe();
        string[]? received = null;
        var result = await RunAsync([flag], probe, app => app.AddVerb("version", args =>
        {
            received = args;
            return 5;
        }), TestContext.Current.CancellationToken);

        Assert.Equal(5, result.ExitCode);
        Assert.NotNull(received);
        Assert.Empty(received);
        Assert.False(probe.PlainHostedServiceStarted);
    }

    [Theory]
    [InlineData("--version", AgentSurface.Mcp)]
    [InlineData("-v", AgentSurface.Mcp)]
    [InlineData("--version", AgentSurface.Cli)]
    [InlineData("-v", AgentSurface.Cli)]
    public async Task Version_flag_prints_the_entry_assembly_version_without_a_version_verb(string flag, AgentSurface surface)
    {
        var probe = new Probe();
        var result = await RunAsync([flag], probe, app => app.DefaultSurface = surface, TestContext.Current.CancellationToken);

        var informational = System.Reflection.Assembly.GetEntryAssembly()!
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>()
            .Single().InformationalVersion;
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(informational.Split('+')[0], result.Output.Trim());
        Assert.Equal(string.Empty, result.Error);
        Assert.False(probe.PlainHostedServiceStarted);
        Assert.False(probe.AsyncDisposed);
    }

    [Theory]
    [InlineData("--foo")]
    [InlineData("-x")]
    [InlineData("-x=1")]
    [InlineData("verison")]
    [InlineData("--Setting=a", "--foo")]
    [InlineData("--")]
    [InlineData("mcp", "--foo")]
    [InlineData("mcp", "verison")]
    public async Task Mcp_surface_rejects_unrecognised_arguments_with_usage_and_exit_2(params string[] args)
    {
        var probe = new Probe();
        var result = await RunAsync(args, probe, app => app.AddVerb("hooks", _ => 0), TestContext.Current.CancellationToken);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        Assert.Contains("Usage:", result.Error);
        Assert.Contains("hooks", result.Error);
        Assert.Contains(args.Last(arg => !arg.StartsWith("--Setting", StringComparison.Ordinal)), result.Error);
        Assert.False(probe.PlainHostedServiceStarted);
        Assert.False(probe.AsyncDisposed);
    }

    [Theory]
    [InlineData("--Setting=fromargs")]
    [InlineData("--Setting", "fromargs")]
    [InlineData("/Setting=fromargs")]
    [InlineData("/Setting", "fromargs")]
    [InlineData("Setting=fromargs")]
    [InlineData("mcp", "--Setting=fromargs")]
    public async Task Mcp_surface_keeps_configuration_style_arguments(params string[] args)
    {
        var probe = new Probe();
        using var cancellation = new CancellationTokenSource();
        string? setting = null;
        var result = await RunAsync(
            args,
            probe,
            app => app.ConfigureServices(builder =>
            {
                setting = builder.Configuration["Setting"];
                builder.Services.AddHostedService(_ => new CancelOnStart(cancellation));
            }),
            cancellation.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("fromargs", setting);
        Assert.True(probe.PlainHostedServiceStarted);
    }

    [Fact]
    public async Task Mcp_surface_without_arguments_starts_the_server()
    {
        var probe = new Probe();
        using var cancellation = new CancellationTokenSource();
        var result = await RunAsync(
            [],
            probe,
            app => app.ConfigureServices(builder => builder.Services.AddHostedService(_ => new CancelOnStart(cancellation))),
            cancellation.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.True(probe.PlainHostedServiceStarted);
    }

    [Fact]
    public async Task Cli_cancellation_token_maps_to_exit_130()
    {
        var probe = new Probe();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var result = await RunAsync(["cli", "ping"], probe, cancellationToken: cancellation.Token);

        Assert.Equal(130, result.ExitCode);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string[] args,
        Probe probe,
        Action<AgentSurfaceApplication>? configure = null,
        CancellationToken cancellationToken = default)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = await AgentSurfaceApplication.RunAsync(args, app =>
        {
            app.Output = output;
            app.Error = error;
            app.HandleCancelKeyPress = false;
            app.TerminateOutputWithNewline = false;
            app.ConfigureServices(builder =>
            {
                builder.Services.AddSingleton(probe);
                builder.Services.AddSingleton<AsyncOnlyService>();
                builder.Services.AddSingleton<TestOperations>();
                builder.Services.AddHostedService<PlainHostedService>();
                builder.Services.AddHostedService<CliHostedService>();
                builder.Logging.ClearProviders();
            });
            app.ConfigureCatalog(_ => OperationCatalog.Discover(typeof(TestOperations)));
            configure?.Invoke(app);
        }, cancellationToken);
        return (exitCode, output.ToString(), error.ToString());
    }

    private sealed class Probe
    {
        public bool AsyncDisposed { get; set; }

        public bool PlainHostedServiceStarted { get; set; }

        public bool CliHostedServiceStarted { get; set; }

        public bool CliHostedServiceStopped { get; set; }
    }

    private sealed class AsyncOnlyService(Probe probe) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            probe.AsyncDisposed = true;
            return ValueTask.CompletedTask;
        }

    }

    private sealed class PlainHostedService(Probe probe, AsyncOnlyService dependency) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(dependency);
            probe.PlainHostedServiceStarted = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CliHostedService(Probe probe) : ICliHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            probe.CliHostedServiceStarted = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            probe.CliHostedServiceStopped = true;
            return Task.CompletedTask;
        }
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

    private sealed class TestOperations(IConfiguration configuration)
    {
        [AgentOperation("ping", "Pings")]
        public string Ping() => "pong";

        [AgentOperation("config", "Reads a setting")]
        public string Config() => configuration["Setting"] ?? "default";

        [AgentOperation("echo-config", "Echoes an argument next to the configuration value of the same name")]
        public string EchoConfig(string query) => $"{query}|{configuration["query"] ?? "none"}";

        [AgentOperation("connect", "Connects")]
        public string Connect(string connectionName) => connectionName;
    }
}
