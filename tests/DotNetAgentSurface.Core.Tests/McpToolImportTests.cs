using System.ComponentModel;
using System.Text.Json;
using DotNetAgentSurface.CommandLine;
using DotNetAgentSurface.Mcp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotNetAgentSurface.Core.Tests;

public sealed class McpToolImportTests
{
    private static OperationCatalog Import(Action<McpToolImportOptions>? configure = null) =>
        new OperationCatalogBuilder()
            .AddMcpServerTools(
                typeof(ImportedTools).Assembly,
                _ => new ImportedTools(),
                options =>
                {
                    options.Include = method => method.DeclaringType == typeof(ImportedTools);
                    configure?.Invoke(options);
                })
            .Build();

    private static OperationInvoker EmptyInvoker() => new(new EmptyServiceProvider());

    [Fact]
    public void Default_import_emits_only_explicitly_set_sdk_hints_and_keeps_safe_level()
    {
        var catalog = Import();
        var tool = Assert.Single(new McpOperationAdapter(catalog, EmptyInvoker()).GetTools(), tool => tool.Name == "echo");

        Assert.All(catalog.Operations, operation => Assert.Equal(AgentSafetyLevel.Safe, operation.SafetyLevel));
        Assert.Null(tool.Annotations);
    }

    [Fact]
    public void Explicit_destructive_mapping_imports_destructive_tools_as_dangerous()
    {
        var catalog = Import(options => options.SafetyMapping = McpToolSafetyMapping.DestructiveIsDangerous);

        Assert.Equal(AgentSafetyLevel.Dangerous, catalog.Operations.Single(operation => operation.Name == "echo").SafetyLevel);
        Assert.Equal(AgentSafetyLevel.Safe, catalog.Operations.Single(operation => operation.Name == "peek").SafetyLevel);
    }

    [Fact]
    public void Include_predicate_filters_tools_out()
    {
        var catalog = Import(options => options.Include = method => method.Name != nameof(ImportedTools.Echo) && method.DeclaringType == typeof(ImportedTools));

        Assert.DoesNotContain(catalog.Operations, operation => operation.Name == "echo");
        Assert.Contains(catalog.Operations, operation => operation.Name == "peek");
    }

    [Fact]
    public void Parameter_description_attribute_is_imported_and_wins_over_xml_documentation()
    {
        var catalog = Import();
        var parameter = Assert.Single(catalog.Operations.Single(operation => operation.Name == "echo").Parameters);

        Assert.Equal("Text to echo back", parameter.Description);

        using var schema = new OperationSchemaGenerator().GenerateInputSchema(catalog.Operations.Single(operation => operation.Name == "echo"));
        Assert.Equal("Text to echo back", schema.RootElement.GetProperty("properties").GetProperty("text").GetProperty("description").GetString());
    }

    [Fact]
    public void Parameter_description_attribute_wins_over_xml_param_text()
    {
        var catalog = new OperationCatalogBuilder()
            .UseDocumentation(new StubDocumentationSource())
            .AddFromType<DocumentedOperations>()
            .Build();

        var operation = catalog.Operations.Single(operation => operation.Name == "documented");
        Assert.Equal("Attribute text", operation.Parameters.Single(parameter => parameter.Name == "text").Description);
        Assert.Equal("XML other", operation.Parameters.Single(parameter => parameter.Name == "other").Description);
    }

    [Fact]
    public async Task Invoker_returns_failure_instead_of_throwing_when_no_confirmation_policy_is_registered()
    {
        var catalog = Import(options => options.SafetyMapping = McpToolSafetyMapping.DestructiveIsDangerous);
        var operation = catalog.Operations.Single(operation => operation.Name == "echo");

        var result = await EmptyInvoker().InvokeAsync(operation, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("IConfirmationEnforcingPolicy", result.Error);
    }

    [Fact]
    public async Task Cli_returns_exit_code_1_and_does_not_throw_for_a_dangerous_operation_without_a_policy()
    {
        var catalog = Import(options => options.SafetyMapping = McpToolSafetyMapping.DestructiveIsDangerous);
        var cli = new OperationCommandLineAdapter(catalog, EmptyInvoker());

        var result = await cli.ExecuteAsync(["echo", "--text", "hi"], TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("IConfirmationEnforcingPolicy", result.Error);
    }

    [Fact]
    public async Task Mcp_returns_is_error_and_does_not_throw_for_a_dangerous_operation_without_a_policy()
    {
        var catalog = Import(options => options.SafetyMapping = McpToolSafetyMapping.DestructiveIsDangerous);
        var adapter = new McpOperationAdapter(catalog, EmptyInvoker());
        var arguments = new Dictionary<string, JsonElement> { ["text"] = JsonSerializer.SerializeToElement("hi") };

        var result = await adapter.InvokeAsync("echo", arguments, TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Contains("IConfirmationEnforcingPolicy", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public async Task Default_imported_tool_is_invokable_through_cli_without_a_policy()
    {
        var cli = new OperationCommandLineAdapter(Import(), EmptyInvoker());

        var result = await cli.ExecuteAsync(["echo", "--text", "hi"], TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Adapters_flow_surface_into_the_invocation_context()
    {
        var recorder = new RecordingPolicy();
        var invoker = new OperationInvoker(new EmptyServiceProvider(), policies: [recorder]);
        var catalog = OperationCatalog.Discover(typeof(DocumentedOperations));
        var arguments = new Dictionary<string, JsonElement> { ["text"] = JsonSerializer.SerializeToElement("a") };

        await new OperationCommandLineAdapter(catalog, invoker).ExecuteAsync(["documented", "--text", "a"], TestContext.Current.CancellationToken, invocationContext: new OperationInvocationContext { RequestId = "r1" });
        await new McpOperationAdapter(catalog, invoker).InvokeAsync("documented", arguments, TestContext.Current.CancellationToken);

        Assert.Equal([AgentSurface.Cli, AgentSurface.Mcp], recorder.Surfaces);
        Assert.Equal("r1", recorder.RequestIds[0]);
        Assert.False(string.IsNullOrEmpty(recorder.RequestIds[1]));
    }

    [Fact]
    public async Task Operation_exception_message_is_surfaced_verbatim_by_both_adapters()
    {
        var invoker = new OperationInvoker(new SingleServiceProvider(new DocumentedOperations()));
        var catalog = OperationCatalog.Discover(typeof(DocumentedOperations));
        var message = "line one\nline two";

        var cli = await new OperationCommandLineAdapter(catalog, invoker).ExecuteAsync(["throws"], TestContext.Current.CancellationToken);
        var mcp = await new McpOperationAdapter(catalog, invoker).InvokeAsync("throws", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, cli.ExitCode);
        Assert.Equal(message, cli.Error);
        Assert.Equal(message, Assert.IsType<TextContentBlock>(Assert.Single(mcp.Content)).Text);
    }

    private sealed class SingleServiceProvider(object service) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    [McpServerToolType]
    private sealed class ImportedTools
    {
        [McpServerTool(Name = "echo"), Description("Echo")]
        public string Echo([Description("Text to echo back")] string text) => text;

        [McpServerTool(Name = "peek", Destructive = false, ReadOnly = true), Description("Peek")]
        public string Peek() => "peek";
    }

    private sealed class DocumentedOperations
    {
        [AgentOperation("documented", "Documented")]
        public string Documented([Description("Attribute text")] string text, string other = "") => text + other;

        [AgentOperation("throws", "Throws")]
        public void Throws() => throw new OperationException("line one\nline two");
    }

    private sealed class StubDocumentationSource : IOperationDocumentationSource
    {
        public OperationDocumentation GetDocumentation(System.Reflection.MethodInfo method) => new(
            "Summary",
            null,
            new Dictionary<string, string> { ["text"] = "XML text", ["other"] = "XML other" },
            null);
    }

    private sealed class RecordingPolicy : IOperationInvocationPolicy
    {
        public List<AgentSurface> Surfaces { get; } = [];

        public List<string?> RequestIds { get; } = [];

        public ValueTask<OperationPolicyResult> EvaluateAsync(
            OperationDescriptor operation,
            IReadOnlyDictionary<string, JsonElement>? inputs,
            OperationConfirmation? confirmation = null,
            CancellationToken cancellationToken = default,
            OperationInvocationContext? invocationContext = null)
        {
            Surfaces.Add(invocationContext!.Surface);
            RequestIds.Add(invocationContext.RequestId);
            return ValueTask.FromResult(OperationPolicyResult.Allow());
        }
    }
}
