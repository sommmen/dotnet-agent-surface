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

    public OperationCommandLineAdapter(OperationCatalog catalog, OperationInvoker invoker)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _root = BuildCommandTree(_catalog.Operations);
    }

    public async ValueTask<CommandLineExecutionResult> ExecuteAsync(string[] args, CancellationToken cancellationToken = default)
    {
        args ??= [];

        var node = _root;
        var index = 0;
        var pathSegments = new List<string>();

        while (index < args.Length && args[index] is not ("--help" or "-h"))
        {
            var token = args[index];

            if (node.Operations.TryGetValue(token, out var operation))
            {
                index++;
                return await ExecuteOperationAsync(operation, args, index, cancellationToken).ConfigureAwait(false);
            }

            if (node.Categories.TryGetValue(token, out var child))
            {
                node = child;
                pathSegments.Add(token);
                index++;
                continue;
            }

            return CommandLineExecutionResult.Failure(
                $"Unknown operation '{token}'.\n\n{RenderNodeHelp(node, pathSegments)}");
        }

        // Ran out of tokens (or hit --help/-h) while still inside a category: show category-scoped (or root) help.
        return CommandLineExecutionResult.Success(RenderNodeHelp(node, pathSegments));
    }

    private async ValueTask<CommandLineExecutionResult> ExecuteOperationAsync(
        OperationDescriptor operation,
        string[] args,
        int startIndex,
        CancellationToken cancellationToken)
    {
        var remaining = args.Skip(startIndex).ToArray();

        if (remaining.Any(static arg => arg is "--help" or "-h"))
        {
            return CommandLineExecutionResult.Success(RenderOperationHelp(operation));
        }

        try
        {
            var inputs = ParseInputs(remaining);
            var invocation = await _invoker.InvokeAsync(operation, inputs, cancellationToken).ConfigureAwait(false);
            return invocation.Succeeded
                ? CommandLineExecutionResult.Success(JsonSerializer.Serialize(invocation.Value))
                : CommandLineExecutionResult.Failure(invocation.Error ?? "Operation failed.", invocation.IsCancelled ? 130 : 1);
        }
        catch (CommandLineUsageException exception)
        {
            return CommandLineExecutionResult.Failure(exception.Message, 2);
        }
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

    private static string RenderOperationHelp(OperationDescriptor operation) => $"{operation.Name}: {operation.Description}\n" + string.Join("\n", operation.Parameters.Where(parameter => !parameter.IsCancellationToken).Select(parameter => $"  --{parameter.Name} <{parameter.ParameterType.Name}>{(parameter.IsOptional ? " (optional)" : string.Empty)}"));

    /// <summary>
    /// A single level of the category tree: operations reachable directly at this level (keyed by name and by
    /// each alias, case-insensitively) plus nested category nodes (keyed by category segment, case-insensitively).
    /// </summary>
    private sealed class CommandNode
    {
        public Dictionary<string, OperationDescriptor> Operations { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, CommandNode> Categories { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, JsonElement> ParseInputs(string[] arguments)
    {
        var inputs = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < arguments.Length; index += 2)
        {
            if (!arguments[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= arguments.Length)
            {
                throw new CommandLineUsageException("Inputs must be supplied as --name JSON-value pairs.");
            }

            var name = arguments[index][2..];
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
