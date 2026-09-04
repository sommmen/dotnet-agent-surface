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

    [Theory]
    [InlineData("confirm-operation")]
    [InlineData("dangerous-operation")]
    public async Task CommandLineAdapter_requires_explicit_confirmation_without_prompting(string operationName)
    {
        var operations = new ConfirmationOperations();
        var adapter = new OperationCommandLineAdapter(
            OperationCatalog.Discover(typeof(ConfirmationOperations)),
            CreateConfirmationInvoker(operations));

        var result = await adapter.ExecuteAsync([operationName]);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal($"Operation '{operationName}' requires explicit confirmation.", result.Error);
        Assert.Equal(0, operations.InvocationCount);
    }

    [Fact]
    public async Task CommandLineAdapter_approves_confirm_operation_with_confirm_flag()
    {
        var operations = new ConfirmationOperations();
        var adapter = new OperationCommandLineAdapter(
            OperationCatalog.Discover(typeof(ConfirmationOperations)),
            CreateConfirmationInvoker(operations));

        var result = await adapter.ExecuteAsync(["confirm-operation", "--confirm"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, operations.InvocationCount);
    }

    [Fact]
    public async Task CommandLineAdapter_requires_yes_in_addition_to_confirm_for_dangerous_operation()
    {
        var operations = new ConfirmationOperations();
        var adapter = new OperationCommandLineAdapter(
            OperationCatalog.Discover(typeof(ConfirmationOperations)),
            CreateConfirmationInvoker(operations));

        var denied = await adapter.ExecuteAsync(["dangerous-operation", "--confirm"]);
        var approved = await adapter.ExecuteAsync(["dangerous-operation", "--confirm", "--yes"]);

        Assert.Equal(1, denied.ExitCode);
        Assert.Equal("Operation 'dangerous-operation' requires explicit confirmation.", denied.Error);
        Assert.Equal(0, approved.ExitCode);
        Assert.Equal(1, operations.InvocationCount);
    }

    [Theory]
    [InlineData("confirm-operation", true, false)]
    [InlineData("dangerous-operation", true, true)]
    public async Task McpAdapter_uses_the_same_confirmation_contract_for_approved_operations(
        string operationName,
        bool confirmed,
        bool dangerousConfirmed)
    {
        var operations = new ConfirmationOperations();
        var adapter = new McpOperationAdapter(
            OperationCatalog.Discover(typeof(ConfirmationOperations)),
            CreateConfirmationInvoker(operations));

        var result = await adapter.InvokeAsync(
            operationName,
            confirmation: new OperationConfirmation(confirmed, dangerousConfirmed));

        Assert.Null(result.IsError);
        Assert.Equal(1, operations.InvocationCount);
    }

    [Theory]
    [InlineData("confirm-operation", false, false)]
    [InlineData("dangerous-operation", false, false)]
    [InlineData("dangerous-operation", true, false)]
    public async Task McpAdapter_denies_insufficient_confirmation_without_executing(
        string operationName,
        bool confirmed,
        bool dangerousConfirmed)
    {
        var operations = new ConfirmationOperations();
        var adapter = new McpOperationAdapter(
            OperationCatalog.Discover(typeof(ConfirmationOperations)),
            CreateConfirmationInvoker(operations));

        var result = await adapter.InvokeAsync(
            operationName,
            confirmation: new OperationConfirmation(confirmed, dangerousConfirmed));

        Assert.True(result.IsError);
        Assert.Contains(
            $"Operation '{operationName}' requires explicit confirmation.",
            Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Equal(0, operations.InvocationCount);
    }

    private static OperationInvoker CreateInvoker(CountingOperations operations) =>
        new(new SingleServiceProvider(operations), policies: [new DenyAllPolicy()]);

    private static OperationInvoker CreateConfirmationInvoker(ConfirmationOperations operations) =>
        new(new SingleServiceProvider(operations), policies: [new DangerousOperationConfirmationPolicy()]);

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

    private sealed class ConfirmationOperations
    {
        public int InvocationCount { get; private set; }

        [AgentOperation("confirm-operation", "Requires confirmation", SafetyLevel = AgentSafetyLevel.Confirm)]
        public int Confirm() => ++InvocationCount;

        [AgentOperation("dangerous-operation", "Requires dangerous confirmation", SafetyLevel = AgentSafetyLevel.Dangerous)]
        public int Dangerous() => ++InvocationCount;
    }

    private sealed class DenyAllPolicy : IOperationInvocationPolicy
    {
        public ValueTask<OperationPolicyResult> EvaluateAsync(
            OperationDescriptor operation,
            IReadOnlyDictionary<string, JsonElement>? inputs,
            OperationConfirmation? confirmation = null,
            CancellationToken cancellationToken = default,
            OperationInvocationContext? invocationContext = null) =>
            ValueTask.FromResult(OperationPolicyResult.Deny($"Operation '{operation.Name}' was denied by policy for testing."));
    }

    private sealed class SingleServiceProvider(object service) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
    }
}
