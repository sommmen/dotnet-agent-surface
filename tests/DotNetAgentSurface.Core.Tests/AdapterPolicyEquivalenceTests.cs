using System.Text.Json;
using DotNetAgentSurface.CommandLine;
using DotNetAgentSurface.Mcp;

namespace DotNetAgentSurface.Core.Tests;

/// <summary>
/// Proves that Core, the CLI adapter, and the MCP adapter all enforce the same shared policy pipeline: a
/// denying policy must block invocation identically across every surface, without ever executing the operation.
/// </summary>
public sealed class AdapterPolicyEquivalenceTests
{
    [Fact]
    public async Task Core_denies_and_never_invokes_operation_when_policy_rejects_it()
    {
        var operations = new CountingOperations();
        var catalog = OperationCatalog.Discover(typeof(CountingOperations));
        var operation = catalog.Operations.Single();
        var invoker = CreateInvoker(operations);

        var result = await invoker.InvokeAsync(operation, Arguments());

        Assert.False(result.Succeeded);
        Assert.Equal("Operation 'increment' was denied by policy for testing.", result.Error);
        Assert.Equal(0, operations.InvocationCount);
    }

    [Fact]
    public async Task CommandLineAdapter_denies_and_never_invokes_operation_when_policy_rejects_it()
    {
        var operations = new CountingOperations();
        var catalog = OperationCatalog.Discover(typeof(CountingOperations));
        var adapter = new OperationCommandLineAdapter(catalog, CreateInvoker(operations));

        var result = await adapter.ExecuteAsync(["increment", "--amount", "1"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Operation 'increment' was denied by policy for testing.", result.Error);
        Assert.Equal(0, operations.InvocationCount);
    }

    [Fact]
    public async Task McpAdapter_denies_and_never_invokes_operation_when_policy_rejects_it()
    {
        var operations = new CountingOperations();
        var catalog = OperationCatalog.Discover(typeof(CountingOperations));
        var adapter = new McpOperationAdapter(catalog, CreateInvoker(operations));

        var result = await adapter.InvokeAsync("increment", Arguments());

        Assert.True(result.IsError);
        Assert.Contains(
            "Operation 'increment' was denied by policy for testing.",
            Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Equal(0, operations.InvocationCount);
    }

    private static OperationInvoker CreateInvoker(CountingOperations operations) =>
        new(new SingleServiceProvider(operations), policies: [new DenyAllPolicy()]);

    private static Dictionary<string, JsonElement> Arguments() => new()
    {
        ["amount"] = JsonDocument.Parse("1").RootElement.Clone()
    };

    private sealed class CountingOperations
    {
        public int InvocationCount { get; private set; }

        [AgentOperation("increment", "Increments a counter")]
        public int Increment(int amount) => InvocationCount += amount;
    }

    private sealed class DenyAllPolicy : IOperationInvocationPolicy
    {
        public ValueTask<OperationPolicyResult> EvaluateAsync(
            OperationDescriptor operation,
            IReadOnlyDictionary<string, JsonElement>? inputs,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(OperationPolicyResult.Deny($"Operation '{operation.Name}' was denied by policy for testing."));
    }

    private sealed class SingleServiceProvider(object service) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
    }
}
