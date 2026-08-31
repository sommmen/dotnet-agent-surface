using System.Text.Json.Nodes;
using DotNetAgentSurface.CommandLine;
using DotNetAgentSurface.Core;

namespace DotNetAgentSurface.CommandLine.Tests;

public sealed class AxiOutputContractTests
{
    [Fact]
    public async Task Defaults_to_toon_with_projected_list_count()
    {
        var result = await CreateAdapter().ExecuteAsync(["list"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("count: 2", result.Output);
        Assert.Contains("items[2]", result.Output);
        Assert.DoesNotContain("ignored", result.Output);
    }

    [Fact]
    public async Task Json_output_includes_requested_fields()
    {
        var result = await CreateAdapter().ExecuteAsync(["list", "--fields", "name,description", "--output", "json"]);

        Assert.Equal(0, result.ExitCode);
        var output = JsonNode.Parse(result.Output)!.AsObject();
        Assert.Equal(2, output["count"]!.GetValue<int>());
        var firstItem = output["items"]!.AsArray()[0]!.AsObject();
        Assert.Equal("first", firstItem["name"]!.GetValue<string>());
        Assert.NotNull(firstItem["description"]);
        Assert.Null(firstItem["id"]);
    }

    [Fact]
    public async Task Unknown_requested_field_returns_usage_error()
    {
        var result = await CreateAdapter().ExecuteAsync(["list", "--fields", "missing"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Unknown output field(s): missing", result.Error);
    }

    [Fact]
    public async Task Fields_on_primitive_list_returns_usage_error()
    {
        var result = await CreateAdapter().ExecuteAsync(["numbers", "--fields", "value"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("only applies to list results made up of objects", result.Error);
    }

    [Fact]
    public async Task Truncates_large_strings_unless_full_is_requested()
    {
        var truncated = await CreateAdapter().ExecuteAsync(["long", "--output", "json"]);
        var full = await CreateAdapter().ExecuteAsync(["long", "--output", "json", "--full"]);

        Assert.Contains("truncated, showing 1000 of 1001 chars", truncated.Output);
        Assert.Contains("Use --full", truncated.Output);
        Assert.DoesNotContain("truncated, showing", full.Output);
        Assert.Contains(new string('x', 1001), full.Output);
    }

    [Fact]
    public async Task Empty_list_has_count_and_explicit_message()
    {
        var result = await CreateAdapter().ExecuteAsync(["empty", "--output", "json"]);

        Assert.Equal(0, result.ExitCode);
        var output = JsonNode.Parse(result.Output)!.AsObject();
        Assert.Equal(0, output["count"]!.GetValue<int>());
        Assert.Equal("No results.", output["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Legacy_constructor_preserves_raw_json()
    {
        var operations = new OutputOperations();
        var catalog = OperationCatalog.Discover(typeof(OutputOperations));
        var adapter = new OperationCommandLineAdapter(catalog, new OperationInvoker(new SingleServiceProvider(operations)));

        var result = await adapter.ExecuteAsync(["empty"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("[]", result.Output);
    }

    private static OperationCommandLineAdapter CreateAdapter()
    {
        var operations = new OutputOperations();
        var catalog = OperationCatalog.Discover(typeof(OutputOperations));
        return new OperationCommandLineAdapter(catalog, new OperationInvoker(new SingleServiceProvider(operations)), new ToonAgentOutputRenderer());
    }

    private sealed class OutputOperations
    {
        [AgentOperation("list", "Lists output objects")]
        public object[] List() =>
        [
            new { id = 1, name = "first", description = "first description", active = true, ignored = "ignored" },
            new { id = 2, name = "second", description = "second description", active = false, ignored = "ignored" },
        ];

        [AgentOperation("numbers", "Lists numbers")]
        public int[] Numbers() => [1, 2];

        [AgentOperation("long", "Returns long text")]
        public object Long() => new { text = new string('x', 1001) };

        [AgentOperation("empty", "Returns no objects")]
        public object[] Empty() => [];
    }

    private sealed class SingleServiceProvider(object service) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
    }
}
