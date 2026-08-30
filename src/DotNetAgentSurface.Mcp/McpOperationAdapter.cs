using System.Text.Json;
using DotNetAgentSurface.Core;
using ModelContextProtocol.Protocol;

namespace DotNetAgentSurface.Mcp;

/// <summary>Exposes an <see cref="OperationCatalog"/> as MCP tool descriptors and invocations.</summary>
public sealed class McpOperationAdapter
{
    private readonly OperationCatalog _catalog;
    private readonly OperationInvoker _invoker;
    private readonly OperationSchemaGenerator _schemaGenerator;

    public McpOperationAdapter(
        OperationCatalog catalog,
        OperationInvoker invoker,
        OperationSchemaGenerator? schemaGenerator = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _schemaGenerator = schemaGenerator ?? new OperationSchemaGenerator();
    }

    public IReadOnlyList<Tool> GetTools() => _catalog.Operations
        .Select(CreateTool)
        .ToArray();

    public async ValueTask<CallToolResult> InvokeAsync(
        string name,
        IReadOnlyDictionary<string, JsonElement>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var operation = _catalog.Operations.FirstOrDefault(
            operation => string.Equals(operation.Name, name, StringComparison.OrdinalIgnoreCase));
        if (operation is null)
        {
            return Error($"Unknown operation '{name}'.");
        }

        var invocation = await _invoker.InvokeAsync(operation, arguments, cancellationToken).ConfigureAwait(false);
        if (invocation.IsCancelled)
        {
            return Error("Operation was cancelled.");
        }

        if (!invocation.Succeeded)
        {
            return Error(invocation.Error ?? "Operation failed.");
        }

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(invocation.Value) }]
        };
    }

    private Tool CreateTool(OperationDescriptor operation)
    {
        using var schema = _schemaGenerator.GenerateInputSchema(operation);
        return new Tool
        {
            Name = operation.Name,
            Description = operation.Description,
            InputSchema = schema.RootElement.Clone(),
            Annotations = new ToolAnnotations
            {
                ReadOnlyHint = operation.SafetyLevel == AgentSafetyLevel.Safe,
                DestructiveHint = operation.SafetyLevel == AgentSafetyLevel.Dangerous
            }
        };
    }

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }]
    };
}
