using Microsoft.Extensions.Logging;

namespace DotNetAgentSurface.Core;

/// <summary>
/// Turns an exception thrown while invoking an operation (including before invocation, for example a binding failure
/// or <see cref="ConfirmationPolicyMissingException"/>) into the failed <see cref="OperationInvocationResult"/> that is
/// shown to the caller.
/// </summary>
public interface IOperationExceptionMapper
{
    OperationInvocationResult Map(OperationFailureContext context);
}

/// <summary>The exception being mapped and the invocation it came from.</summary>
/// <param name="Operation">The operation being invoked.</param>
/// <param name="Exception">The exception, with <see cref="System.Reflection.TargetInvocationException"/> already unwrapped.</param>
/// <param name="InvocationContext">The invocation context, including the correlation <see cref="OperationInvocationContext.RequestId"/>.</param>
public sealed record OperationFailureContext(
    OperationDescriptor Operation,
    Exception Exception,
    OperationInvocationContext InvocationContext)
{
    public string RequestId => InvocationContext.RequestId ?? string.Empty;
}

/// <summary>
/// The default mapping:
/// <list type="bullet">
/// <item><description>cancellation becomes a cancelled result;</description></item>
/// <item><description>an <see cref="OperationException"/> or an MCP SDK <c>McpException</c> keeps its message verbatim, newlines included;</description></item>
/// <item><description>binding and confirmation-configuration failures keep their (safe) message;</description></item>
/// <item><description>any other exception is logged with a correlation id and shown as a generic message carrying that id, so
/// connection strings, paths and similar details never reach a remote caller. The CLI is local to the user and keeps the original message.</description></item>
/// </list>
/// </summary>
public sealed class DefaultOperationExceptionMapper : IOperationExceptionMapper
{
    private readonly ILogger? _logger;

    public DefaultOperationExceptionMapper(ILogger? logger = null)
    {
        _logger = logger;
    }

    public OperationInvocationResult Map(OperationFailureContext context)
    {
        Guard.ThrowIfNull(context);
        var exception = context.Exception;

        if (exception is OperationCanceledException)
        {
            return OperationInvocationResult.Cancelled();
        }

        if (exception is OperationException || IsMcpException(exception))
        {
            return OperationInvocationResult.VerbatimFailure(exception.Message);
        }

        if (exception is OperationBindingException or ConfirmationPolicyMissingException)
        {
            return OperationInvocationResult.Failure(exception.Message);
        }

        _logger?.LogError(
            exception,
            "Operation '{Operation}' failed unexpectedly (correlation id {CorrelationId}, surface {Surface}).",
            context.Operation.Name,
            context.RequestId,
            context.InvocationContext.Surface);

        return context.InvocationContext.Surface == AgentSurface.Cli
            ? OperationInvocationResult.Failure(exception.Message)
            : OperationInvocationResult.Failure(
                $"An unexpected error occurred while running '{context.Operation.Name}'. Correlation id: {context.RequestId}.");
    }

    private static bool IsMcpException(Exception exception)
    {
        for (var type = exception.GetType(); type is not null; type = type.BaseType)
        {
            if (type.FullName == "ModelContextProtocol.McpException")
            {
                return true;
            }
        }

        return false;
    }
}
