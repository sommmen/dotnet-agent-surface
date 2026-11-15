using System.Security.Claims;

namespace DotNetAgentSurface.Core;

/// <summary>
/// Surface-neutral information about the invocation an operation is running in. Operations declare a parameter of
/// this type to receive it; the parameter is injected and never appears in the input schema.
/// </summary>
public interface IOperationContext
{
    /// <summary>Gets the surface (MCP, CLI, ...) that is invoking the operation.</summary>
    AgentSurface Surface { get; }

    /// <summary>Gets the name of the operation being invoked.</summary>
    string OperationName { get; }

    /// <summary>Gets the correlation identifier for this invocation.</summary>
    string RequestId { get; }

    /// <summary>Gets the authenticated caller, when the host supplied one.</summary>
    ClaimsPrincipal? Caller { get; }

    /// <summary>
    /// Gets the output format the caller asked for, for example <c>json</c> or <c>toon</c> from the CLI's <c>--output</c>
    /// flag, or <see langword="null"/> when none was requested. Operations that return pre-rendered text use it to pick a
    /// representation.
    /// </summary>
    string? OutputFormat { get; }
}

/// <summary>
/// Surface-neutral progress reporting. Under MCP this sends progress notifications when the client asked for them;
/// other surfaces provide their own sink. When a host supplies none, <see cref="OperationProgress.None"/> is injected.
/// </summary>
public interface IOperationProgress
{
    /// <summary>Reports progress.</summary>
    /// <param name="progress">The progress made so far. It should increase with every call.</param>
    /// <param name="total">The total amount of work, when known.</param>
    /// <param name="message">An optional human-readable status message.</param>
    void Report(float progress, float? total = null, string? message = null);
}

/// <summary>Provides a no-op <see cref="IOperationProgress"/>.</summary>
public static class OperationProgress
{
    /// <summary>Gets a progress sink that discards every report.</summary>
    public static IOperationProgress None { get; } = new NoOpProgress();

    private sealed class NoOpProgress : IOperationProgress
    {
        public void Report(float progress, float? total = null, string? message = null)
        {
        }
    }
}

internal sealed class OperationContext(OperationDescriptor operation, OperationInvocationContext context) : IOperationContext
{
    public AgentSurface Surface => context.Surface;

    public string OperationName => operation.Name;

    public string RequestId => context.RequestId ?? string.Empty;

    public ClaimsPrincipal? Caller => context.Principal;

    public string? OutputFormat => context.OutputFormat;
}

internal static class AmbientParameters
{
    // MCP SDK types are matched by name so Core does not reference the SDK.
    public static bool IsAmbient(Type type)
    {
        if (type == typeof(CancellationToken)
            || type == typeof(IServiceProvider)
            || type == typeof(ClaimsPrincipal)
            || type == typeof(IOperationContext)
            || type == typeof(IOperationProgress))
        {
            return true;
        }

        if (type.FullName == "ModelContextProtocol.Server.McpServer")
        {
            return true;
        }

        if (!type.IsGenericType)
        {
            return false;
        }

        var definition = type.GetGenericTypeDefinition().FullName;
        var argument = type.GetGenericArguments()[0].FullName;
        return (definition == "ModelContextProtocol.Server.RequestContext`1")
            || (definition == "System.IProgress`1" && argument == "ModelContextProtocol.ProgressNotificationValue");
    }
}
