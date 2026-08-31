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
/// operation-name/alias matching is case-insensitive; help output is sorted using ordinal string comparison.
/// </para>
/// </remarks>
public sealed class OperationCommandLineAdapter
{
    private readonly OperationCatalog _catalog;
    private readonly OperationInvoker _invoker;
    private readonly CommandNode _root;
    private readonly IAgentOutputRenderer _defaultRenderer;
    private readonly bool _useAxiOutputContract;

    /// <summary>
    /// Creates a command-line adapter. Supplying a renderer enables the AXI output contract and makes that renderer
    /// the default format; omitting it preserves the historical JSON-only output for existing hosts.
    /// </summary>
    public OperationCommandLineAdapter(
        OperationCatalog catalog,
        OperationInvoker invoker,
        IAgentOutputRenderer? defaultRenderer = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _defaultRenderer = defaultRenderer ?? new JsonAgentOutputRenderer();
        _useAxiOutputContract = defaultRenderer is not null;
        _root = BuildCommandTree(_catalog.Operations);
    }

    public async ValueTask<CommandLineExecutionResult> ExecuteAsync(string[] args, CancellationToken cancellationToken = default)
    {
        args ??= [];

        try
        {
            var outputOptions = ParseOutputOptions(args);
            return await ExecuteCommandAsync(outputOptions.CommandArgs, cancellationToken, outputOptions).ConfigureAwait(false);
        }
        catch (CommandLineUsageException exception)
        {
            return CommandLineExecutionResult.Failure(exception.Message, 2);
        }
        catch (OperationCanceledException)
        {
            return CommandLineExecutionResult.Failure("Operation was cancelled.", 130);
        }
    }

    private async ValueTask<CommandLineExecutionResult> ExecuteCommandAsync(
        string[] args,
        CancellationToken cancellationToken,
        OutputOptions outputOptions)
    {
        var node = _root;
        var index = 0;
        var pathSegments = new List<string>();

        while (index < args.Length && args[index] is not ("--help" or "-h"))
        {
            var token = args[index];

            if (node.Operations.TryGetValue(token, out var operation))
            {
                index++;
                return await ExecuteOperationAsync(operation, args, index, cancellationToken, outputOptions).ConfigureAwait(false);
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

        // Ran out of tokens (or hit --help/-h) while still inside a category: show category-scoped (or root) help.
        return CommandLineExecutionResult.Success(RenderNodeHelp(node, pathSegments));
    }

    private async ValueTask<CommandLineExecutionResult> ExecuteOperationAsync(
        OperationDescriptor operation,
        string[] args,
        int startIndex,
        CancellationToken cancellationToken,
        OutputOptions outputOptions)
    {
        var remaining = args.Skip(startIndex).ToArray();

        if (remaining.Any(static arg => arg is "--help" or "-h"))
        {
            return CommandLineExecutionResult.Success(RenderOperationHelp(operation));
        }

        try
        {
            var inputs = ParseInputs(operation, remaining);
            var invocation = await _invoker.InvokeAsync(operation, inputs, cancellationToken).ConfigureAwait(false);

            if (!invocation.Succeeded)
            {
                return CommandLineExecutionResult.Failure(CleanOperationError(invocation.Error), invocation.IsCancelled ? 130 : 1);
            }

            if (operation.IsIdempotent && invocation.Value is OperationNoOp noOp)
            {
                return CommandLineExecutionResult.Success(noOp.Message);
            }

            if (!_useAxiOutputContract && outputOptions.Renderer is null && outputOptions.Fields is null && !outputOptions.Full)
            {
                // Legacy behavior: no renderer or AXI-specific output option was configured for this adapter instance,
                // so preserve the original raw-JSON output exactly for backward compatibility.
                return CommandLineExecutionResult.Success(JsonSerializer.Serialize(invocation.Value));
            }

            var projection = AgentOutputProjector.Project(invocation.Value, outputOptions.Fields, outputOptions.Full);
            var renderer = outputOptions.Renderer ?? _defaultRenderer;
            return CommandLineExecutionResult.Success(renderer.Render(projection));
        }
        catch (CommandLineUsageException exception)
        {
            return CommandLineExecutionResult.Failure(exception.Message, 2);
        }
    }

    /// <summary>
    /// Strips exception-style noise (stack traces, provider dumps) from operation error text so CLI users get an
    /// actionable one-line message rather than internal implementation detail.
    /// </summary>
    private static string CleanOperationError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return "Operation failed. Check the supplied inputs and try again.";
        }

        var message = error!;
        return message.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    /// <summary>
    /// Extracts and validates the global <c>--output</c>/<c>--fields</c>/<c>--full</c> flags from anywhere in the
    /// argument list, returning the remaining command/operation arguments untouched.
    /// </summary>
    private static OutputOptions ParseOutputOptions(string[] args)
    {
        var commandArgs = new List<string>();
        IAgentOutputRenderer? renderer = null;
        IReadOnlyList<string>? fields = null;
        var full = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--output":
                    if (renderer is not null)
                    {
                        throw new CommandLineUsageException("Specify --output at most once.");
                    }

                    if (++index >= args.Length)
                    {
                        throw new CommandLineUsageException("The --output flag requires a value of 'toon' or 'json'.");
                    }

                    renderer = args[index].ToLowerInvariant() switch
                    {
                        "toon" => new ToonAgentOutputRenderer(),
                        "json" => new JsonAgentOutputRenderer(),
                        _ => throw new CommandLineUsageException($"Unknown --output value '{args[index]}'. Expected 'toon' or 'json'."),
                    };
                    break;
                case "--fields":
                    if (fields is not null)
                    {
                        throw new CommandLineUsageException("Specify --fields at most once.");
                    }

                    if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                    {
                        throw new CommandLineUsageException("The --fields flag requires a comma-separated list of field names.");
                    }

                    fields = args[index]
                        .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(static field => field.Trim())
                        .Where(static field => field.Length > 0)
                        .ToArray();
                    if (fields.Count == 0)
                    {
                        throw new CommandLineUsageException("The --fields flag requires at least one field name.");
                    }

                    break;
                case "--full":
                    if (full)
                    {
                        throw new CommandLineUsageException("Specify --full at most once.");
                    }

                    full = true;
                    break;
                default:
                    commandArgs.Add(args[index]);
                    break;
            }
        }

        return new OutputOptions(commandArgs.ToArray(), renderer, fields, full);
    }

    private sealed record OutputOptions(string[] CommandArgs, IAgentOutputRenderer? Renderer, IReadOnlyList<string>? Fields, bool Full);

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

        return root;
    }

    /// <summary>
    /// Renders help for a node in the command tree: at the root this is uncategorized operations plus known
    /// top-level categories; within a category it is that category's own operations plus its nested
    /// sub-categories. Categories and operations are each sorted ordinally by name (requirement: deterministic,
    /// ordinal-sorted help output).
    /// </summary>
    private static string RenderNodeHelp(CommandNode node, IReadOnlyList<string> pathSegments)
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

        return string.Join("\n", lines);
    }

    private static int CountOperations(CommandNode node) =>
        node.Operations.Values.Distinct().Count() + node.Categories.Values.Sum(CountOperations);

    private static string RenderOperationHelp(OperationDescriptor operation)
    {
        var lines = new List<string> { $"{operation.Name}: {operation.Description}", "", "Operation flags:" };
        lines.AddRange(operation.Parameters
            .Where(parameter => !parameter.IsCancellationToken)
            .Select(parameter => $"  --{parameter.Name} <{parameter.ParameterType.Name}>{(parameter.IsOptional ? " (optional)" : string.Empty)}"));
        lines.AddRange([
            "", "Global flags:", "  --output <toon|json>", "  --fields <name,...>", "  --full", "  --help, -h",
        ]);
        return string.Join("\n", lines);
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

    private static IReadOnlyDictionary<string, JsonElement> ParseInputs(OperationDescriptor operation, string[] arguments)
    {
        var validInputNames = operation.Parameters
            .Where(parameter => !parameter.IsCancellationToken)
            .Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var validFlags = validInputNames.Select(name => $"--{name}")
            .Append("--output")
            .Append("--fields")
            .Append("--full")
            .Append("--help")
            .Append("-h");
        var inputs = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < arguments.Length; index += 2)
        {
            if (!arguments[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= arguments.Length)
            {
                throw new CommandLineUsageException("Inputs must be supplied as --name JSON-value pairs.");
            }

            var name = arguments[index][2..];
            if (!validInputNames.Contains(name))
            {
                throw new CommandLineUsageException($"Unknown flag '--{name}' for operation '{operation.Name}'. Valid flags: {string.Join(", ", validFlags)}.");
            }

            try
            {
                inputs[name] = JsonDocument.Parse(arguments[index + 1]).RootElement.Clone();
            }
            catch (JsonException)
            {
                inputs[name] = JsonSerializer.SerializeToElement(arguments[index + 1]);
            }
        }

        return inputs;
    }
}

public sealed record CommandLineExecutionResult(int ExitCode, string Output, string? Error)
{
    public static CommandLineExecutionResult Success(string output) => new(0, output, null);

    public static CommandLineExecutionResult Failure(string error, int exitCode = 1) => new(exitCode, string.Empty, error);
}

public sealed class CommandLineUsageException : Exception
{
    public CommandLineUsageException(string message)
        : base(message)
    {
    }
}
