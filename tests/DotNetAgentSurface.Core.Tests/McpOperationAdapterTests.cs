using System.ComponentModel;
using System.Text.Json;
using DotNetAgentSurface.CommandLine;
using DotNetAgentSurface.Mcp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

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

        var tool = Assert.Single(adapter.GetTools(), tool => tool.Name == "greet");
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
    public async Task InvokeAsync_resolves_operation_by_alias()
    {
        var adapter = new McpOperationAdapter(
            OperationCatalog.Discover(typeof(GreetingOperations)),
            new OperationInvoker(new SingleServiceProvider(new GreetingOperations())));

        var arguments = new Dictionary<string, JsonElement>
        {
            ["name"] = JsonDocument.Parse("\"Ada\"").RootElement.Clone()
        };

        var result = await adapter.InvokeAsync("hello", arguments);

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

    [Fact]
    public async Task InvokeAsync_returns_structured_error_when_operation_is_cancelled()
    {
        var adapter = new McpOperationAdapter(
            OperationCatalog.Discover(typeof(GreetingOperations)),
            new OperationInvoker(new SingleServiceProvider(new GreetingOperations())));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await adapter.InvokeAsync("slow", cancellationToken: cts.Token);

        Assert.True(result.IsError);
        Assert.Contains("cancel", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeAsync_returns_structured_error_for_missing_required_argument()
    {
        var adapter = new McpOperationAdapter(
            OperationCatalog.Discover(typeof(GreetingOperations)),
            new OperationInvoker(new SingleServiceProvider(new GreetingOperations())));

        var result = await adapter.InvokeAsync("repeat");

        Assert.True(result.IsError);
        Assert.Contains("times", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public async Task InvokeAsync_returns_structured_error_when_reflected_operation_throws()
    {
        var adapter = new McpOperationAdapter(
            OperationCatalog.Discover(typeof(GreetingOperations)),
            new OperationInvoker(
                new SingleServiceProvider(new GreetingOperations()),
                policies: [new DangerousOperationConfirmationPolicy((_, _, _) => ValueTask.FromResult(true))]));

        var result = await adapter.InvokeAsync("fail");

        Assert.True(result.IsError);
        Assert.Contains("boom", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public void GetTools_annotates_safe_and_dangerous_operations()
    {
        var adapter = new McpOperationAdapter(
            OperationCatalog.Discover(typeof(GreetingOperations)),
            new OperationInvoker(new SingleServiceProvider(new GreetingOperations())));

        var tools = adapter.GetTools().ToDictionary(tool => tool.Name);

        Assert.True(tools["greet"].Annotations!.ReadOnlyHint);
        Assert.False(tools["greet"].Annotations!.DestructiveHint);
        Assert.False(tools["fail"].Annotations!.ReadOnlyHint);
        Assert.True(tools["fail"].Annotations!.DestructiveHint);
    }

    [Fact]
    public async Task AddMcpServerTools_registers_native_tools_for_catalog_projection()
    {
        var catalog = new OperationCatalogBuilder()
            .AddMcpServerTools(typeof(NativeMcpTools).Assembly, type =>
                type == typeof(NativeMcpTools) ? new NativeMcpTools() : null)
            .Build();
        var confirmationPolicies = new IOperationInvocationPolicy[]
        {
            new DangerousOperationConfirmationPolicy((_, _, _) => ValueTask.FromResult(true)),
        };
        var adapter = new McpOperationAdapter(
            catalog,
            new OperationInvoker(new SingleServiceProvider(new NativeMcpTools()), policies: confirmationPolicies));
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"dotnet-agent-surface-{Guid.NewGuid():N}");

        try
        {
            new SkillReferenceGenerator().Generate(catalog, outputDirectory);

            var operation = Assert.Single(catalog.Operations);
            var tool = Assert.Single(adapter.GetTools());
            var commandLine = new OperationCommandLineAdapter(
                catalog,
                new OperationInvoker(new SingleServiceProvider(new NativeMcpTools()), policies: confirmationPolicies));
            var cliResult = await commandLine.ExecuteAsync(["native-greet", "--name", "\"Ada\""]);
            var result = await adapter.InvokeAsync("native-greet", ArgumentsFor("Ada"));

            Assert.Equal("native-greet", operation.Name);
            Assert.Equal("Native MCP greeting", operation.Description);
            Assert.True(operation.IsIdempotent);
            Assert.Equal(AgentSafetyLevel.Dangerous, operation.SafetyLevel);
            Assert.Equal("native-greet", tool.Name);
            Assert.Equal(0, cliResult.ExitCode);
            Assert.Equal("\"Hello, Ada!\"", cliResult.Output);
            Assert.Contains("`native-greet`", File.ReadAllText(Path.Combine(outputDirectory, "SKILL.md")));
            Assert.Equal("\"Hello, Ada!\"", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    private static Dictionary<string, JsonElement> ArgumentsFor(string name) => new()
    {
        ["name"] = JsonDocument.Parse(JsonSerializer.Serialize(name)).RootElement.Clone()
    };

    [McpServerToolType]
    private sealed class NativeMcpTools
    {
        [McpServerTool(Name = "native-greet", Title = "Native MCP greeting", Destructive = true, Idempotent = true)]
        [Description("Native MCP greeting")]
        public string Greet(string name) => $"Hello, {name}!";
    }

    private sealed class GreetingOperations
    {
        [AgentOperation("greet", "Greets a person", Aliases = ["hello"])]
        public string Greet(string name) => $"Hello, {name}!";

        [AgentOperation("fail", "Always fails", SafetyLevel = AgentSafetyLevel.Dangerous)]
        public void Fail() => throw new InvalidOperationException("boom");

        [AgentOperation("slow", "Checks cancellation")]
        public void Slow(CancellationToken cancellationToken) => cancellationToken.ThrowIfCancellationRequested();

        [AgentOperation("repeat", "Repeats a greeting")]
        public string Repeat(int times) => string.Concat(Enumerable.Repeat("Hi! ", times));
    }

    private sealed class SingleServiceProvider(object service) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
    }
}
