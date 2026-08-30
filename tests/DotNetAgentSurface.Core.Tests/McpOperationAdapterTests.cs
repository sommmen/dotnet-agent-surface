using System.Text.Json;
using DotNetAgentSurface.Mcp;
using ModelContextProtocol.Protocol;

namespace DotNetAgentSurface.Core.Tests;

public sealed class McpOperationAdapterTests
{
    [Fact]
    public async Task GetTools_and_InvokeAsync_expose_catalog_operation()
    {
        var catalog = OperationCatalog.Discover(typeof(GreetingOperations));
        var adapter = new McpOperationAdapter(
            catalog,
            new OperationInvoker(new SingleServiceProvider(new GreetingOperations())));

        var tool = Assert.Single(adapter.GetTools());
        var arguments = new Dictionary<string, JsonElement>
        {
            ["name"] = JsonDocument.Parse("\"Ada\"").RootElement.Clone()
        };

        var result = await adapter.InvokeAsync("greet", arguments);

        Assert.Equal("greet", tool.Name);
        Assert.Equal("Greets a person", tool.Description);
        Assert.Null(result.IsError);
        Assert.Equal("\"Hello, Ada!\"", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public async Task InvokeAsync_returns_structured_error_for_unknown_operation()
    {
        var adapter = new McpOperationAdapter(
            OperationCatalog.Discover(typeof(GreetingOperations)),
            new OperationInvoker(new SingleServiceProvider(new GreetingOperations())));

        var result = await adapter.InvokeAsync("missing");

        Assert.True(result.IsError);
        Assert.Contains("Unknown operation 'missing'", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    private sealed class GreetingOperations
    {
        [AgentOperation("greet", "Greets a person")]
        public string Greet(string name) => $"Hello, {name}!";
    }

    private sealed class SingleServiceProvider(object service) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
    }
}
