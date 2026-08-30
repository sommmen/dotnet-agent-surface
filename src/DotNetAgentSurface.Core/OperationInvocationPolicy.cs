using System.Text.Json;

namespace DotNetAgentSurface.Core;

/// <summary>Determines whether an operation may be invoked for a particular request.</summary>
public interface IOperationInvocationPolicy
{
    ValueTask<OperationPolicyResult> EvaluateAsync(
        OperationDescriptor operation,
        IReadOnlyDictionary<string, JsonElement>? inputs,
        CancellationToken cancellationToken = default);
}

public sealed record OperationPolicyResult(bool IsAllowed, string? Error)
{
    public static OperationPolicyResult Allow() => new(true, null);

    public static OperationPolicyResult Deny(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return new(false, error);
    }
}

/// <summary>Requires a registered policy to explicitly approve dangerous operations.</summary>
public sealed class DangerousOperationConfirmationPolicy : IOperationInvocationPolicy
{
    private readonly Func<OperationDescriptor, IReadOnlyDictionary<string, JsonElement>?, CancellationToken, ValueTask<bool>> _isConfirmed;

    public DangerousOperationConfirmationPolicy()
        : this(static (_, _, _) => ValueTask.FromResult(false))
    {
    }

    public DangerousOperationConfirmationPolicy(
        Func<OperationDescriptor, IReadOnlyDictionary<string, JsonElement>?, CancellationToken, ValueTask<bool>> isConfirmed)
    {
        _isConfirmed = isConfirmed ?? throw new ArgumentNullException(nameof(isConfirmed));
    }

    public async ValueTask<OperationPolicyResult> EvaluateAsync(
        OperationDescriptor operation,
        IReadOnlyDictionary<string, JsonElement>? inputs,
        CancellationToken cancellationToken = default)
    {
        if (operation.SafetyLevel != AgentSafetyLevel.Dangerous)
        {
            return OperationPolicyResult.Allow();
        }

        return await _isConfirmed(operation, inputs, cancellationToken).ConfigureAwait(false)
            ? OperationPolicyResult.Allow()
            : OperationPolicyResult.Deny($"Operation '{operation.Name}' requires explicit confirmation.");
    }
}
