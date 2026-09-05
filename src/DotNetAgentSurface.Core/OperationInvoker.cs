using System.Reflection;
using System.Text.Json;

namespace DotNetAgentSurface.Core;

/// <summary>
/// Invokes <see cref="OperationDescriptor"/>s discovered/registered via <see cref="OperationCatalog"/>/<see
/// cref="OperationCatalogBuilder"/>, resolving service targets, binding JSON inputs, and evaluating the
/// constructor-supplied policies before the underlying method runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Confirmation is opt-in, not automatic.</b> <see cref="AgentOperationAttribute.SafetyLevel"/>/<see
/// cref="OperationDescriptor.SafetyLevel"/> (<see cref="AgentSafetyLevel.Confirm"/>/<see
/// cref="AgentSafetyLevel.Dangerous"/>) is metadata only — it describes intent but enforces nothing by itself.
/// An operation is only actually gated when a policy that implements <see cref="IConfirmationEnforcingPolicy"/>
/// (for example, <see cref="DangerousOperationConfirmationPolicy"/>) is supplied either to this class's
/// constructor policies or on the individual <see cref="OperationDescriptor.InvocationPolicies"/>.
/// </para>
/// <para>
/// To make this hard to miss, <see cref="InvokeAsync"/> throws <see cref="ConfirmationPolicyMissingException"/>
/// before running any operation whose <see cref="OperationDescriptor.SafetyLevel"/> is above <see
/// cref="AgentSafetyLevel.Safe"/> if no confirmation-enforcing policy is present. Attach a <see
/// cref="DangerousOperationConfirmationPolicy"/> (or a custom policy implementing <see
/// cref="IConfirmationEnforcingPolicy"/>) to avoid this, and pass an <see cref="OperationConfirmation"/> at
/// invocation time to actually satisfy it.
/// </para>
/// </remarks>
public sealed class OperationInvoker
{
    private readonly IServiceProvider _services;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly IReadOnlyList<IOperationInvocationPolicy> _policies;

    /// <param name="services">Resolves unbound instance-method targets for discovered/registered operations.</param>
    /// <param name="jsonOptions">Options used to bind JSON inputs; defaults to <see cref="JsonSerializerDefaults.Web"/>.</param>
    /// <param name="policies">
    /// Policies evaluated before every invocation, in order, ahead of any operation-level <see
    /// cref="OperationDescriptor.InvocationPolicies"/>. To enforce <see cref="AgentSafetyLevel.Confirm"/>/<see
    /// cref="AgentSafetyLevel.Dangerous"/> operations — rather than merely describing them via metadata — include
    /// a policy implementing <see cref="IConfirmationEnforcingPolicy"/>, such as <see
    /// cref="DangerousOperationConfirmationPolicy"/>. See the type-level remarks for details.
    /// </param>
    public OperationInvoker(
        IServiceProvider services,
        JsonSerializerOptions? jsonOptions = null,
        IEnumerable<IOperationInvocationPolicy>? policies = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _jsonOptions = jsonOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
        _policies = (policies ?? []).ToArray();
    }

    public async ValueTask<OperationInvocationResult> InvokeAsync(
        OperationDescriptor operation,
        IReadOnlyDictionary<string, JsonElement>? inputs = null,
        CancellationToken cancellationToken = default,
        OperationConfirmation? confirmation = null,
        OperationInvocationContext? invocationContext = null)
    {
        Guard.ThrowIfNull(operation);
        EnsureConfirmationIsEnforceable(operation);

        try
        {
            foreach (var policy in _policies.Concat(operation.InvocationPolicies))
            {
                var decision = await policy.EvaluateAsync(operation, inputs, confirmation, cancellationToken, invocationContext).ConfigureAwait(false);
                if (!decision.IsAllowed)
                {
                    return OperationInvocationResult.Failure(decision.Error ?? $"Operation '{operation.Name}' was denied by policy.");
                }
            }

            var arguments = Bind(operation, inputs, cancellationToken);
            var target = ResolveTarget(operation);
            var rawResult = operation.Method.Invoke(target, arguments);
            var result = await UnwrapResultAsync(rawResult).ConfigureAwait(false);
            return OperationInvocationResult.Success(result);
        }
        catch (OperationCanceledException)
        {
            return OperationInvocationResult.Cancelled();
        }
        catch (TargetInvocationException exception) when (exception.InnerException is OperationCanceledException)
        {
            return OperationInvocationResult.Cancelled();
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            return OperationInvocationResult.Failure(exception.InnerException.Message);
        }
        catch (OperationBindingException exception)
        {
            return OperationInvocationResult.Failure(exception.Message);
        }
        catch (Exception exception)
        {
            return OperationInvocationResult.Failure(exception.Message);
        }
    }

    /// <summary>
    /// Guards against the trap where <see cref="OperationDescriptor.SafetyLevel"/> looks gated (<see
    /// cref="AgentSafetyLevel.Confirm"/> or <see cref="AgentSafetyLevel.Dangerous"/>) but no supplied policy
    /// actually enforces confirmation, which would otherwise let the invocation silently proceed unconfirmed.
    /// See the <see cref="OperationInvoker(IServiceProvider, JsonSerializerOptions?, IEnumerable{IOperationInvocationPolicy}?)"/>
    /// constructor remarks for how to satisfy this check.
    /// </summary>
    private void EnsureConfirmationIsEnforceable(OperationDescriptor operation)
    {
        if (operation.SafetyLevel == AgentSafetyLevel.Safe)
        {
            return;
        }

        if (_policies.Concat(operation.InvocationPolicies).Any(static policy => policy is IConfirmationEnforcingPolicy))
        {
            return;
        }

        throw new ConfirmationPolicyMissingException(
            $"Operation '{operation.Name}' has SafetyLevel '{operation.SafetyLevel}', which is metadata only. " +
            $"No supplied policy implements {nameof(IConfirmationEnforcingPolicy)} to actually enforce confirmation, " +
            $"so invoking it would silently bypass the safety gate. Pass a {nameof(DangerousOperationConfirmationPolicy)} " +
            "(or another policy implementing that interface) to the OperationInvoker constructor or the operation's " +
            $"{nameof(OperationDescriptor.InvocationPolicies)}.");
    }

    private object? ResolveTarget(OperationDescriptor operation)
    {
        if (operation.BoundTarget is not null)
        {
            return operation.BoundTarget;
        }

        if (operation.Method.IsStatic)
        {
            return null;
        }

        var target = _services.GetService(operation.ServiceType!);
        return target ?? throw new OperationBindingException($"No service is registered for operation '{operation.Name}' ({operation.ServiceType!.FullName}).");
    }

    private object?[] Bind(OperationDescriptor operation, IReadOnlyDictionary<string, JsonElement>? inputs, CancellationToken cancellationToken)
    {
        var inputValues = inputs ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var arguments = new object?[operation.Parameters.Count];

        for (var index = 0; index < operation.Parameters.Count; index++)
        {
            var parameter = operation.Parameters[index];
            if (parameter.IsCancellationToken)
            {
                arguments[index] = cancellationToken;
                continue;
            }

            if (TryGetValue(inputValues, parameter.Name, out var value))
            {
                try
                {
                    arguments[index] = value.Deserialize(parameter.ParameterType, _jsonOptions);
                }
                catch (JsonException exception)
                {
                    throw new OperationBindingException($"Input '{parameter.Name}' is invalid for operation '{operation.Name}': {exception.Message}");
                }

                continue;
            }

            if (parameter.IsOptional)
            {
                arguments[index] = parameter.DefaultValue is DBNull ? Type.Missing : parameter.DefaultValue;
                continue;
            }

            if (IsNullable(parameter.ParameterType))
            {
                arguments[index] = null;
                continue;
            }

            throw new OperationBindingException($"Required input '{parameter.Name}' was not provided for operation '{operation.Name}'.");
        }

        return arguments;
    }

    private static bool TryGetValue(IReadOnlyDictionary<string, JsonElement> inputs, string name, out JsonElement value)
    {
        if (inputs.TryGetValue(name, out value))
        {
            return true;
        }

        foreach (var pair in inputs)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool IsNullable(Type type) => !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;

    private static async ValueTask<object?> UnwrapResultAsync(object? rawResult)
    {
        switch (rawResult)
        {
            case null:
                return null;
            case Task task:
                await task.ConfigureAwait(false);
                return GetTaskResult(task);
            case ValueTask valueTask:
                await valueTask.ConfigureAwait(false);
                return null;
            default:
                return await UnwrapGenericValueTaskAsync(rawResult).ConfigureAwait(false) ?? rawResult;
        }
    }

    private static object? GetTaskResult(Task task) => task.GetType().IsGenericType ? task.GetType().GetProperty("Result")!.GetValue(task) : null;

    private static async ValueTask<object?> UnwrapGenericValueTaskAsync(object rawResult)
    {
        var type = rawResult.GetType();
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(ValueTask<>))
        {
            return null;
        }

        var task = (Task)type.GetMethod("AsTask")!.Invoke(rawResult, null)!;
        await task.ConfigureAwait(false);
        return GetTaskResult(task);
    }
}

/// <summary>
/// Explicitly marks an idempotent operation's already-applied result. Operations return this marker instead of
/// relying on error-message text; the CLI adapter then emits its concise acknowledgement as a successful no-op.
/// </summary>
public sealed record OperationNoOp(string Message)
{
    public static OperationNoOp AlreadyApplied(string message = "Already applied.") => new(message);
}

public sealed record OperationInvocationResult(bool Succeeded, bool IsCancelled, object? Value, string? Error)
{
    public static OperationInvocationResult Success(object? value) => new(true, false, value, null);

    public static OperationInvocationResult Failure(string error) => new(false, false, null, error);

    public static OperationInvocationResult Cancelled() => new(false, true, null, "Operation was cancelled.");
}

public sealed class OperationBindingException : Exception
{
    public OperationBindingException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Thrown by <see cref="OperationInvoker"/> when an operation's <see cref="OperationDescriptor.SafetyLevel"/> is
/// <see cref="AgentSafetyLevel.Confirm"/> or <see cref="AgentSafetyLevel.Dangerous"/> but none of the supplied
/// policies (constructor-level or operation-level) implement <see cref="IConfirmationEnforcingPolicy"/>. This
/// fails fast instead of allowing an apparently safety-gated operation to run unconfirmed.
/// </summary>
public sealed class ConfirmationPolicyMissingException : Exception
{
    public ConfirmationPolicyMissingException(string message)
        : base(message)
    {
    }
}
