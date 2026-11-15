using System.Text.Json.Nodes;
using DotNetAgentSurface.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotNetAgentSurface.Mcp;

/// <summary>Hosts catalog-backed MCP operations over the official MCP stdio transport.</summary>
public sealed class McpOperationServer
{
    private readonly McpOperationAdapter _adapter;
    private readonly string? _serverInstructions;

    /// <param name="adapter">Adapts a catalog to MCP tool descriptors and invocations.</param>
    public McpOperationServer(McpOperationAdapter adapter)
        : this(adapter, null)
    {
    }

    /// <param name="adapter">Adapts a catalog to MCP tool descriptors and invocations.</param>
    /// <param name="serverInstructions">
    /// Free-form guidance surfaced to MCP clients via <see cref="McpServerOptions.ServerInstructions"/>
    /// during initialization. Clients that honor it typically fold it into the model's system prompt (per
    /// the MCP spec, this is advisory: a client may add it to context, but is not required to). Use it to
    /// describe how the tools on this server are meant to be used — for example, when to prefer one
    /// operation over another, or ordering/workflow expectations — rather than repeating text already
    /// present in individual tool descriptions.
    /// </param>
    public McpOperationServer(McpOperationAdapter adapter, string? serverInstructions)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _serverInstructions = serverInstructions;
    }

    /// <summary>
    /// Creates SDK server options that carry the catalog's operations in <see cref="McpServerOptions.ToolCollection"/>. The
    /// list handler is left to the SDK; only an alias-resolving <see cref="McpServerHandlers.CallToolHandler"/> fallback is
    /// set, because the SDK matches tool names exactly.
    /// </summary>
    public McpServerOptions CreateOptions()
    {
        var options = new McpServerOptions
        {
            ServerInstructions = _serverInstructions,
            Handlers = new McpServerHandlers
            {
                CallToolHandler = (context, cancellationToken) => _adapter.InvokeAsync(
                    context.Params?.Name ?? string.Empty,
                    context.Params?.Arguments is null
                        ? null
                        : new Dictionary<string, System.Text.Json.JsonElement>(context.Params.Arguments),
                    cancellationToken,
                    GetConfirmation(context.Params?.Meta),
                    GetInvocationContext(context))
            }
        };

        options.ToolCollection = [.. _adapter.CreateServerTools()];
        return options;
    }

    public Task RunStdioAsync(CancellationToken cancellationToken = default)
    {
        var options = CreateOptions();
        var transport = new StdioServerTransport(options);
        return McpServer.Create(transport, options).RunAsync(cancellationToken);
    }
    /// <summary>The MCP request metadata key carrying host-level operation approval.</summary>
    public const string ConfirmationMetadataKey = "io.dotnetagentsurface/confirmation";

    internal static OperationConfirmation GetConfirmation(JsonObject? metadata)
    {
        if (metadata?[ConfirmationMetadataKey] is not JsonObject confirmation)
        {
            return OperationConfirmation.None;
        }

        var isConfirmed = confirmation["confirmed"]?.GetValue<bool>() ?? false;
        var isDangerousConfirmed = confirmation["dangerousConfirmed"]?.GetValue<bool>() ?? false;
        return new OperationConfirmation(isConfirmed || isDangerousConfirmed, isDangerousConfirmed);
    }

    private static OperationInvocationContext? GetInvocationContext(RequestContext<CallToolRequestParams> context)
    {
        var principal = context.JsonRpcRequest.Context?.User;
        return principal is null ? null : new OperationInvocationContext(principal);
    }
}
