using DotNetAgentSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace DotNetAgentSurface.Mcp;

internal static class McpOperationAdapterFactory
{
    public static McpOperationAdapter Create(OperationCatalog catalog, IServiceProvider? services, McpOperationOptions options)
    {
        var jsonOptions = options.JsonSerializerOptions ?? McpJsonUtilities.DefaultOptions;
        var logger = options.Logger
            ?? services?.GetService<ILoggerFactory>()?.CreateLogger("DotNetAgentSurface.Mcp");

        var policies = (services?.GetServices<IOperationInvocationPolicy>() ?? []).Concat(options.Policies).ToArray();
        var invokerOptions = new OperationInvokerOptions
        {
            Logger = logger,
            ExceptionMapper = options.ExceptionMapper
                ?? services?.GetService<IOperationExceptionMapper>()
                ?? new DefaultOperationExceptionMapper(logger),
            ConfirmationMode = options.ConfirmationMode,
            TargetFactory = static (provider, type) => ActivatorUtilities.CreateInstance(provider, type),
        };

        foreach (var filter in (services?.GetServices<IOperationInvocationFilter>() ?? []).Concat(options.Filters))
        {
            invokerOptions.Filters.Add(filter);
        }

        var invoker = new OperationInvoker(services ?? EmptyServiceProvider.Instance, jsonOptions, policies, invokerOptions);
        var effective = options.JsonSerializerOptions is null
            ? CloneWith(options, jsonOptions)
            : options;
        return new McpOperationAdapter(catalog, invoker, options: effective);
    }

    private static McpOperationOptions CloneWith(McpOperationOptions source, System.Text.Json.JsonSerializerOptions jsonOptions)
    {
        var clone = new McpOperationOptions
        {
            JsonSerializerOptions = jsonOptions,
            ResultRenderer = source.ResultRenderer,
            UseStructuredContent = source.UseStructuredContent,
            UseElicitationConfirmation = source.UseElicitationConfirmation,
            ConfirmationMode = source.ConfirmationMode,
            ExceptionMapper = source.ExceptionMapper,
            Logger = source.Logger,
        };
        foreach (var pair in source.OperationRenderers)
        {
            clone.OperationRenderers[pair.Key] = pair.Value;
        }

        return clone;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }
}
