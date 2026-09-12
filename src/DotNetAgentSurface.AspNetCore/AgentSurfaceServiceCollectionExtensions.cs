using DotNetAgentSurface.CommandLine;
using DotNetAgentSurface.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetAgentSurface.AspNetCore;

/// <summary>Registers Agent Surface services for ASP.NET Core API Explorer endpoints.</summary>
public static class AgentSurfaceServiceCollectionExtensions
{
    /// <summary>
    /// Registers Agent Surface command infrastructure for API Explorer endpoints.
    /// </summary>
    /// <remarks>
    /// The catalog is constructed only when it is first resolved. Map all application routes before resolving it so
    /// the endpoint data sources are complete. Minimal API descriptions are normally populated only once ASP.NET Core
    /// starts; the command runner additionally discovers unmapped descriptions as parameterless route operations
    /// without starting a listener. Use <paramref name="configure"/> to add non-HTTP operations to the same catalog.
    /// If an added operation needs a service resolved from the application's <see cref="IServiceProvider"/> (for
    /// example an <c>IHubContext&lt;THub&gt;</c> or a scoped service), use the
    /// <see cref="AddAgentSurfaceFromApiExplorer(IServiceCollection, Action{OperationCatalogBuilder, IServiceProvider})"/>
    /// overload instead.
    /// </remarks>
    public static IServiceCollection AddAgentSurfaceFromApiExplorer(
        this IServiceCollection services,
        Action<OperationCatalogBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddAgentSurfaceFromApiExplorerCore(new AgentSurfaceApiExplorerConfiguration(configure));
    }

    /// <summary>
    /// Registers Agent Surface command infrastructure for API Explorer endpoints, giving
    /// <paramref name="configure"/> access to the application's <see cref="IServiceProvider"/> so it can resolve
    /// services needed by non-HTTP operations added to the catalog.
    /// </summary>
    /// <remarks>
    /// The catalog is constructed only when it is first resolved. Map all application routes before resolving it so
    /// the endpoint data sources are complete. Minimal API descriptions are normally populated only once ASP.NET Core
    /// starts; the command runner additionally discovers unmapped descriptions as parameterless route operations
    /// without starting a listener. The <see cref="IServiceProvider"/> passed to <paramref name="configure"/> is the
    /// same root provider used to build the catalog (<see cref="RunAgentSurfaceCliAsync"/> passes <c>app.Services</c>);
    /// resolve scoped services through <see cref="ServiceProviderServiceExtensions.CreateScope"/> rather than
    /// resolving them directly from it.
    /// </remarks>
    public static IServiceCollection AddAgentSurfaceFromApiExplorer(
        this IServiceCollection services,
        Action<OperationCatalogBuilder, IServiceProvider> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        return services.AddAgentSurfaceFromApiExplorerCore(new AgentSurfaceApiExplorerConfiguration(configure));
    }

    private static IServiceCollection AddAgentSurfaceFromApiExplorerCore(
        this IServiceCollection services,
        AgentSurfaceApiExplorerConfiguration configuration)
    {
        services.AddSingleton(configuration);
        services.AddSingleton(sp =>
        {
            var catalogBuilder = new OperationCatalogBuilder();
            configuration.Configure?.Invoke(catalogBuilder, sp);
            return catalogBuilder
                .AddFromApiExplorer(
                    sp.GetRequiredService<IApiDescriptionGroupCollectionProvider>(),
                    sp.GetServices<EndpointDataSource>(),
                    sp)
                .Build();
        });
        services.AddSingleton<OperationInvoker>();
        return services;
    }

    /// <summary>
    /// Executes the application's Agent Surface command line without starting the web server.
    /// </summary>
    /// <remarks>
    /// Map all routes before calling this method. It constructs an execution catalog from the application's current
    /// route data sources, including parameterless minimal API routes that API Explorer has not yet described.
    /// </remarks>
    public static async Task<int> RunAgentSurfaceCliAsync(
        this WebApplication app,
        string[] args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(args);

        using var invocation = AgentSurfaceCliInvocation.Enter();
        var configuration = app.Services.GetRequiredService<AgentSurfaceApiExplorerConfiguration>();
        var catalogBuilder = new OperationCatalogBuilder();
        configuration.Configure?.Invoke(catalogBuilder, app.Services);
        var catalog = catalogBuilder
            .AddFromApiExplorer(
                app.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>(),
                ((IEndpointRouteBuilder)app).DataSources,
                app.Services)
            .Build();
        var adapter = new OperationCommandLineAdapter(
            catalog,
            app.Services.GetRequiredService<OperationInvoker>());
        var result = await adapter.ExecuteAsync(args, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(result.Output))
        {
            await Console.Out.WriteAsync(result.Output).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(result.Error))
        {
            await Console.Error.WriteAsync(result.Error).ConfigureAwait(false);
        }

        return result.ExitCode;
    }
}

internal sealed class AgentSurfaceApiExplorerConfiguration
{
    public AgentSurfaceApiExplorerConfiguration(Action<OperationCatalogBuilder>? configure)
        : this(configure is null ? null : (builder, _) => configure(builder))
    {
    }

    public AgentSurfaceApiExplorerConfiguration(Action<OperationCatalogBuilder, IServiceProvider>? configure)
    {
        Configure = configure;
    }

    public Action<OperationCatalogBuilder, IServiceProvider>? Configure { get; }
}

/// <summary>Exposes whether execution is currently occurring through <see cref="AgentSurfaceServiceCollectionExtensions.RunAgentSurfaceCliAsync"/>.</summary>
public static class AgentSurfaceCliInvocation
{
    private static readonly AsyncLocal<int> Depth = new();

    /// <summary>Gets whether the current asynchronous flow is executing an Agent Surface CLI command.</summary>
    public static bool IsInProgress => Depth.Value > 0;

    internal static IDisposable Enter()
    {
        Depth.Value++;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose()
        {
            if (Depth.Value > 0)
            {
                Depth.Value--;
            }
        }
    }
}
