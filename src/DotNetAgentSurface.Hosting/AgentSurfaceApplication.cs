using System.Text;
using DotNetAgentSurface.CommandLine;
using DotNetAgentSurface.Core;
using DotNetAgentSurface.Mcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotNetAgentSurface.Hosting;

/// <summary>
/// A dual-surface application host: one executable and one dependency-injection composition that serves an MCP
/// stdio server and a plain command-line interface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mode selection</b> looks at the first argument: a verb registered with <see cref="AddVerb(string, Func{string[], CancellationToken, Task{int}})"/>
/// runs its handler before any host is built; <see cref="CliVerb"/> (default <c>cli</c>) runs the CLI;
/// <see cref="McpVerb"/> (default <c>mcp</c>) runs the MCP server; anything else uses <see cref="DefaultSurface"/>
/// and the arguments are left untouched. In MCP mode configuration-style arguments (<c>--Key=Value</c>) reach the
/// host builder as usual.
/// </para>
/// <para>
/// <b>CLI mode</b> builds the host from configuration only (never from operation arguments), does not start hosted
/// services other than <see cref="ICliHostedService"/> implementations, logs to standard error at
/// <see cref="LogLevel.Warning"/> by default, maps Ctrl+C to a <see cref="CancellationToken"/>, writes results to standard
/// output and errors to standard error, and disposes the host with <c>DisposeAsync</c>. String results are written
/// verbatim.
/// </para>
/// <para>
/// Console logging is routed to standard error in both modes so it can never corrupt MCP stdio traffic or CLI output.
/// </para>
/// </remarks>
public sealed class AgentSurfaceApplication
{
    private readonly List<Action<HostApplicationBuilder>> _serviceConfigurations = [];
    private readonly List<Action<IMcpServerBuilder>> _mcpConfigurations = [];
    private readonly List<Action<IServiceProvider, OperationCatalogBuilder>> _catalogConfigurations = [];
    private readonly List<Action<OperationCommandLineOptions>> _cliConfigurations = [];
    private readonly Dictionary<string, Func<string[], CancellationToken, Task<int>>> _verbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CommandLineGlobalOption> _globalOptions = [];
    private Func<IServiceProvider, OperationCatalog>? _catalogFactory;

    /// <summary>Gets or sets the verb that selects the CLI. Defaults to <c>cli</c>.</summary>
    public string CliVerb { get; set; } = "cli";

    /// <summary>Gets or sets the verb that selects the MCP server. Defaults to <c>mcp</c>; set to <see langword="null"/> to disable it.</summary>
    public string? McpVerb { get; set; } = "mcp";

    /// <summary>
    /// Gets or sets the surface used when the first argument is not a verb. <see cref="AgentSurface.Mcp"/> (the default)
    /// suits a server package; <see cref="AgentSurface.Cli"/> suits a CLI-first package built from the same code.
    /// </summary>
    public AgentSurface DefaultSurface { get; set; } = AgentSurface.Mcp;

    /// <summary>Gets or sets the executable name used in help and generated skills. Defaults to the process name.</summary>
    public string? ExecutableName { get; set; }

    /// <summary>Gets or sets the generated skill name. Defaults to <see cref="ExecutableName"/>.</summary>
    public string? SkillName { get; set; }

    /// <summary>Gets or sets the generated skill description.</summary>
    public string? SkillDescription { get; set; }

    /// <summary>Gets or sets the directory <c>skill generate</c> and <c>skill check</c> use without <c>--out-dir</c>. Defaults to <c>skill</c>.</summary>
    public string SkillOutputDirectory { get; set; } = SkillGeneratorCommand.DefaultOutputDirectory;

    /// <summary>Gets or sets the default CLI output renderer for object results. Defaults to TOON.</summary>
    public IAgentOutputRenderer? CliRenderer { get; set; }

    /// <summary>Gets or sets the writer for standard output. Defaults to the process standard output (UTF-8 when redirected).</summary>
    public TextWriter? Output { get; set; }

    /// <summary>Gets or sets the writer for standard error. Defaults to the process standard error (UTF-8 when redirected).</summary>
    public TextWriter? Error { get; set; }

    /// <summary>Gets or sets the reader used for <c>-</c> values. Defaults to <see cref="Console.In"/>.</summary>
    public TextReader? Input { get; set; }

    /// <summary>
    /// Gets or sets whether a string result gets a trailing newline. By default a newline is added only when the
    /// default writer is an interactive terminal, so redirected output is byte-identical to the result.
    /// </summary>
    public bool? TerminateOutputWithNewline { get; set; }

    /// <summary>Gets or sets whether Ctrl+C is mapped to cancellation in CLI mode. Defaults to <see langword="true"/>.</summary>
    public bool HandleCancelKeyPress { get; set; } = true;

    /// <summary>Registers services shared by both modes. The builder's configuration includes global option values in CLI mode.</summary>
    public AgentSurfaceApplication ConfigureServices(Action<HostApplicationBuilder> configure)
    {
        _serviceConfigurations.Add(configure ?? throw new ArgumentNullException(nameof(configure)));
        return this;
    }

    /// <summary>
    /// Configures the MCP SDK builder (MCP mode only), for example <c>mcp.WithStdioServerTransport()</c>, <c>WithTools&lt;T&gt;()</c>,
    /// or a custom transport.
    /// </summary>
    public AgentSurfaceApplication ConfigureMcp(Action<IMcpServerBuilder> configure)
    {
        _mcpConfigurations.Add(configure ?? throw new ArgumentNullException(nameof(configure)));
        return this;
    }

    /// <summary>
    /// Supplies the operation catalog from the built service provider, so a <c>targetFactory</c> or an operation's
    /// dependencies can resolve from DI. The catalog is registered as a singleton <see cref="OperationCatalog"/>.
    /// </summary>
    public AgentSurfaceApplication ConfigureCatalog(Func<IServiceProvider, OperationCatalog> factory)
    {
        _catalogFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    /// <summary>Populates an <see cref="OperationCatalogBuilder"/> with access to the built service provider. May be called more than once.</summary>
    public AgentSurfaceApplication ConfigureCatalog(Action<IServiceProvider, OperationCatalogBuilder> configure)
    {
        _catalogConfigurations.Add(configure ?? throw new ArgumentNullException(nameof(configure)));
        return this;
    }

    /// <summary>Adjusts the CLI adapter options, for example <see cref="OperationCommandLineOptions.ResultRenderer"/>.</summary>
    public AgentSurfaceApplication ConfigureCli(Action<OperationCommandLineOptions> configure)
    {
        _cliConfigurations.Add(configure ?? throw new ArgumentNullException(nameof(configure)));
        return this;
    }

    /// <summary>Registers a host-owned verb that runs before any host is built. The handler receives the arguments after the verb.</summary>
    public AgentSurfaceApplication AddVerb(string name, Func<string[], CancellationToken, Task<int>> handler)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A verb name is required.", nameof(name));
        }

        _verbs[name] = handler ?? throw new ArgumentNullException(nameof(handler));
        return this;
    }

    /// <inheritdoc cref="AddVerb(string, Func{string[], CancellationToken, Task{int}})"/>
    public AgentSurfaceApplication AddVerb(string name, Func<string[], Task<int>> handler)
    {
        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        return AddVerb(name, (args, _) => handler(args));
    }

    /// <inheritdoc cref="AddVerb(string, Func{string[], CancellationToken, Task{int}})"/>
    public AgentSurfaceApplication AddVerb(string name, Func<string[], int> handler)
    {
        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        return AddVerb(name, (args, _) => Task.FromResult(handler(args)));
    }

    /// <summary>
    /// Declares a host-level CLI flag such as <c>--connection</c>.
    /// </summary>
    /// <param name="name">The flag, for example <c>--connection</c>.</param>
    /// <param name="injectAs">
    /// The operation parameter that receives the value. It is injected only into operations that declare that parameter,
    /// and ignored otherwise.
    /// </param>
    /// <param name="configKey">A configuration key the value maps to before the host is built, for example <c>TargetAssemblyPath</c>.</param>
    /// <param name="description">Text shown in help and generated skills.</param>
    public AgentSurfaceApplication AddGlobalOption(string name, string? injectAs = null, string? configKey = null, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.TrimStart('-').Length == 0)
        {
            throw new ArgumentException("A flag name is required.", nameof(name));
        }

        _globalOptions.Add(new CommandLineGlobalOption(name, injectAs, configKey, description));
        return this;
    }

    /// <summary>Creates, configures, and runs an application, returning the process exit code.</summary>
    public static Task<int> RunAsync(string[] args, Action<AgentSurfaceApplication> configure, CancellationToken cancellationToken = default)
    {
        if (configure is null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        var application = new AgentSurfaceApplication();
        configure(application);
        return application.RunAsync(args, cancellationToken);
    }

    /// <summary>Selects a mode from <paramref name="args"/> and runs it, returning the process exit code.</summary>
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        args ??= [];
        if (DefaultSurface is not (AgentSurface.Mcp or AgentSurface.Cli))
        {
            throw new InvalidOperationException($"{nameof(DefaultSurface)} must be {nameof(AgentSurface.Mcp)} or {nameof(AgentSurface.Cli)}.");
        }

        var (surface, handler, modeArgs) = SelectMode(args);
        try
        {
            if (handler is null && surface == AgentSurface.Mcp && modeArgs is ["--help" or "-h"])
            {
                (Output ?? Console.Out).WriteLine(RenderUsage());
                return 0;
            }

            if (handler is not null)
            {
                return await handler(modeArgs, cancellationToken).ConfigureAwait(false);
            }

            return surface == AgentSurface.Cli
                ? await RunCliAsync(modeArgs, cancellationToken).ConfigureAwait(false)
                : await RunMcpAsync(modeArgs, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
    }

    private string RenderUsage()
    {
        var executable = ResolveExecutableName();
        var lines = new List<string> { $"Usage: {executable} [verb] [arguments]", string.Empty };
        lines.Add(DefaultSurface == AgentSurface.Mcp
            ? $"  (no verb)        Run the MCP server over stdio."
            : $"  (no verb)        Run the command-line interface ('{executable} <operation> ...').");
        if (!string.IsNullOrWhiteSpace(McpVerb))
        {
            lines.Add($"  {McpVerb,-16} Run the MCP server.");
        }

        lines.Add($"  {CliVerb,-16} Run the command-line interface ('{executable} {CliVerb} --help' lists operations).");
        lines.AddRange(_verbs.Keys.OrderBy(static name => name, StringComparer.Ordinal).Select(name => $"  {name,-16} Host command."));
        return string.Join("\n", lines);
    }

    private (AgentSurface Surface, Func<string[], CancellationToken, Task<int>>? Handler, string[] Args) SelectMode(string[] args)
    {
        if (args.Length > 0)
        {
            var first = args[0];
            if (_verbs.TryGetValue(first, out var handler))
            {
                return (AgentSurface.Unknown, handler, args.Skip(1).ToArray());
            }

            if (string.Equals(first, CliVerb, StringComparison.OrdinalIgnoreCase))
            {
                return (AgentSurface.Cli, null, args.Skip(1).ToArray());
            }

            if (!string.IsNullOrWhiteSpace(McpVerb) && string.Equals(first, McpVerb, StringComparison.OrdinalIgnoreCase))
            {
                return (AgentSurface.Mcp, null, args.Skip(1).ToArray());
            }
        }

        return (DefaultSurface, null, args);
    }

    private async Task<int> RunMcpAsync(string[] args, CancellationToken cancellationToken)
    {
        var builder = Host.CreateApplicationBuilder(args);
        ConfigureCommon(builder);
        var mcp = builder.Services.AddMcpServer();
        foreach (var configure in _mcpConfigurations)
        {
            configure(mcp);
        }

        var host = builder.Build();
        try
        {
            await host.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await DisposeHostAsync(host).ConfigureAwait(false);
        }

        return 0;
    }

    private async Task<int> RunCliAsync(string[] args, CancellationToken cancellationToken)
    {
        var writers = CliWriters.Create(this);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler? onCancel = null;
        if (HandleCancelKeyPress)
        {
            onCancel = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            Console.CancelKeyPress += onCancel;
        }

        try
        {
            var result = await ExecuteCliAsync(args, cancellation.Token).ConfigureAwait(false);
            writers.Write(result, TerminateOutputWithNewline);
            return result.ExitCode;
        }
        catch (OperationCanceledException)
        {
            writers.Write(CommandLineExecutionResult.Failure("Operation was cancelled.", 130), TerminateOutputWithNewline);
            return 130;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            writers.Write(CommandLineExecutionResult.Failure(exception.Message), TerminateOutputWithNewline);
            return 1;
        }
        finally
        {
            if (onCancel is not null)
            {
                Console.CancelKeyPress -= onCancel;
            }

            writers.Dispose();
        }
    }

    private async Task<CommandLineExecutionResult> ExecuteCliAsync(string[] args, CancellationToken cancellationToken)
    {
        var configuration = OperationCommandLineAdapter.ReadConfigurationValues(args, _globalOptions);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        if (configuration.Count > 0)
        {
            builder.Configuration.AddInMemoryCollection(configuration.Select(static pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)));
        }

        ConfigureCommon(builder);

        var host = builder.Build();
        var started = new List<IHostedService>();
        try
        {
            foreach (var service in host.Services.GetServices<IHostedService>())
            {
                if (service is ICliHostedService)
                {
                    await service.StartAsync(cancellationToken).ConfigureAwait(false);
                    started.Add(service);
                }
            }

            var catalog = host.Services.GetRequiredService<OperationCatalog>();
            var invoker = host.Services.GetRequiredService<OperationInvoker>();
            var options = CreateCliOptions();

            if (args is ["skill", ..] && !HasRootEntry(catalog, "skill"))
            {
                return await SkillGeneratorCommand.ExecuteAsync(
                    args.Skip(1).ToArray(),
                    catalog,
                    SkillOutputDirectory,
                    cancellationToken,
                    CreateSkillOptions(options)).ConfigureAwait(false);
            }

            var adapter = new OperationCommandLineAdapter(catalog, invoker, CliRenderer ?? new ToonAgentOutputRenderer(), options: options);
            var result = await adapter.ExecuteAsync(args, cancellationToken).ConfigureAwait(false);
            return IsRootHelp(args) && result.ExitCode == 0 && !HasRootEntry(catalog, "skill")
                ? result with { Output = result.Output + "\n\nSkill commands:\n  skill generate [--out-dir <dir>] [--force]\n  skill check [--out-dir <dir>]" }
                : result;
        }
        finally
        {
            for (var index = started.Count - 1; index >= 0; index--)
            {
                await started[index].StopAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await DisposeHostAsync(host).ConfigureAwait(false);
        }
    }

    // IHost only exposes sync Dispose, which throws for a singleton that is IAsyncDisposable-only.
    private static async ValueTask DisposeHostAsync(IHost host)
    {
        if (host is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            host.Dispose();
        }
    }

    private static bool IsRootHelp(string[] args) => args.Length == 0 || args[0] is "--help" or "-h";

    private static bool HasRootEntry(OperationCatalog catalog, string name) =>
        catalog.ForSurface(AgentSurface.Cli).Operations.Any(operation =>
            string.Equals(OperationCatalog.GetCategorySegments(operation.Category).FirstOrDefault() ?? operation.Name, name, StringComparison.OrdinalIgnoreCase));

    private OperationCommandLineOptions CreateCliOptions()
    {
        var options = new OperationCommandLineOptions
        {
            GlobalOptions = [.. _globalOptions],
            Input = Input,
        };
        foreach (var configure in _cliConfigurations)
        {
            configure(options);
        }

        return options;
    }

    private SkillGenerationOptions CreateSkillOptions(OperationCommandLineOptions cliOptions)
    {
        var executable = ResolveExecutableName();
        var globals = new List<SkillGlobalOption>
        {
            new("--output <toon|json>", "Output format for object results (alias `--format`). String results are always written verbatim."),
            new("--fields <name,...>", "Select fields of list results."),
            new("--full", "Do not truncate long values in object results."),
            new("--confirm", "Confirm an operation that requires confirmation."),
            new("--yes", "With `--confirm`, also confirm a dangerous operation."),
        };
        globals.AddRange(cliOptions.GlobalOptions.Select(option =>
            new SkillGlobalOption(
                $"--{option.FlagName} <value>",
                (option.Description ?? "Host-level option.") + (option.InjectAs is null ? string.Empty : $" Sets `--{OperationNaming.ToKebabCase(option.InjectAs)}` on operations that declare it."))));

        return new SkillGenerationOptions(
            string.IsNullOrWhiteSpace(SkillName) ? executable : SkillName!,
            string.IsNullOrWhiteSpace(SkillDescription) ? $"Operations exposed by the {executable} CLI." : SkillDescription!,
            executable)
        {
            CommandPrefix = DefaultSurface == AgentSurface.Cli ? executable : $"{executable} {CliVerb}",
            GlobalOptions = globals,
        };
    }

    private string ResolveExecutableName()
    {
        if (!string.IsNullOrWhiteSpace(ExecutableName))
        {
            return ExecutableName!;
        }

        var processPath = Environment.ProcessPath;
        var name = string.IsNullOrWhiteSpace(processPath) ? null : Path.GetFileNameWithoutExtension(processPath);
        return string.IsNullOrWhiteSpace(name) || string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase)
            ? System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "app"
            : name;
    }

    private void ConfigureCommon(HostApplicationBuilder builder)
    {
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        foreach (var configure in _serviceConfigurations)
        {
            configure(builder);
        }

        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IOperationInvocationPolicy, DangerousOperationConfirmationPolicy>());
        builder.Services.TryAddSingleton(BuildCatalog);
        builder.Services.TryAddSingleton(static services => new OperationInvoker(
            services,
            policies: services.GetServices<IOperationInvocationPolicy>()));
        builder.Services.TryAddSingleton(static services => new McpOperationAdapter(
            services.GetRequiredService<OperationCatalog>().ForSurface(AgentSurface.Mcp),
            services.GetRequiredService<OperationInvoker>()));
    }

    private OperationCatalog BuildCatalog(IServiceProvider services)
    {
        if (_catalogFactory is not null)
        {
            if (_catalogConfigurations.Count > 0)
            {
                throw new InvalidOperationException("Use either ConfigureCatalog(Func<IServiceProvider, OperationCatalog>) or ConfigureCatalog(Action<IServiceProvider, OperationCatalogBuilder>), not both.");
            }

            return _catalogFactory(services);
        }

        var catalogBuilder = new OperationCatalogBuilder();
        foreach (var configure in _catalogConfigurations)
        {
            configure(services, catalogBuilder);
        }

        return catalogBuilder.Build();
    }

    private sealed class CliWriters : IDisposable
    {
        private readonly TextWriter _output;
        private readonly TextWriter _error;
        private readonly bool _interactive;
        private readonly List<TextWriter> _owned = [];

        private CliWriters(TextWriter output, TextWriter error, bool interactive)
        {
            _output = output;
            _error = error;
            _interactive = interactive;
        }

        public static CliWriters Create(AgentSurfaceApplication application)
        {
            var owned = new List<TextWriter>();
            var interactive = false;
            var output = application.Output;
            if (output is null)
            {
                if (Console.IsOutputRedirected)
                {
                    output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
                    owned.Add(output);
                }
                else
                {
                    output = Console.Out;
                    interactive = true;
                }
            }

            var error = application.Error;
            if (error is null)
            {
                if (Console.IsErrorRedirected)
                {
                    error = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };
                    owned.Add(error);
                }
                else
                {
                    error = Console.Error;
                }
            }

            var writers = new CliWriters(output, error, interactive);
            writers._owned.AddRange(owned);
            return writers;
        }

        public void Write(CommandLineExecutionResult result, bool? terminateWithNewline)
        {
            if (!string.IsNullOrEmpty(result.Output))
            {
                _output.Write(result.Output);
                var terminate = result.IsVerbatimOutput
                    ? terminateWithNewline ?? _interactive
                    : terminateWithNewline ?? true;
                if (terminate && !result.Output.EndsWith("\n", StringComparison.Ordinal))
                {
                    _output.Write('\n');
                }

                _output.Flush();
            }

            if (!string.IsNullOrEmpty(result.Error))
            {
                _error.Write(result.Error);
                if (!result.Error!.EndsWith("\n", StringComparison.Ordinal))
                {
                    _error.Write('\n');
                }

                _error.Flush();
            }
        }

        public void Dispose()
        {
            foreach (var writer in _owned)
            {
                writer.Flush();
                writer.Dispose();
            }
        }
    }
}
