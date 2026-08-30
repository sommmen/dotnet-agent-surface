using System.Text.Json;

namespace DotNetAgentSurface.Core.Tests;

public sealed class OperationInvokerTests
{
    [Fact]
    public async Task InvokeAsync_binds_values_defaults_and_service_target()
    {
        var operation = OperationCatalog.Discover(typeof(MathOperations)).Operations.Single();
        var invoker = new OperationInvoker(new SingleServiceProvider(new MathOperations()));
        var inputs = new Dictionary<string, JsonElement> { ["left"] = JsonDocument.Parse("3").RootElement.Clone() };

        var result = await invoker.InvokeAsync(operation, inputs);

        Assert.True(result.Succeeded);
        Assert.Equal(5, result.Value);
    }

    [Fact]
    public async Task InvokeAsync_returns_stable_failure_for_missing_required_input()
    {
        var operation = OperationCatalog.Discover(typeof(MathOperations)).Operations.Single();
        var invoker = new OperationInvoker(new SingleServiceProvider(new MathOperations()));

        var result = await invoker.InvokeAsync(operation);

        Assert.False(result.Succeeded);
        Assert.Contains("Required input 'left'", result.Error);
    }

    [Fact]
    public async Task InvokeAsync_awaits_task_result()
    {
        var operation = OperationCatalog.Discover(typeof(AsyncOperations)).Operations.Single();
        var invoker = new OperationInvoker(new SingleServiceProvider(new AsyncOperations()));
        var inputs = new Dictionary<string, JsonElement> { ["value"] = JsonDocument.Parse("\"hello\"").RootElement.Clone() };

        var result = await invoker.InvokeAsync(operation, inputs);

        Assert.True(result.Succeeded);
        Assert.Equal("HELLO", result.Value);
    }

    private sealed class MathOperations
    {
        [AgentOperation("add", "Adds two numbers")]
        public int Add(int left, int right = 2) => left + right;
    }

    private sealed class AsyncOperations
    {
        [AgentOperation("upper", "Uppercases a value")]
        public async ValueTask<string> Upper(string value)
        {
            await Task.Yield();
            return value.ToUpperInvariant();
        }
    }

    private sealed class SingleServiceProvider(object service) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
    }
}
