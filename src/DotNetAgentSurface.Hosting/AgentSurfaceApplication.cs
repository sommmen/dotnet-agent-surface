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
/// <b>Session state:</b> an <see cref="IAgentSession"/> is registered for operations and services. MCP mode uses an
/// in-memory session that never touches disk. CLI mode uses a <see cref="FileAgentSession"/> that is loaded before the
/// invocation and saved after a successful one; its id comes from <c>--session &lt;id&gt;</c>, then the
/// <c>&lt;APP&gt;_SESSION</c> environment variable, then a hash of the working directory. Every registered
/// <see cref="IAgentSessionInitializer"/> runs after the host is built and before the operation is invoked. The built-in
/// <c>cli session show|clear</c> commands inspect and reset the file. Never store secrets in a session.
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
    private const string SessionConfigurationKey = "DotNetAgentSurface:Session";

    private readonly AgentSessionOptions _sessionOptions = new();
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

    /// <summary>
    /// Configures session state: the application name (state directory and <c>&lt;APP&gt;_SESSION</c> variable), the state
    /// directory, and the time-to-live for pruning. See <see cref="IAgentSession"/>.
    /// </summary>
    public AgentSurfaceApplication ConfigureSession(Action<AgentSessionOptions> configure)
    {
        (configure ?? throw new ArgumentNullException(nameof(configure)))(_sessionOptions);
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
        ConfigureCommon(builder, session: null);
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
        var globalOptions = GetCliGlobalOptions();
        var configuration = OperationCommandLineAdapter.ReadConfigurationValues(args, globalOptions);

        FileAgentSession session;
        try
        {
            session = OpenSession(configuration);
        }
        catch (ArgumentException exception)
        {
            return CommandLineExecutionResult.Failure(exception.Message, 2);
        }

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        if (configuration.Count > 0)
        {
            builder.Configuration.AddInMemoryCollection(configuration.Select(static pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)));
        }

        ConfigureCommon(builder, session);

        var host = builder.Build();
        var started = new List<IHostedService>();
        try
        {
            var catalog = host.Services.GetRequiredService<OperationCatalog>();
            var options = CreateCliOptions(globalOptions);
            var commandStart = OperationCommandLineAdapter.FindCommandStart(args, globalOptions);
            var command = commandStart < args.Length ? args[commandStart] : null;
            var hasSkillEntry = HasRootEntry(catalog, "skill");
            var hasSessionEntry = HasRootEntry(catalog, "session");

            if (string.Equals(command, "session", StringComparison.Ordinal) && !hasSessionEntry)
            {
                return RunSessionCommand(args.Skip(commandStart + 1).ToArray(), session);
            }

            foreach (var service in host.Services.GetServices<IHostedService>())
            {
                if (service is ICliHostedService)
                {
                    await service.StartAsync(cancellationToken).ConfigureAwait(false);
                    started.Add(service);
                }
            }

            if (string.Equals(command, "skill", StringComparison.Ordinal) && !hasSkillEntry)
            {
                return await SkillGeneratorCommand.ExecuteAsync(
                    args.Skip(commandStart + 1).ToArray(),
                    catalog,
                    SkillOutputDirectory,
                    cancellationToken,
                    CreateSkillOptions(options)).ConfigureAwait(false);
            }

            var isRootHelp = command is null || command is "--help" or "-h";
            if (!isRootHelp)
            {
                await InitializeSessionAsync(host.Services, session, cancellationToken).ConfigureAwait(false);
            }

            var adapter = new OperationCommandLineAdapter(catalog, invoker: host.Services.GetRequiredService<OperationInvoker>(), CliRenderer ?? new ToonAgentOutputRenderer(), options: options);
            var result = await adapter.ExecuteAsync(args, cancellationToken).ConfigureAwait(false);
            if (isRootHelp && result.ExitCode == 0)
            {
                var hints = new List<string>();
                if (!hasSkillEntry)
                {
                    hints.Add("Skill commands:\n  skill generate [--out-dir <dir>] [--force]\n  skill check [--out-dir <dir>]");
                }

                if (!hasSessionEntry)
                {
                    hints.Add("Session commands:\n  session show\n  session clear");
                }

                if (hints.Count > 0)
                {
                    result = result with { Output = result.Output + "\n\n" + string.Join("\n\n", hints) };
                }
            }

            if (result.ExitCode == 0)
            {
                try
                {
                    session.Save();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return CommandLineExecutionResult.Failure($"The operation succeeded but session '{session.Id}' could not be saved to '{session.Path}': {exception.Message}");
                }
            }

            return result;
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

    private IReadOnlyList<CommandLineGlobalOption> GetCliGlobalOptions()
    {
        var options = new List<CommandLineGlobalOption>(_globalOptions);
        if (!options.Any(static option => OperationNaming.ToMatchKey(option.FlagName) == "session"))
        {
            options.Add(new CommandLineGlobalOption(
                "--session",
                ConfigKey: SessionConfigurationKey,
                Description: $"Session id for persisted state. Defaults to the {ResolveSessionEnvironmentVariable()} environment variable, then a hash of the working directory."));
        }

        return options;
    }

    private string ResolveSessionAppName()
    {
        if (!string.IsNullOrWhiteSpace(_sessionOptions.AppName))
        {
            return _sessionOptions.AppName!;
        }

        return System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "app";
    }

    private string ResolveSessionEnvironmentVariable()
    {
        var builder = new StringBuilder();
        foreach (var character in ResolveSessionAppName())
        {
            builder.Append(char.IsLetterOrDigit(character) ? char.ToUpperInvariant(character) : '_');
        }

        return builder.Append("_SESSION").ToString();
    }

    private FileAgentSession OpenSession(IReadOnlyDictionary<string, string> configuration)
    {
        var directory = FileAgentSession.GetSessionsDirectory(ResolveSessionAppName(), _sessionOptions.StateDirectory);
        string? id = configuration.TryGetValue(SessionConfigurationKey, out var flag) ? flag : null;
        id = string.IsNullOrWhiteSpace(id) ? Environment.GetEnvironmentVariable(ResolveSessionEnvironmentVariable()) : id;
        id = string.IsNullOrWhiteSpace(id)
            ? FileAgentSession.CreateWorkingDirectoryId(string.IsNullOrWhiteSpace(_sessionOptions.WorkingDirectory) ? Directory.GetCurrentDirectory() : _sessionOptions.WorkingDirectory!)
            : id.Trim();

        if (_sessionOptions.TimeToLive is { } timeToLive)
        {
            FileAgentSession.PruneExpired(directory, timeToLive, id);
        }

        return FileAgentSession.Open(directory, id);
    }

    private static async Task InitializeSessionAsync(IServiceProvider services, IAgentSession session, CancellationToken cancellationToken)
    {
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger<AgentSurfaceApplication>();
        foreach (var initializer in services.GetServices<IAgentSessionInitializer>())
        {
            try
            {
                await initializer.InitializeAsync(session, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                // A stale session (for example a moved assembly) must not block the very commands that repair it.
                logger?.LogWarning(exception, "Session initializer {Initializer} failed; continuing without its state.", initializer.GetType().Name);
            }
        }
    }

    private static CommandLineExecutionResult RunSessionCommand(string[] args, FileAgentSession session)
    {
        const string help = "Usage: session <show|clear>\n  show    Print the session id, file path and stored values.\n  clear   Delete the session file and its stored values.";
        switch (args)
        {
            case [] or ["--help" or "-h"]:
                return CommandLineExecutionResult.Success(help);
            case ["show"]:
                var lines = new List<string>
                {
                    $"id: {session.Id}",
                    $"path: {session.Path}",
                    $"exists: {(session.UpdatedAt is null ? "false" : "true")}",
                };
                if (session.UpdatedAt is { } updatedAt)
                {
                    lines.Add($"updatedAt: {updatedAt:O}");
                }

                var values = session.Values;
                lines.Add(values.Count == 0 ? "values: (none)" : "values:");
                lines.AddRange(values.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(pair => $"  {pair.Key}: {pair.Value.GetRawText()}"));
                return CommandLineExecutionResult.Success(string.Join("\n", lines)) with { IsVerbatimOutput = true };
            case ["clear"]:
                try
                {
                    var deleted = session.Clear();
                    return CommandLineExecutionResult.Success(deleted
                        ? $"Cleared session '{session.Id}' ({session.Path})."
                        : $"Session '{session.Id}' has no stored state ({session.Path}).") with { IsVerbatimOutput = true };
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return CommandLineExecutionResult.Failure($"Could not clear session '{session.Id}': {exception.Message}");
                }

            default:
                return CommandLineExecutionResult.Failure($"Unknown session command '{string.Join(' ', args)}'.\n\n{help}", 2);
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

    private static bool HasRootEntry(OperationCatalog catalog, string name) =>
        catalog.ForSurface(AgentSurface.Cli).Operations.Any(operation =>
            string.Equals(OperationCatalog.GetCategorySegments(operation.Category).FirstOrDefault() ?? operation.Name, name, StringComparison.OrdinalIgnoreCase));

    private OperationCommandLineOptions CreateCliOptions(IReadOnlyList<CommandLineGlobalOption> globalOptions)
    {
        var options = new OperationCommandLineOptions
        {
            GlobalOptions = [.. globalOptions],
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

    private void ConfigureCommon(HostApplicationBuilder builder, IAgentSession? session)
    {
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        foreach (var configure in _serviceConfigurations)
        {
            configure(builder);
        }

        if (session is not null)
        {
            builder.Services.TryAddSingleton(session);
        }
        else
        {
            builder.Services.TryAddSingleton<IAgentSession>(static _ => new InMemoryAgentSession());
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
