using DotNetAgentSurface.Core;
using DotNetAgentSurface.Mcp;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace DotNetAgentSurface.Hosting;

/// <summary>Connects the application's <see cref="OperationCatalog"/> to the MCP SDK builder.</summary>
public static class AgentSurfaceMcpServerBuilderExtensions
{
    /// <summary>
    /// Serves the <see cref="OperationCatalog"/> registered by <see cref="AgentSurfaceApplication"/> as MCP tools, honoring
    /// <see cref="AgentSurfaces"/> (CLI-only operations are not listed) and confirmation metadata. Do not combine it with
    /// another registration that supplies the tool list and call handlers.
    /// </summary>
    public static IMcpServerBuilder WithAgentSurfaceCatalog(this IMcpServerBuilder builder)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        builder.Services.AddOptions<McpServerOptions>().Configure<McpOperationAdapter>((options, adapter) =>
        {
            var handlers = new McpOperationServer(adapter).CreateOptions().Handlers;
            options.Handlers.ListToolsHandler = handlers.ListToolsHandler;
            options.Handlers.CallToolHandler = handlers.CallToolHandler;
        });
        return builder;
    }
}
