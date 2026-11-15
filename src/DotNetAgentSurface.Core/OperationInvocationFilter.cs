using System.Text.Json;

namespace DotNetAgentSurface.Core;

/// <summary>
/// An ordered step wrapped around every <see cref="OperationInvoker"/> invocation, shared by all surfaces. Use it for
/// telemetry, redaction and audit logging. Filters run in registration order before the invocation and in reverse
/// order afterwards.
/// </summary>
/// <remarks>
/// Derive from <see cref="OperationInvocationFilter"/> to override only the callbacks you need.
/// </remarks>
public interface IOperationInvocationFilter
{
    /// <summary>Runs before policies, binding and the operation itself.</summary>
    ValueTask OnInvokingAsync(OperationInvocationFilterContext context);

    /// <summary>
    /// Runs after the invocation with the final result, which is also the mapped failure when an exception was thrown.
    /// Return <paramref name="result"/> unchanged, or a replacement (for example to redact a value).
    /// </summary>
    ValueTask<OperationInvocationResult> OnInvokedAsync(OperationInvocationFilterContext context, OperationInvocationResult result);

    /// <summary>
    /// Observes an exception thrown anywhere in the pipeline before it is mapped to a result by the
    /// <see cref="IOperationExceptionMapper"/>. It cannot suppress the exception.
    /// </summary>
    ValueTask OnExceptionAsync(OperationInvocationFilterContext context, Exception exception);
}

/// <summary>A no-op <see cref="IOperationInvocationFilter"/> to derive from.</summary>
public abstract class OperationInvocationFilter : IOperationInvocationFilter
{
    public virtual ValueTask OnInvokingAsync(OperationInvocationFilterContext context) => default;

    public virtual ValueTask<OperationInvocationResult> OnInvokedAsync(OperationInvocationFilterContext context, OperationInvocationResult result)
        => new(result);

    public virtual ValueTask OnExceptionAsync(OperationInvocationFilterContext context, Exception exception) => default;
}

/// <summary>What an <see cref="IOperationInvocationFilter"/> can see about one invocation.</summary>
public sealed class OperationInvocationFilterContext
{
    internal OperationInvocationFilterContext(
        OperationDescriptor operation,
        IReadOnlyDictionary<string, JsonElement>? inputs,
        OperationInvocationContext invocationContext,
        CancellationToken cancellationToken)
    {
        Operation = operation;
        Inputs = inputs;
        InvocationContext = invocationContext;
        CancellationToken = cancellationToken;
    }

    public OperationDescriptor Operation { get; }

    /// <summary>The raw JSON inputs as supplied by the caller, before binding.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Inputs { get; }

    public OperationInvocationContext InvocationContext { get; }

    public AgentSurface Surface => InvocationContext.Surface;

    /// <summary>The correlation identifier of this invocation. It is never <see langword="null"/>.</summary>
    public string RequestId => InvocationContext.RequestId ?? string.Empty;

    public CancellationToken CancellationToken { get; }

    /// <summary>Per-invocation scratch space for passing state from <c>OnInvokingAsync</c> to <c>OnInvokedAsync</c>.</summary>
    public IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
}
