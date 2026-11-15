using System.Security.Claims;
using DotNetAgentSurface.Core;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotNetAgentSurface.Mcp;

/// <summary>
/// The request-scoped provider handed to the <see cref="OperationInvoker"/> for an MCP call. It supplies the MCP
/// ambient services and defers everything else to the request's own provider.
/// </summary>
internal sealed class McpRequestServices(RequestContext<CallToolRequestParams> request, IServiceProvider? inner) : IServiceProvider
{
    private readonly List<Task> _pending = [];

    /// <summary>Waits for progress notifications still in flight so they reach the client before the result does.</summary>
    public Task FlushProgressAsync()
    {
        lock (_pending)
        {
            return Task.WhenAll(_pending.ToArray());
        }
    }

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(RequestContext<CallToolRequestParams>))
        {
            return request;
        }

        if (serviceType == typeof(McpServer))
        {
            return request.Server;
        }

        if (serviceType == typeof(IProgress<ProgressNotificationValue>) || serviceType == typeof(IOperationProgress))
        {
            return request.Params?.ProgressToken is { } token
                ? new McpOperationProgress(request.Server, token, _pending)
                : serviceType == typeof(IOperationProgress) ? OperationProgress.None : IgnoredProgress.Instance;
        }

        if (serviceType == typeof(ClaimsPrincipal))
        {
            return request.User;
        }

        return inner?.GetService(serviceType);
    }
}

internal sealed class McpOperationProgress(McpServer server, ProgressToken token, List<Task> pending) : IOperationProgress, IProgress<ProgressNotificationValue>
{
    public void Report(float progress, float? total = null, string? message = null)
        => Report(new ProgressNotificationValue { Progress = progress, Total = total, Message = message });

    public void Report(ProgressNotificationValue value)
    {
        var send = SendAsync(value);
        lock (pending)
        {
            pending.Add(send);
        }
    }

    private async Task SendAsync(ProgressNotificationValue value)
    {
        try
        {
            await server.NotifyProgressAsync(token, value).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Progress is best-effort; a closed transport must not fail the operation.
        }
    }
}

internal sealed class IgnoredProgress : IProgress<ProgressNotificationValue>
{
    public static IgnoredProgress Instance { get; } = new();

    public void Report(ProgressNotificationValue value)
    {
    }
}
