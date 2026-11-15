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
    /// <see cref="AgentSurfaces"/> (CLI-only operations are not listed) and confirmation metadata. The operations are added as SDK tools, so this
    /// composes with <c>WithTools&lt;T&gt;()</c> and the SDK's own handlers.
    /// </summary>
    public static IMcpServerBuilder WithAgentSurfaceCatalog(this IMcpServerBuilder builder)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        builder.Services.AddOptions<McpServerOptions>().Configure<McpOperationAdapter>((options, adapter) =>
        {
            options.ToolCollection ??= [];
            foreach (var tool in adapter.CreateServerTools())
            {
                options.ToolCollection.Add(tool);
            }
        });
        return builder;
    }
}
