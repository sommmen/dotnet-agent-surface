using DotNetAgentSurface.Core;
using DotNetAgentSurface.Mcp;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers an <see cref="OperationCatalog"/> with the ModelContextProtocol SDK server builder.</summary>
public static class McpServerBuilderOperationExtensions
{
    /// <summary>
    /// Builds a catalog with <paramref name="configureCatalog"/> and registers every operation as an SDK
    /// <see cref="McpServerTool"/>. The SDK's list/call handlers are left alone, so this composes with
    /// <c>WithStdioServerTransport()</c>, a replaced <c>ITransport</c>, <c>WithTools&lt;T&gt;()</c>, request/message filters,
    /// prompts, resources, hosted services and <c>McpServerOptions</c> such as <c>ServerInstructions</c>.
    /// </summary>
    /// <remarks>
    /// Operation targets and ambient parameters are resolved from the request's service provider (or the root provider), so scoped
    /// services work there. Policies, filters, the <see cref="IOperationExceptionMapper"/> and <see cref="ILoggerFactory"/> are
    /// resolved once from the first provider that builds a tool and reused for every request, so they must be singleton-safe. An instance operation whose
    /// declaring type is not registered is constructed per call with <see cref="ActivatorUtilities"/> and disposed afterwards,
    /// the way <c>WithTools&lt;T&gt;()</c> does.
    /// </remarks>
    public static IMcpServerBuilder WithOperations(
        this IMcpServerBuilder builder,
        Action<OperationCatalogBuilder> configureCatalog,
        Action<McpOperationOptions>? configure = null)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (configureCatalog is null)
        {
            throw new ArgumentNullException(nameof(configureCatalog));
        }


        var catalogBuilder = new OperationCatalogBuilder();
        configureCatalog(catalogBuilder);
        return builder.WithOperationCatalog(catalogBuilder.Build(), configure);
    }

    /// <summary>Registers every operation of <paramref name="catalog"/> as an SDK <see cref="McpServerTool"/>. See <see cref="WithOperations"/>.</summary>
    public static IMcpServerBuilder WithOperationCatalog(
        this IMcpServerBuilder builder,
        OperationCatalog catalog,
        Action<McpOperationOptions>? configure = null)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (catalog is null)
        {
            throw new ArgumentNullException(nameof(catalog));
        }


        var options = new McpOperationOptions();
        configure?.Invoke(options);
        var mcpCatalog = catalog.ForSurface(AgentSurface.Mcp);
        var holder = new AdapterHolder(mcpCatalog, options);

        foreach (var operation in mcpCatalog.Operations)
        {
            builder.Services.AddSingleton<McpServerTool>(services => new McpOperationTool(holder.Get(services), operation));
        }

        return builder;
    }

    private sealed class AdapterHolder(OperationCatalog catalog, McpOperationOptions options)
    {
        private readonly object _gate = new();
        private McpOperationAdapter? _adapter;

        public McpOperationAdapter Get(IServiceProvider services)
        {
            lock (_gate)
            {
                return _adapter ??= McpOperationAdapterFactory.Create(catalog, services, options);
            }
        }
    }
}
