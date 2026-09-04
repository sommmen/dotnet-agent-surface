using System.Text.Json;

namespace DotNetAgentSurface.Core;

/// <summary>Determines whether an operation may be invoked for a particular request.</summary>
public interface IOperationInvocationPolicy
{
    ValueTask<OperationPolicyResult> EvaluateAsync(
        OperationDescriptor operation,
        IReadOnlyDictionary<string, JsonElement>? inputs,
        OperationConfirmation? confirmation = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Explicit, host-supplied approval for safety-gated operation invocations.</summary>
public sealed record OperationConfirmation(bool IsConfirmed, bool IsDangerousConfirmed)
{
    public static OperationConfirmation None { get; } = new(false, false);

    public static OperationConfirmation Confirmed { get; } = new(true, false);

    public static OperationConfirmation DangerousConfirmed { get; } = new(true, true);
}

public sealed record OperationPolicyResult(bool IsAllowed, string? Error)
{
    public static OperationPolicyResult Allow() => new(true, null);

    public static OperationPolicyResult Deny(string error)
    {
        Guard.ThrowIfNullOrWhiteSpace(error);
        return new(false, error);
    }
}

/// <summary>Requires explicit host approval for <see cref="AgentSafetyLevel.Confirm"/> and dangerous operations.</summary>
public sealed class DangerousOperationConfirmationPolicy : IOperationInvocationPolicy
{
    private readonly Func<OperationDescriptor, IReadOnlyDictionary<string, JsonElement>?, CancellationToken, ValueTask<bool>>? _isConfirmed;

    /// <summary>Creates a fail-closed policy that accepts only explicit host confirmation metadata.</summary>
    public DangerousOperationConfirmationPolicy()
    {
    }

    /// <summary>
    /// Creates a policy with a host approval callback. The callback is retained for compatibility; hosts should prefer
    /// passing <see cref="OperationConfirmation"/> to the invocation because it makes non-interactive approval explicit.
    /// </summary>
    public DangerousOperationConfirmationPolicy(
        Func<OperationDescriptor, IReadOnlyDictionary<string, JsonElement>?, CancellationToken, ValueTask<bool>> isConfirmed)
    {
        _isConfirmed = isConfirmed ?? throw new ArgumentNullException(nameof(isConfirmed));
    }

    public async ValueTask<OperationPolicyResult> EvaluateAsync(
        OperationDescriptor operation,
        IReadOnlyDictionary<string, JsonElement>? inputs,
        OperationConfirmation? confirmation = null,
        CancellationToken cancellationToken = default)
    {
        Guard.ThrowIfNull(operation);

        var isAllowed = operation.SafetyLevel switch
        {
            AgentSafetyLevel.Safe => true,
            AgentSafetyLevel.Confirm => confirmation?.IsConfirmed == true,
            AgentSafetyLevel.Dangerous => confirmation?.IsDangerousConfirmed == true,
            _ => false,
        };

        if (isAllowed)
        {
            return OperationPolicyResult.Allow();
        }

        if (_isConfirmed is not null && await _isConfirmed(operation, inputs, cancellationToken).ConfigureAwait(false))
        {
            return OperationPolicyResult.Allow();
        }

        return OperationPolicyResult.Deny($"Operation '{operation.Name}' requires explicit confirmation.");
    }
}
