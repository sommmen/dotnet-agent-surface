using System.Reflection;
using System.Text.Json;

namespace DotNetAgentSurface.Core;

public sealed class OperationInvoker
{
    private readonly IServiceProvider _services;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly IReadOnlyList<IOperationInvocationPolicy> _policies;

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
        CancellationToken cancellationToken = default)
    {
        Guard.ThrowIfNull(operation);

        try
        {
            foreach (var policy in _policies)
            {
                var decision = await policy.EvaluateAsync(operation, inputs, cancellationToken).ConfigureAwait(false);
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

    private object? ResolveTarget(OperationDescriptor operation)
    {
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
