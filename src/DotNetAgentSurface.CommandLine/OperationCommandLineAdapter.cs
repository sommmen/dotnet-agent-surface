using System.Text.Json;
using DotNetAgentSurface.Core;

namespace DotNetAgentSurface.CommandLine;

public sealed class OperationCommandLineAdapter
{
    private readonly OperationCatalog _catalog;
    private readonly OperationInvoker _invoker;

    public OperationCommandLineAdapter(OperationCatalog catalog, OperationInvoker invoker)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
    }

    public async ValueTask<CommandLineExecutionResult> ExecuteAsync(string[] args, CancellationToken cancellationToken = default)
    {
        args ??= [];
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            return CommandLineExecutionResult.Success(RenderHelp());
        }

        var operation = _catalog.Operations.SingleOrDefault(candidate => string.Equals(candidate.Name, args[0], StringComparison.OrdinalIgnoreCase));
        if (operation is null)
        {
            return CommandLineExecutionResult.Failure($"Unknown operation '{args[0]}'.\n\n{RenderHelp()}");
        }

        if (args.Skip(1).Any(static arg => arg is "--help" or "-h"))
        {
            return CommandLineExecutionResult.Success(RenderOperationHelp(operation));
        }

        try
        {
            var inputs = ParseInputs(args.Skip(1).ToArray());
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

    private string RenderHelp() => "Available operations:\n" + string.Join("\n", _catalog.Operations.Select(operation => $"  {operation.Name,-20} {operation.Description}"));

    private static string RenderOperationHelp(OperationDescriptor operation) => $"{operation.Name}: {operation.Description}\n" + string.Join("\n", operation.Parameters.Where(parameter => !parameter.IsCancellationToken).Select(parameter => $"  --{parameter.Name} <{parameter.ParameterType.Name}>{(parameter.IsOptional ? " (optional)" : string.Empty)}"));

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
