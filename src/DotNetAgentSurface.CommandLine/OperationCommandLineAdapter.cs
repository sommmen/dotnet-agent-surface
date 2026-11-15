using System.Text.Json;
using DotNetAgentSurface.Core;

namespace DotNetAgentSurface.CommandLine;

/// <summary>
/// Routes command-line invocations to operations in an <see cref="OperationCatalog"/>, honoring each
/// operation's <see cref="OperationDescriptor.Category"/> as a nested command group.
/// </summary>
/// <remarks>
/// <para>
/// <b>Category-path convention:</b> a <see cref="OperationDescriptor.Category"/> is a whitespace-separated path
/// of command-group segments (see <see cref="OperationCatalog.GetCategorySegments"/>). For example, an operation
/// with <c>Category = "customers"</c> and <c>Name = "find-customer"</c> is invoked as
/// <c>myapp-cli customers find-customer --email ...</c>; an operation with <c>Category = "projects archived"</c>
/// is invoked as <c>myapp-cli projects archived &lt;name&gt;</c> (two nested command groups). Operations with a
/// null or blank category remain at the command-line root, e.g. <c>myapp-cli find-customer ...</c>. Category and
/// operation-name/alias matching is case-insensitive and treats <c>_</c> and <c>-</c> alike for operation names
/// (<c>run-query</c> invokes <c>run_query</c>); help output is sorted using ordinal string comparison and shows
/// the kebab-case form.
/// </para>
/// <para>
/// <b>Arguments:</b> operation inputs are flags after the operation name: <c>--name value</c>, <c>--name=value</c>,
/// bare boolean switches (<c>--verbose</c>) and <c>--no-verbose</c>, repeated flags for array parameters, kebab-case
/// aliases (<c>--context-name</c>), <c>@path</c> to read a value from a file (<c>@@</c> escapes a literal leading
/// <c>@</c>), and <c>-</c> to read a value from standard input. Text is parsed as JSON only for parameters that are
/// not string-like; <c>string</c>, <c>char</c>, <c>Guid</c> and enum parameters receive the text verbatim.
/// </para>
/// <para>
/// <b>Global flags</b> (<c>--output</c>/<c>--format</c>, <c>--fields</c>, <c>--full</c>, <c>--confirm</c>,
/// <c>--yes</c>, and host-declared <see cref="CommandLineGlobalOption"/>s) are recognized before the operation
/// name, and after it only when the operation does not declare a parameter with that name.
/// </para>
/// <para>
/// <b>Output:</b> a <see cref="string"/> result is written verbatim: never JSON-quoted, projected, or truncated.
/// Projection, <c>--fields</c> and <c>--full</c> apply to object results only.
/// </para>
/// </remarks>
public sealed class OperationCommandLineAdapter
{
    private static readonly string[] OutputKeys = ["output", "format"];

    private readonly OperationCatalog _catalog;
    private readonly OperationInvoker _invoker;
    private readonly CommandNode _root;
    private readonly IAgentOutputRenderer _defaultRenderer;
    private readonly bool _useAxiOutputContract;
    private readonly OperationInvocationContext? _invocationContext;
    private readonly OperationCommandLineOptions _options;
    private readonly Dictionary<string, CommandLineGlobalOption> _hostOptions;

    /// <summary>
    /// Creates a command-line adapter. Supplying a renderer enables the AXI output contract and makes that renderer
    /// the default format; omitting it preserves the historical JSON-only output for existing hosts.
    /// </summary>
    public OperationCommandLineAdapter(
        OperationCatalog catalog,
        OperationInvoker invoker,
        IAgentOutputRenderer? defaultRenderer = null,
        OperationInvocationContext? invocationContext = null,
        OperationCommandLineOptions? options = null)
    {
        if (catalog is null)
        {
            throw new ArgumentNullException(nameof(catalog));
        }

        _catalog = catalog.ForSurface(AgentSurface.Cli);
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _defaultRenderer = defaultRenderer ?? new JsonAgentOutputRenderer();
        _useAxiOutputContract = defaultRenderer is not null;
        _invocationContext = invocationContext;
        _options = options ?? new OperationCommandLineOptions();
        _hostOptions = new Dictionary<string, CommandLineGlobalOption>(StringComparer.Ordinal);
        foreach (var option in _options.GlobalOptions)
        {
            _hostOptions[OperationNaming.ToMatchKey(option.FlagName)] = option;
        }

        _root = BuildCommandTree(_catalog.Operations);
    }

    /// <summary>Executes one command line. Never throws for usage, operation, input, rendering, or cancellation failures; they map to exit codes 2, 1, 1 and 130.</summary>
    public async ValueTask<CommandLineExecutionResult> ExecuteAsync(
        string[] args,
        CancellationToken cancellationToken = default,
        OperationInvocationContext? invocationContext = null)
    {
        args ??= [];

        try
        {
            return await ExecuteCommandAsync(args, cancellationToken, invocationContext).ConfigureAwait(false);
        }
        catch (CommandLineUsageException exception)
        {
            return CommandLineExecutionResult.Failure(exception.Message, 2);
        }
        catch (OperationCanceledException)
        {
            return CommandLineExecutionResult.Failure("Operation was cancelled.", 130);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Standard input failures and exceptions from a custom renderer must not escape the adapter.
            return CommandLineExecutionResult.Failure(exception.Message);
        }
    }

    /// <summary>
    /// Returns the index of the first argument that is not a leading global flag (or a global flag's value), so a host can
    /// recognize its own verbs (<c>skill</c>, <c>session</c>) after flags such as <c>--connection x</c>. A help flag
    /// stops the scan and its index is returned.
    /// </summary>
    public static int FindCommandStart(string[] args, IReadOnlyList<CommandLineGlobalOption>? globalOptions = null)
    {
        if (args is null)
        {
            return 0;
        }

        var hostKeys = new HashSet<string>(
            (globalOptions ?? []).Select(static option => OperationNaming.ToMatchKey(option.FlagName)),
            StringComparer.Ordinal);
        var index = 0;
        while (index < args.Length && args[index] is not ("--help" or "-h") && TrySplitFlag(args[index], out var name, out var inline))
        {
            var key = OperationNaming.ToMatchKey(name);
            if (inline is null && (IsValueTakingBuiltin(key) || hostKeys.Contains(key)))
            {
                index++;
            }

            index++;
        }

        return Math.Min(index, args.Length);
    }

    /// <summary>
    /// Removes the global flags (and their values) from <paramref name="args"/> and returns what is left, so a host verb
    /// such as <c>session</c> or <c>skill</c> accepts <c>--session x</c> or <c>--format json</c> after the verb as well as
    /// before it. Recognizes the adapter's own flags and <paramref name="globalOptions"/>; <paramref name="exceptFlags"/>
    /// names flags (without dashes) that must be left in place because the verb uses them itself. A value-taking flag
    /// without a value is left in place so the verb rejects it. Everything else, including unknown flags, is kept.
    /// </summary>
    public static string[] RemoveGlobalFlags(string[] args, IReadOnlyList<CommandLineGlobalOption>? globalOptions = null, IEnumerable<string>? exceptFlags = null)
    {
        if (args is null)
        {
            return [];
        }

        var hostKeys = new HashSet<string>(
            (globalOptions ?? []).Select(static option => OperationNaming.ToMatchKey(option.FlagName)),
            StringComparer.Ordinal);
        var excluded = new HashSet<string>((exceptFlags ?? []).Select(OperationNaming.ToMatchKey), StringComparer.Ordinal);
        var kept = new List<string>(args.Length);
        for (var index = 0; index < args.Length; index++)
        {
            if (!TrySplitFlag(args[index], out var name, out var inline))
            {
                kept.Add(args[index]);
                continue;
            }

            var key = OperationNaming.ToMatchKey(name);
            if (excluded.Contains(key) || !(IsValueTakingBuiltin(key) || IsBooleanBuiltin(key) || hostKeys.Contains(key)))
            {
                kept.Add(args[index]);
                continue;
            }

            if (inline is not null || IsBooleanBuiltin(key))
            {
                continue;
            }

            if (index + 1 < args.Length && !(args[index + 1].StartsWith("--", StringComparison.Ordinal) && args[index + 1].Length > 2))
            {
                index++;
            }
            else
            {
                kept.Add(args[index]);
            }
        }

        return [.. kept];
    }

    /// <summary>
    /// Describes the global flags for a host verb's help: the adapter's own flags (without <paramref name="exceptFlags"/>)
    /// followed by <paramref name="globalOptions"/>.
    /// </summary>
    public static string RenderGlobalFlagHelp(IReadOnlyList<CommandLineGlobalOption>? globalOptions = null, IEnumerable<string>? exceptFlags = null)
    {
        var excluded = new HashSet<string>((exceptFlags ?? []).Select(OperationNaming.ToMatchKey), StringComparer.Ordinal);
        var lines = new List<string> { "Global flags (accepted before or after the command):" };
        if (!excluded.Contains("output"))
        {
            lines.Add("  --output <toon|json> (alias: --format)");
        }
        else if (!excluded.Contains("format"))
        {
            lines.Add("  --format <toon|json>");
        }

        lines.Add("  --fields <name,...>");
        lines.Add("  --full");
        lines.Add("  --confirm");
        lines.Add("  --yes");
        lines.AddRange((globalOptions ?? []).Select(static option =>
            $"  --{option.FlagName} <value>{(string.IsNullOrWhiteSpace(option.Description) ? string.Empty : " - " + option.Description)}"));
        return string.Join("\n", lines);
    }
    /// <summary>
    /// Reads the values of <paramref name="globalOptions"/> that declare a <see cref="CommandLineGlobalOption.ConfigKey"/>
    /// from <paramref name="args"/>, so a host can apply them to its configuration before building. The catalog does
    /// not exist yet at that point, so the flags are recognized anywhere in the arguments; avoid naming a configuration
    /// flag the same as an operation parameter.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadConfigurationValues(string[] args, IReadOnlyList<CommandLineGlobalOption> globalOptions)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (args is null || globalOptions is null || globalOptions.Count == 0)
        {
            return values;
        }

        var byKey = globalOptions.ToDictionary(option => OperationNaming.ToMatchKey(option.FlagName), StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            if (!TrySplitFlag(args[index], out var name, out var inline))
            {
                continue;
            }

            var key = OperationNaming.ToMatchKey(name);
            if (byKey.TryGetValue(key, out var option))
            {
                string? value = inline;
                if (value is null && index + 1 < args.Length)
                {
                    value = args[++index];
                }

                if (value is not null && option.ConfigKey is not null)
                {
                    values[option.ConfigKey] = value;
                }
            }
            else if (inline is null && IsValueTakingBuiltin(key))
            {
                index++;
            }
        }

        return values;
    }

    private async ValueTask<CommandLineExecutionResult> ExecuteCommandAsync(
        string[] args,
        CancellationToken cancellationToken,
        OperationInvocationContext? invocationContext)
    {
        var state = new GlobalState();
        var node = _root;
        var index = 0;
        var pathSegments = new List<string>();

        while (index < args.Length && args[index] is not ("--help" or "-h"))
        {
            var token = args[index];

            if (TrySplitFlag(token, out var flagName, out var inlineValue))
            {
                if (!TryApplyGlobal(flagName, inlineValue, args, ref index, state))
                {
                    throw new CommandLineUsageException(
                        $"Unknown flag '{token}'. Flags before the operation name must be global flags; operation flags go after the operation name.\n\n{RenderGlobalFlags()}");
                }

                index++;
                continue;
            }

            if (node.Operations.TryGetValue(token, out var operation))
            {
                index++;
                return await ExecuteOperationAsync(operation, args, index, cancellationToken, state, invocationContext).ConfigureAwait(false);
            }

            if (node.Categories.TryGetValue(token, out var child))
            {
                node = child;
                pathSegments.Add(token);
                index++;
                continue;
            }

            return CommandLineExecutionResult.Failure(
                $"Unknown operation '{token}'.\n\n{RenderNodeHelp(node, pathSegments)}",
                2);
        }

        return CommandLineExecutionResult.Success(RenderNodeHelp(node, pathSegments));
    }

    private async ValueTask<CommandLineExecutionResult> ExecuteOperationAsync(
        OperationDescriptor operation,
        string[] args,
        int startIndex,
        CancellationToken cancellationToken,
        GlobalState state,
        OperationInvocationContext? invocationContext)
    {
        var remaining = args.Skip(startIndex).ToArray();

        if (remaining.Any(static arg => arg is "--help" or "-h"))
        {
            return CommandLineExecutionResult.Success(RenderOperationHelp(operation));
        }

        var inputs = ParseInputs(operation, remaining, state);
        InjectGlobalValues(operation, inputs, state);
        cancellationToken.ThrowIfCancellationRequested();
        var confirmation = state.Confirm && state.Yes
            ? OperationConfirmation.DangerousConfirmed
            : state.Confirm
                ? OperationConfirmation.Confirmed
                : OperationConfirmation.None;
        var invocation = await _invoker.InvokeAsync(
            operation,
            inputs,
            cancellationToken,
            confirmation,
            WithSurface(invocationContext ?? _invocationContext)).ConfigureAwait(false);

        if (!invocation.Succeeded)
        {
            var error = invocation.ErrorIsVerbatim && !string.IsNullOrWhiteSpace(invocation.Error)
                ? invocation.Error!
                : FormatOperationError(invocation.Error);
            return CommandLineExecutionResult.Failure(error, invocation.IsCancelled ? 130 : 1);
        }

        if (operation.IsIdempotent && invocation.Value is OperationNoOp noOp)
        {
            return CommandLineExecutionResult.Success(noOp.Message);
        }

        if (invocation.Value is string text)
        {
            return CommandLineExecutionResult.Success(text) with { IsVerbatimOutput = true };
        }

        var explicitFormat = state.Renderer is not null || state.Fields is not null || state.Full;
        if (!explicitFormat && invocation.Value is not null && _options.ResultRenderer?.Invoke(operation, invocation.Value) is { } rendered)
        {
            return CommandLineExecutionResult.Success(rendered);
        }

        if (!_useAxiOutputContract && !explicitFormat)
        {
            // Legacy behavior: no renderer or AXI-specific output option was configured for this adapter instance,
            // so preserve the original raw-JSON output exactly for backward compatibility.
            return CommandLineExecutionResult.Success(JsonSerializer.Serialize(invocation.Value));
        }

        var projection = AgentOutputProjector.Project(invocation.Value, state.Fields, state.Full);
        var renderer = state.Renderer ?? _defaultRenderer;
        return CommandLineExecutionResult.Success(renderer.Render(projection));
    }

    private static OperationInvocationContext WithSurface(OperationInvocationContext? context) =>
        context is null
            ? new OperationInvocationContext { Surface = AgentSurface.Cli }
            : context with { Surface = AgentSurface.Cli };

    private string FormatOperationError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return "Operation failed. Check the supplied inputs and try again.";
        }

        var message = error!.TrimEnd();
        return _options.FlattenOperationErrors
            ? message.Replace("\r", " ").Replace("\n", " ").Trim()
            : message;
    }

    private static bool TrySplitFlag(string token, out string name, out string? inlineValue)
    {
        name = string.Empty;
        inlineValue = null;
        if (token.Length < 2 || token[0] != '-')
        {
            return false;
        }

        var body = token.StartsWith("--", StringComparison.Ordinal) ? token.Substring(2) : token.Substring(1);
        var equals = body.IndexOf('=');
        if (equals >= 0)
        {
            name = body.Substring(0, equals);
            inlineValue = body.Substring(equals + 1);
        }
        else
        {
            name = body;
        }

        return name.Length > 0;
    }

    private static bool IsValueTakingBuiltin(string key) => key is "output" or "format" or "fields";

    private static bool IsBooleanBuiltin(string key) => key is "full" or "confirm" or "yes";

    /// <summary>Applies a global flag; <paramref name="index"/> is advanced past a consumed value. Returns false when the flag is not global.</summary>
    private bool TryApplyGlobal(string name, string? inlineValue, string[] args, ref int index, GlobalState state)
    {
        var key = OperationNaming.ToMatchKey(name);
        var display = "--" + name;

        if (OutputKeys.Contains(key))
        {
            var value = RequireValue(display, inlineValue, args, ref index);
            if (state.Renderer is not null)
            {
                throw new CommandLineUsageException("Specify --output at most once.");
            }

            state.Renderer = value.ToLowerInvariant() switch
            {
                "toon" => new ToonAgentOutputRenderer(),
                "json" => new JsonAgentOutputRenderer(),
                _ => throw new CommandLineUsageException($"Unknown --output value '{value}'. Expected 'toon' or 'json'."),
            };
            return true;
        }

        if (key == "fields")
        {
            var value = RequireValue(display, inlineValue, args, ref index);
            if (state.Fields is not null)
            {
                throw new CommandLineUsageException("Specify --fields at most once.");
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new CommandLineUsageException("The --fields flag requires a comma-separated list of field names.");
            }

            var fields = value
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(static field => field.Trim())
                .Where(static field => field.Length > 0)
                .ToArray();
            if (fields.Length == 0)
            {
                throw new CommandLineUsageException("The --fields flag requires at least one field name.");
            }

            state.Fields = fields;
            return true;
        }

        if (IsBooleanBuiltin(key))
        {
            var enabled = inlineValue is null || ParseSwitch(display, inlineValue);
            switch (key)
            {
                case "full":
                    state.Full = ClaimSwitch(display, state.Full, enabled);
                    break;
                case "confirm":
                    state.Confirm = ClaimSwitch(display, state.Confirm, enabled);
                    break;
                default:
                    state.Yes = ClaimSwitch(display, state.Yes, enabled);
                    break;
            }

            return true;
        }

        if (_hostOptions.TryGetValue(key, out var host))
        {
            state.HostValues[host.InjectAs ?? host.FlagName] = RequireValue(display, inlineValue, args, ref index);
            return true;
        }

        return false;
    }

    private static bool ClaimSwitch(string display, bool alreadySet, bool enabled) =>
        alreadySet ? throw new CommandLineUsageException($"Specify {display} at most once.") : enabled;

    private static bool ParseSwitch(string display, string value) =>
        bool.TryParse(value, out var parsed)
            ? parsed
            : throw new CommandLineUsageException($"'{value}' is not a valid value for {display}. Expected 'true' or 'false'.");

    private static string RequireValue(string display, string? inlineValue, string[] args, ref int index)
    {
        if (inlineValue is not null)
        {
            return inlineValue;
        }

        if (index + 1 >= args.Length || (args[index + 1].StartsWith("--", StringComparison.Ordinal) && args[index + 1].Length > 2))
        {
            throw new CommandLineUsageException($"The {display} flag requires a value. Use '{display} <value>' or '{display}=<value>'.");
        }

        return args[++index];
    }

    private Dictionary<string, JsonElement> ParseInputs(OperationDescriptor operation, string[] arguments, GlobalState state)
    {
        var parameters = operation.Parameters.Where(static parameter => !parameter.IsAmbient).ToArray();
        var byKey = new Dictionary<string, OperationParameterDescriptor>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            byKey[OperationNaming.ToMatchKey(parameter.Name)] = parameter;
        }

        var inputs = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var collections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var stdinConsumed = false;

        for (var index = 0; index < arguments.Length; index++)
        {
            var token = arguments[index];
            if (!TrySplitFlag(token, out var name, out var inlineValue))
            {
                throw new CommandLineUsageException($"Unexpected argument '{token}'. Supply inputs as flags: --name value or --name=value.");
            }

            var key = OperationNaming.ToMatchKey(name);
            var isLong = token.StartsWith("--", StringComparison.Ordinal);

            if (isLong && byKey.TryGetValue(key, out var parameter))
            {
                if (CommandLineValueConverter.IsBoolean(parameter.ParameterType))
                {
                    inputs[parameter.Name] = ReadBooleanSwitch(token, inlineValue, arguments, ref index);
                    continue;
                }

                var value = ResolveValue(RequireValue("--" + name, inlineValue, arguments, ref index), ref stdinConsumed);
                if (CommandLineValueConverter.TryGetCollectionElementType(parameter.ParameterType, out _))
                {
                    if (!collections.TryGetValue(parameter.Name, out var list))
                    {
                        collections[parameter.Name] = list = [];
                    }

                    list.Add(value);
                    continue;
                }

                inputs[parameter.Name] = CommandLineValueConverter.ToJson(parameter.ParameterType, value);
                continue;
            }

            if (isLong && inlineValue is null && name.StartsWith("no-", StringComparison.OrdinalIgnoreCase)
                && byKey.TryGetValue(OperationNaming.ToMatchKey(name.Substring(3)), out var negated)
                && CommandLineValueConverter.IsBoolean(negated.ParameterType))
            {
                inputs[negated.Name] = JsonSerializer.SerializeToElement(false);
                continue;
            }

            if (isLong && TryApplyGlobal(name, inlineValue, arguments, ref index, state))
            {
                continue;
            }

            var validFlags = parameters.Select(static parameter => "--" + OperationNaming.ToKebabCase(parameter.Name))
                .Concat(GlobalFlagNames());
            throw new CommandLineUsageException($"Unknown flag '{token}' for operation '{operation.Name}'. Valid flags: {string.Join(", ", validFlags)}.");
        }

        foreach (var entry in collections)
        {
            var parameter = parameters.First(candidate => string.Equals(candidate.Name, entry.Key, StringComparison.OrdinalIgnoreCase));
            CommandLineValueConverter.TryGetCollectionElementType(parameter.ParameterType, out var elementType);
            inputs[parameter.Name] = CommandLineValueConverter.ToJsonArray(elementType, entry.Value);
        }

        return inputs;
    }

    private static JsonElement ReadBooleanSwitch(string token, string? inlineValue, string[] arguments, ref int index)
    {
        if (inlineValue is not null)
        {
            return JsonSerializer.SerializeToElement(ParseSwitch(token, inlineValue));
        }

        if (index + 1 < arguments.Length && bool.TryParse(arguments[index + 1], out var explicitValue))
        {
            index++;
            return JsonSerializer.SerializeToElement(explicitValue);
        }

        return JsonSerializer.SerializeToElement(true);
    }

    private string ResolveValue(string value, ref bool stdinConsumed)
    {
        if (value == "-")
        {
            if (stdinConsumed)
            {
                throw new CommandLineUsageException("Standard input ('-') can only supply one value.");
            }

            stdinConsumed = true;
            return (_options.Input ?? Console.In).ReadToEnd();
        }

        if (value.StartsWith("@@", StringComparison.Ordinal))
        {
            return value.Substring(1);
        }

        if (value.StartsWith("@", StringComparison.Ordinal) && value.Length > 1)
        {
            var path = value.Substring(1);
            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new CommandLineUsageException($"Cannot read value from file '{path}': {exception.Message} Use '@@' for a literal leading '@'.");
            }
        }

        return value;
    }

    private void InjectGlobalValues(OperationDescriptor operation, Dictionary<string, JsonElement> inputs, GlobalState state)
    {
        foreach (var option in _options.GlobalOptions)
        {
            if (option.InjectAs is null || !state.HostValues.TryGetValue(option.InjectAs, out var value))
            {
                continue;
            }

            var parameter = operation.Parameters.FirstOrDefault(candidate =>
                !candidate.IsAmbient
                && OperationNaming.ToMatchKey(candidate.Name) == OperationNaming.ToMatchKey(option.InjectAs));
            if (parameter is not null && !inputs.ContainsKey(parameter.Name))
            {
                inputs[parameter.Name] = CommandLineValueConverter.ToJson(parameter.ParameterType, value);
            }
        }
    }

    private IEnumerable<string> GlobalFlagNames() =>
        new[] { "--output", "--format", "--fields", "--full", "--confirm", "--yes", "--help", "-h" }
            .Concat(_options.GlobalOptions.Select(static option => "--" + option.FlagName));

    private sealed class GlobalState
    {
        public IAgentOutputRenderer? Renderer { get; set; }

        public IReadOnlyList<string>? Fields { get; set; }

        public bool Full { get; set; }
        public bool Confirm { get; set; }

        public bool Yes { get; set; }

        public Dictionary<string, string> HostValues { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds the category tree once from the catalog's operations, keyed case-insensitively at every level, so
    /// routing and help rendering never need to re-scan the flat operation list.
    /// </summary>
    private static CommandNode BuildCommandTree(IReadOnlyList<OperationDescriptor> operations)
    {
        var root = new CommandNode();

        foreach (var operation in operations)
        {
            var segments = OperationCatalog.GetCategorySegments(operation.Category);
            var node = root;
            foreach (var segment in segments)
            {
                if (!node.Categories.TryGetValue(segment, out var child))
                {
                    child = new CommandNode();
                    node.Categories[segment] = child;
                }

                node = child;
            }

            node.Operations[operation.Name] = operation;
            foreach (var alias in operation.Aliases)
            {
                node.Operations[alias] = operation;
            }
        }

        AddKebabAliases(root);
        return root;
    }

    private static void AddKebabAliases(CommandNode node)
    {
        foreach (var entry in node.Operations.ToArray())
        {
            var kebab = OperationNaming.ToKebabCase(entry.Key);
            if (!node.Operations.ContainsKey(kebab))
            {
                node.Operations[kebab] = entry.Value;
            }
        }

        foreach (var child in node.Categories.Values)
        {
            AddKebabAliases(child);
        }
    }

    private string RenderNodeHelp(CommandNode node, IReadOnlyList<string> pathSegments)
    {
        var prefix = pathSegments.Count == 0 ? string.Empty : string.Join(" ", pathSegments) + " ";
        var lines = new List<string>
        {
            pathSegments.Count == 0
                ? "Available operations:"
                : $"Available operations under '{string.Join(" ", pathSegments)}':",
        };

        var operationNames = node.Operations.Values
            .Distinct()
            .Select(operation => (Name: OperationNaming.ToKebabCase(operation.Name), operation.Description))
            .OrderBy(static operation => operation.Name, StringComparer.Ordinal)
            .Select(operation => $"  {prefix}{operation.Name,-20} {operation.Description}");
        lines.AddRange(operationNames);

        if (node.Categories.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Categories:");
            var categoryLines = node.Categories
                .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => $"  {prefix}{entry.Key,-20} ({CountOperations(entry.Value)} operations)");
            lines.AddRange(categoryLines);
        }

        if (pathSegments.Count == 0 && node.Categories.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Use '<category> [<sub-category> ...] --help' to explore a category, then invoke an operation by its displayed path.");
        }

        return string.Join("\n", lines);
    }

    private static int CountOperations(CommandNode node) =>
        node.Operations.Values.Distinct().Count() + node.Categories.Values.Sum(CountOperations);

    private string RenderOperationHelp(OperationDescriptor operation)
    {
        var parameters = operation.Parameters.Where(static parameter => !parameter.IsAmbient).ToArray();
        var commandPath = string.Join(" ", OperationCatalog.GetCategorySegments(operation.Category).Append(OperationNaming.ToKebabCase(operation.Name)));
        var usage = string.Join(" ", parameters.Select(parameter =>
        {
            var text = $"--{OperationNaming.ToKebabCase(parameter.Name)}{(CommandLineValueConverter.IsBoolean(parameter.ParameterType) ? string.Empty : $" <{FriendlyTypeName(parameter.ParameterType)}>")}";
            return IsRequired(parameter) ? text : $"[{text}]";
        }));
        var lines = new List<string>
        {
            $"{OperationNaming.ToKebabCase(operation.Name)}: {operation.Description}",
            string.Empty,
            $"Usage: {commandPath}{(usage.Length > 0 ? " " + usage : string.Empty)}",
        };

        if (parameters.Length > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Operation flags:");
            lines.AddRange(parameters.Select(RenderParameterHelp));
        }

        if (operation.Examples.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Examples:");
            lines.AddRange(operation.Examples.Select(example => $"  {commandPath}{(string.IsNullOrWhiteSpace(example) ? string.Empty : " " + example.Trim())}"));
        }

        lines.Add(string.Empty);
        lines.Add(RenderGlobalFlags());
        return string.Join("\n", lines);
    }

    private static bool IsRequired(OperationParameterDescriptor parameter) => !parameter.IsOptional && !parameter.IsNullable;

    private static string RenderParameterHelp(OperationParameterDescriptor parameter)
    {
        var flag = $"--{OperationNaming.ToKebabCase(parameter.Name)}";
        var isBoolean = CommandLineValueConverter.IsBoolean(parameter.ParameterType);
        var text = isBoolean ? $"  {flag} [true|false]" : $"  {flag} <{FriendlyTypeName(parameter.ParameterType)}>";
        var traits = new List<string> { IsRequired(parameter) ? "required" : "optional" };
        if (parameter.IsOptional && parameter.DefaultValue is not null && parameter.DefaultValue is not DBNull)
        {
            traits.Add($"default: {parameter.DefaultValue}");
        }

        if (isBoolean)
        {
            traits.Add($"--no-{OperationNaming.ToKebabCase(parameter.Name)} sets false");
        }
        else if (CommandLineValueConverter.TryGetCollectionElementType(parameter.ParameterType, out _))
        {
            traits.Add("repeatable");
        }
        else if ((Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType).IsEnum)
        {
            traits.Add("values: " + string.Join("|", Enum.GetNames(Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType)));
        }

        text += $" ({string.Join(", ", traits)})";
        return string.IsNullOrWhiteSpace(parameter.Description) ? text : $"{text} - {parameter.Description}";
    }

    private string RenderGlobalFlags()
    {
        var lines = new List<string>
        {
            "Global flags (before the operation name, or after it when the operation has no parameter of that name):",
            "  --output <toon|json> (alias: --format)",
            "  --fields <name,...>",
            "  --full",
            "  --confirm",
            "  --yes (requires --confirm for dangerous operations)",
            "  --help, -h",
        };
        lines.AddRange(_options.GlobalOptions.Select(option =>
            $"  --{option.FlagName} <value>{(string.IsNullOrWhiteSpace(option.Description) ? string.Empty : " - " + option.Description)}{(option.InjectAs is null ? string.Empty : $" (sets '{OperationNaming.ToKebabCase(option.InjectAs)}' on operations that declare it)")}"));
        lines.Add("Values: '@path' reads a file ('@@' for a literal '@'), '-' reads standard input.");
        return string.Join("\n", lines);
    }

    private static string FriendlyTypeName(Type type)
    {
        var nullable = Nullable.GetUnderlyingType(type);
        if (nullable is not null)
        {
            return FriendlyTypeName(nullable) + "?";
        }

        if (type.IsArray)
        {
            return FriendlyTypeName(type.GetElementType()!) + "[]";
        }

        if (type.IsGenericType)
        {
            var name = type.Name.Substring(0, type.Name.IndexOf('`'));
            return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(FriendlyTypeName))}>";
        }

        return type.FullName switch
        {
            "System.String" => "string",
            "System.Boolean" => "bool",
            "System.Int32" => "int",
            "System.Int64" => "long",
            "System.Int16" => "short",
            "System.Double" => "double",
            "System.Single" => "float",
            "System.Decimal" => "decimal",
            "System.Char" => "char",
            "System.Object" => "json",
            _ => type.Name,
        };
    }

    /// <summary>
    /// A single level of the category tree: operations reachable directly at this level (keyed by name and by
    /// each alias, case-insensitively) plus nested category nodes (keyed by category segment, case-insensitively).
    /// </summary>
    private sealed class CommandNode
    {
        public Dictionary<string, OperationDescriptor> Operations { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, CommandNode> Categories { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
