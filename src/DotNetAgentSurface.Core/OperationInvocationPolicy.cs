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
    public ValueTask<OperationPolicyResult> EvaluateAsync(
        OperationDescriptor operation,
        IReadOnlyDictionary<string, JsonElement>? inputs,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(operation.SafetyLevel == AgentSafetyLevel.Dangerous
            ? OperationPolicyResult.Deny($"Operation '{operation.Name}' requires explicit confirmation.")
            : OperationPolicyResult.Allow());
}
