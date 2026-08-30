using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotNetAgentSurface.Mcp;

/// <summary>Hosts catalog-backed MCP operations over the official MCP stdio transport.</summary>
public sealed class McpOperationServer
{
    private readonly McpOperationAdapter _adapter;

    public McpOperationServer(McpOperationAdapter adapter)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    public McpServerOptions CreateOptions() => new()
    {
        Handlers = new McpServerHandlers
        {
            ListToolsHandler = (context, cancellationToken) => new ValueTask<ListToolsResult>(new ListToolsResult
            {
                Tools = [.. _adapter.GetTools()]
            }),
            CallToolHandler = (context, cancellationToken) => _adapter.InvokeAsync(
                context.Params.Name,
                context.Params.Arguments is null
                    ? null
                    : new Dictionary<string, System.Text.Json.JsonElement>(context.Params.Arguments),
                cancellationToken)
        }
    };

    public Task RunStdioAsync(CancellationToken cancellationToken = default)
    {
        var options = CreateOptions();
        var transport = new StdioServerTransport(options);
        return McpServer.Create(transport, options).RunAsync(cancellationToken);
    }
}
