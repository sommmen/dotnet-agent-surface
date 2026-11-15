using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Logging;

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
/// To make this hard to miss, <see cref="InvokeAsync"/> returns a failed <see cref="OperationInvocationResult"/>
/// (carrying a <see cref="ConfirmationPolicyMissingException"/> message) before running any operation whose <see cref="OperationDescriptor.SafetyLevel"/> is above <see
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
    private readonly IReadOnlyList<IOperationInvocationFilter> _filters;
    private readonly IOperationExceptionMapper _exceptionMapper;
    private readonly ILogger? _logger;
    private readonly OperationConfirmationMode _confirmationMode;
    private readonly Func<IServiceProvider, Type, object?>? _targetFactory;

    /// <param name="services">Resolves unbound instance-method targets for discovered/registered operations.</param>
    /// <param name="jsonOptions">Options used to bind JSON inputs; defaults to <see cref="JsonSerializerDefaults.Web"/>.</param>
    /// <param name="policies">
    /// Policies evaluated before every invocation, in order, ahead of any operation-level <see
    /// cref="OperationDescriptor.InvocationPolicies"/>. To enforce <see cref="AgentSafetyLevel.Confirm"/>/<see
    /// cref="AgentSafetyLevel.Dangerous"/> operations — rather than merely describing them via metadata — include
    /// a policy implementing <see cref="IConfirmationEnforcingPolicy"/>, such as <see
    /// cref="DangerousOperationConfirmationPolicy"/>. See the type-level remarks for details.
    /// </param>
    /// <param name="options">Filters, exception mapping, logging and the confirmation mode.</param>
    public OperationInvoker(
        IServiceProvider services,
        JsonSerializerOptions? jsonOptions = null,
        IEnumerable<IOperationInvocationPolicy>? policies = null,
        OperationInvokerOptions? options = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _jsonOptions = jsonOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
        _policies = (policies ?? []).ToArray();
        _filters = (options?.Filters ?? []).ToArray();
        _logger = options?.Logger;
        _exceptionMapper = options?.ExceptionMapper ?? new DefaultOperationExceptionMapper(_logger);
        _confirmationMode = options?.ConfirmationMode ?? OperationConfirmationMode.PolicyEnforced;
        _targetFactory = options?.TargetFactory;
    }

    public async ValueTask<OperationInvocationResult> InvokeAsync(
        OperationDescriptor operation,
        IReadOnlyDictionary<string, JsonElement>? inputs = null,
        CancellationToken cancellationToken = default,
        OperationConfirmation? confirmation = null,
        OperationInvocationContext? invocationContext = null)
    {
        Guard.ThrowIfNull(operation);

        var context = invocationContext is { RequestId: not null }
            ? invocationContext
            : (invocationContext ?? new OperationInvocationContext()) with { RequestId = Guid.NewGuid().ToString("N") };
        var filterContext = new OperationInvocationFilterContext(operation, inputs, context, cancellationToken);

        using var logScope = _logger?.BeginScope(new Dictionary<string, object?>
        {
            ["OperationName"] = operation.Name,
            ["RequestId"] = context.RequestId,
            ["Surface"] = context.Surface,
        });

        OperationInvocationResult result;
        var entered = 0;
        try
        {
            for (; entered < _filters.Count; entered++)
            {
                await _filters[entered].OnInvokingAsync(filterContext).ConfigureAwait(false);
            }

            result = await InvokeCoreAsync(operation, inputs, cancellationToken, confirmation, context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            result = await MapExceptionAsync(filterContext, exception, entered).ConfigureAwait(false);
        }

        for (var index = entered - 1; index >= 0; index--)
        {
            try
            {
                result = await _filters[index].OnInvokedAsync(filterContext, result).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Filters that already ran their "after" step are not told about a later failing one.
                result = await MapExceptionAsync(filterContext, exception, 0).ConfigureAwait(false);
            }
        }

        return result;
    }

    private async ValueTask<OperationInvocationResult> InvokeCoreAsync(
        OperationDescriptor operation,
        IReadOnlyDictionary<string, JsonElement>? inputs,
        CancellationToken cancellationToken,
        OperationConfirmation? confirmation,
        OperationInvocationContext context)
    {
        EnsureConfirmationIsEnforceable(operation);

        foreach (var policy in _policies.Concat(operation.InvocationPolicies))
        {
            var decision = await policy.EvaluateAsync(operation, inputs, confirmation, cancellationToken, context).ConfigureAwait(false);
            if (!decision.IsAllowed)
            {
                return OperationInvocationResult.Failure(decision.Error ?? $"Operation '{operation.Name}' was denied by policy.");
            }
        }

        var services = context.Services ?? _services;
        var arguments = Bind(operation, inputs, cancellationToken, context, services);
        var (target, ownsTarget) = ResolveTarget(operation, services);
        try
        {
            var rawResult = operation.Method.Invoke(target, arguments);
            var result = await UnwrapResultAsync(rawResult).ConfigureAwait(false);
            return OperationInvocationResult.Success(result);
        }
        finally
        {
            await DisposeTargetAsync(ownsTarget ? target : null, operation).ConfigureAwait(false);
        }
    }

    private async ValueTask<OperationInvocationResult> MapExceptionAsync(OperationInvocationFilterContext filterContext, Exception exception, int enteredFilters)
    {
        var unwrapped = exception;
        while (unwrapped is TargetInvocationException { InnerException: not null } wrapper)
        {
            unwrapped = wrapper.InnerException!;
        }

        foreach (var filter in _filters.Take(enteredFilters))
        {
            try
            {
                await filter.OnExceptionAsync(filterContext, unwrapped).ConfigureAwait(false);
            }
            catch (Exception filterException)
            {
                _logger?.LogWarning(filterException, "An invocation filter failed while observing an exception for operation '{Operation}'.", filterContext.Operation.Name);
            }
        }

        try
        {
            return _exceptionMapper.Map(new OperationFailureContext(filterContext.Operation, unwrapped, filterContext.InvocationContext));
        }
        catch (Exception mapperException)
        {
            _logger?.LogError(mapperException, "The exception mapper failed for operation '{Operation}'.", filterContext.Operation.Name);
            return OperationInvocationResult.Failure($"Operation '{filterContext.Operation.Name}' failed. Correlation id: {filterContext.RequestId}.");
        }
    }

    // A throwing Dispose must not replace the outcome of an operation that already ran. A result must not stay tied to a
    // target created through TargetFactory, because the target is released as soon as the result is awaited.
    private async ValueTask DisposeTargetAsync(object? target, OperationDescriptor operation)
    {
        try
        {
            if (target is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (target is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Disposing the target of operation '{Operation}' failed.", operation.Name);
        }
    }

    /// <summary>
    /// Guards against the trap where <see cref="OperationDescriptor.SafetyLevel"/> looks gated (<see
    /// cref="AgentSafetyLevel.Confirm"/> or <see cref="AgentSafetyLevel.Dangerous"/>) but no supplied policy
    /// actually enforces confirmation, which would otherwise let the invocation silently proceed unconfirmed.
    /// See the <see cref="OperationInvoker(IServiceProvider, JsonSerializerOptions?, IEnumerable{IOperationInvocationPolicy}?, OperationInvokerOptions?)"/>
    /// constructor remarks for how to satisfy this check.
    /// </summary>
    private void EnsureConfirmationIsEnforceable(OperationDescriptor operation)
    {
        if (operation.SafetyLevel == AgentSafetyLevel.Safe || _confirmationMode == OperationConfirmationMode.OperationEnforced)
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

    private (object? Target, bool OwnsTarget) ResolveTarget(OperationDescriptor operation, IServiceProvider services)
    {
        if (operation.BoundTarget is not null)
        {
            return (operation.BoundTarget, false);
        }

        if (operation.Method.IsStatic)
        {
            return (null, false);
        }

        var target = services.GetService(operation.ServiceType!);
        if (target is not null)
        {
            return (target, false);
        }

        var created = _targetFactory?.Invoke(services, operation.ServiceType!);
        return created is null
            ? throw new OperationBindingException($"No service is registered for operation '{operation.Name}' ({operation.ServiceType!.FullName}).")
            : (created, true);
    }

    private object?[] Bind(
        OperationDescriptor operation,
        IReadOnlyDictionary<string, JsonElement>? inputs,
        CancellationToken cancellationToken,
        OperationInvocationContext context,
        IServiceProvider services)
    {
        var inputValues = inputs ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var arguments = new object?[operation.Parameters.Count];

        for (var index = 0; index < operation.Parameters.Count; index++)
        {
            var parameter = operation.Parameters[index];
            if (parameter.IsAmbient)
            {
                arguments[index] = ResolveAmbient(parameter, operation, cancellationToken, context, services);
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

    private static object? ResolveAmbient(
        OperationParameterDescriptor parameter,
        OperationDescriptor operation,
        CancellationToken cancellationToken,
        OperationInvocationContext context,
        IServiceProvider services)
    {
        var type = parameter.ParameterType;
        if (type == typeof(CancellationToken))
        {
            return cancellationToken;
        }

        if (type == typeof(IServiceProvider))
        {
            return services;
        }

        if (type == typeof(ClaimsPrincipal))
        {
            return context.Principal;
        }

        if (type == typeof(IOperationContext))
        {
            return new OperationContext(operation, context);
        }

        if (type == typeof(IOperationProgress))
        {
            return services.GetService(typeof(IOperationProgress)) ?? OperationProgress.None;
        }

        var ambient = services.GetService(type);
        if (ambient is null && !parameter.IsOptional)
        {
            throw new OperationBindingException(
                $"Parameter '{parameter.Name}' of operation '{operation.Name}' requires '{type.Name}', which is only available when the operation is invoked by the surface that provides it.");
        }

        return ambient;
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

/// <summary>
/// The outcome of <see cref="OperationInvoker.InvokeAsync"/>, which reports failures here instead of throwing,
/// including a missing confirmation policy, policy denials, binding errors and exceptions from the operation.
/// </summary>
public sealed record OperationInvocationResult(bool Succeeded, bool IsCancelled, object? Value, string? Error)
{
    /// <summary>
    /// Gets a value indicating whether <see cref="Error"/> came from an <see cref="OperationException"/> and must be
    /// shown to callers exactly as written, including newlines.
    /// </summary>
    public bool ErrorIsVerbatim { get; init; }

    public static OperationInvocationResult Success(object? value) => new(true, false, value, null);

    public static OperationInvocationResult Failure(string error) => new(false, false, null, error);

    /// <summary>Creates a failure whose <paramref name="error"/> adapters must surface unmodified.</summary>
    public static OperationInvocationResult VerbatimFailure(string error) => new(false, false, null, error) { ErrorIsVerbatim = true };

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
