using DotNetAgentSurface.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotNetAgentSurface.Mcp;

/// <summary>
/// An SDK <see cref="McpServerTool"/> backed by one catalog operation. It is added to the server's tool collection, so the
/// SDK's own list/call handlers, filters and transports keep working; it never replaces them.
/// </summary>
public sealed class McpOperationTool : McpServerTool
{
    private readonly McpOperationAdapter _adapter;
    private readonly OperationDescriptor _operation;

    internal McpOperationTool(McpOperationAdapter adapter, OperationDescriptor operation)
    {
        _adapter = adapter;
        _operation = operation;
        ProtocolTool = adapter.CreateTool(operation);
        Metadata = [operation, operation.Method];
    }

    public OperationDescriptor Operation => _operation;

    public override Tool ProtocolTool { get; }

    public override IReadOnlyList<object> Metadata { get; }

    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return _adapter.InvokeToolAsync(_operation, request, cancellationToken);
    }
}
