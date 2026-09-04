using System.Text.Json;

namespace DotNetAgentSurface.Core.Tests;

public sealed class OperationInvocationPolicyTests
{
    [Fact]
    public async Task InvokeAsync_does_not_invoke_operation_when_policy_denies_request()
    {
        var operations = new DangerousOperations();
        var operation = OperationCatalog.Discover(typeof(DangerousOperations)).Operations.Single();
        var invoker = new OperationInvoker(
            new SingleServiceProvider(operations),
            policies: [new DangerousOperationConfirmationPolicy()]);

        var result = await invoker.InvokeAsync(operation);

        Assert.False(result.Succeeded);
        Assert.Equal("Operation 'delete-all' requires explicit confirmation.", result.Error);
        Assert.False(operations.WasInvoked);
    }

    [Fact]
    public async Task InvokeAsync_allows_dangerous_operation_when_confirmation_policy_approves_it()
    {
        var operations = new DangerousOperations();
        var operation = OperationCatalog.Discover(typeof(DangerousOperations)).Operations.Single();
        var invoker = new OperationInvoker(
            new SingleServiceProvider(operations),
            policies: [new DangerousOperationConfirmationPolicy((_, _, _) => ValueTask.FromResult(true))]);

        var result = await invoker.InvokeAsync(operation);

        Assert.True(result.Succeeded);
        Assert.True(operations.WasInvoked);
    }

    [Fact]
    public async Task InvokeAsync_runs_custom_policy_before_binding_and_invocation()
    {
        var operations = new DangerousOperations();
        var operation = OperationCatalog.Discover(typeof(DangerousOperations)).Operations.Single();
        var policy = new RecordingPolicy();
        var invoker = new OperationInvoker(new SingleServiceProvider(operations), policies: [policy]);

        var result = await invoker.InvokeAsync(operation);

        Assert.True(result.Succeeded);
        Assert.True(policy.WasEvaluated);
        Assert.True(operations.WasInvoked);
    }

    private sealed class DangerousOperations
    {
        public bool WasInvoked { get; private set; }

        [AgentOperation("delete-all", "Deletes all data", SafetyLevel = AgentSafetyLevel.Dangerous)]
        public void DeleteAll() => WasInvoked = true;
    }

    private sealed class RecordingPolicy : IOperationInvocationPolicy
    {
        public bool WasEvaluated { get; private set; }

        public ValueTask<OperationPolicyResult> EvaluateAsync(
            OperationDescriptor operation,
            IReadOnlyDictionary<string, JsonElement>? inputs,
            OperationConfirmation? confirmation = null,
            CancellationToken cancellationToken = default)
        {
            WasEvaluated = true;
            return ValueTask.FromResult(OperationPolicyResult.Allow());
        }
    }

    private sealed class SingleServiceProvider(object service) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
    }
}
