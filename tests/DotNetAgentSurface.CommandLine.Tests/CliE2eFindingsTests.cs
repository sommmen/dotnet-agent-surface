using DotNetAgentSurface.Core;

namespace DotNetAgentSurface.CommandLine.Tests;

public sealed class CliE2EFindingsTests
{
    [Fact]
    public async Task Missing_required_parameters_exit_2_and_name_every_missing_flag()
    {
        var adapter = CreateAdapter();

        var none = await adapter.ExecuteAsync(["run-query"], TestContext.Current.CancellationToken);
        var partial = await adapter.ExecuteAsync(["run-query", "--context-name", "main"], TestContext.Current.CancellationToken);
        var supplied = await adapter.ExecuteAsync(["run-query", "--context-name", "main", "--sql", "select 1"], TestContext.Current.CancellationToken);
        var optional = await adapter.ExecuteAsync(["run-query", "--context-name", "main", "--sql", "x", "--note", "n"], TestContext.Current.CancellationToken);

        Assert.Equal(2, none.ExitCode);
        Assert.Contains("missing required --context-name, --sql", none.Error);
        Assert.Equal(2, partial.ExitCode);
        Assert.Contains("missing required --sql", partial.Error);
        Assert.DoesNotContain("--context-name", partial.Error);
        Assert.Equal("main|select 1|", supplied.Output);
        Assert.Equal("main|x|n", optional.Output);
    }

    [Fact]
    public async Task Explicit_null_style_values_still_satisfy_a_required_nullable_parameter()
    {
        var result = await CreateAdapter().ExecuteAsync(["nullable-required", "--name", "x"], TestContext.Current.CancellationToken);
        var missing = await CreateAdapter().ExecuteAsync(["nullable-required"], TestContext.Current.CancellationToken);

        Assert.Equal("x", result.Output);
        Assert.Equal(2, missing.ExitCode);
        Assert.Contains("missing required --name", missing.Error);
    }

    [Theory]
    [InlineData("--fields", "a,b", false)]
    [InlineData("--full", null, false)]
    [InlineData("--fields", "a,b", true)]
    public async Task Fields_and_full_are_rejected_for_text_results(string flag, string? value, bool withJsonOutput)
    {
        string[] args = value is null ? ["text", flag] : ["text", flag, value];
        if (withJsonOutput)
        {
            args = [.. args, "--output", "json"];
        }

        var result = await CreateAdapter().ExecuteAsync(args, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains($"{flag} is not supported for this operation's text output", result.Error);
        Assert.DoesNotContain("Use --output", result.Error);
    }

    [Fact]
    public async Task Fields_are_still_applied_to_object_results_and_text_without_the_flags_is_verbatim()
    {
        var text = await CreateAdapter().ExecuteAsync(["text"], TestContext.Current.CancellationToken);
        var obj = await CreateAdapter().ExecuteAsync(["rows", "--fields", "name", "--output", "json"], TestContext.Current.CancellationToken);

        Assert.Equal("plain", text.Output);
        Assert.Contains("\"Name\":\"n\"", obj.Output);
        Assert.DoesNotContain("Other", obj.Output);
    }

    [Fact]
    public async Task Operations_receive_the_requested_output_format_on_the_operation_context()
    {
        var adapter = CreateAdapter();

        var none = await adapter.ExecuteAsync(["format"], TestContext.Current.CancellationToken);
        var json = await adapter.ExecuteAsync(["--output", "json", "format"], TestContext.Current.CancellationToken);
        var toon = await adapter.ExecuteAsync(["format", "--format=TOON"], TestContext.Current.CancellationToken);

        Assert.Equal("(none)", none.Output);
        Assert.Equal("json", json.Output);
        Assert.Equal("toon", toon.Output);
    }

    [Theory]
    [InlineData(new[] { "--output", "json", "x" }, "json")]
    [InlineData(new[] { "x", "--format=toon" }, "toon")]
    [InlineData(new[] { "--connection", "json", "x", "--output", "toon" }, "toon")]
    [InlineData(new[] { "export", "--output", "folder" }, null)]
    [InlineData(new[] { "skill", "generate", "--output", "json" }, null)]
    [InlineData(new[] { "x" }, null)]
    public void ReadOutputFormat_finds_a_json_or_toon_format_anywhere_but_not_in_host_verbs(string[] args, string? expected)
    {
        Assert.Equal(expected, OperationCommandLineAdapter.ReadOutputFormat(args, [new CommandLineGlobalOption("--connection")]));
    }

    [Fact]
    public async Task Help_uses_friendly_type_names()
    {
        var help = await CreateAdapter().ExecuteAsync(["types", "--help"], TestContext.Current.CancellationToken);

        Assert.Contains("--names <string[] (repeatable)> (required)", help.Output);
        Assert.Contains("--limit <integer> (required)", help.Output);
        Assert.Contains("--enabled [true|false]", help.Output);
        Assert.Contains("--kind <Alpha|Beta> (required)", help.Output);
        Assert.Contains("--paging <object (JSON: skip, take)> (required)", help.Output);
        Assert.Contains("--raw <JSON> (required)", help.Output);
        Assert.DoesNotContain("IReadOnlyList", help.Output);
        Assert.DoesNotContain("`1", help.Output);
        Assert.DoesNotContain("Paging>", help.Output);
    }

    [Fact]
    public async Task Unknown_operations_list_names_only_and_suggest_close_matches()
    {
        var result = await CreateAdapter().ExecuteAsync(["run-qeury"], TestContext.Current.CancellationToken);
        var far = await CreateAdapter().ExecuteAsync(["zzzzzz"], TestContext.Current.CancellationToken);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Unknown operation 'run-qeury'", result.Error);
        Assert.Contains("Did you mean: run-query?", result.Error);
        Assert.Contains("run-query", result.Error);
        Assert.DoesNotContain("Runs a query", result.Error);
        Assert.Equal(2, far.ExitCode);
        Assert.DoesNotContain("Did you mean", far.Error);
        Assert.DoesNotContain("Runs a query", far.Error);
    }

    [Fact]
    public async Task Unknown_operations_suggest_at_most_three_matches()
    {
        var result = await CreateAdapter().ExecuteAsync(["tex"], TestContext.Current.CancellationToken);

        var suggestions = result.Error!.Split('\n').Single(line => line.StartsWith("Did you mean", StringComparison.Ordinal));
        Assert.True(suggestions.Split(',').Length <= 3);
        Assert.Contains("text", suggestions);
    }

    private static OperationCommandLineAdapter CreateAdapter() =>
        new(OperationCatalog.Discover(typeof(Operations)), new OperationInvoker(new SingleServiceProvider(new Operations())), new ToonAgentOutputRenderer());

    private enum Kind
    {
        Alpha,
        Beta,
    }

    private sealed record Paging
    {
        public int Skip { get; init; }

        public int Take { get; init; }
    }

    private sealed record Row
    {
        public string Name { get; init; } = string.Empty;

        public string Other { get; init; } = string.Empty;
    }

    private sealed class Operations
    {
        [AgentOperation("run-query", "Runs a query")]
        public string RunQuery(string contextName, string sql, string? note = null) => $"{contextName}|{sql}|{note}";

        [AgentOperation("nullable-required", "Has a nullable parameter without a default")]
        public string NullableRequired(string? name) => name ?? "null";

        [AgentOperation("text", "Returns text")]
        public string Text() => "plain";

        [AgentOperation("text-extra", "Returns text too")]
        public string TextExtra() => "plain";

        [AgentOperation("texture", "Returns text three")]
        public string Texture() => "plain";

        [AgentOperation("textile", "Returns text four")]
        public string Textile() => "plain";

        [AgentOperation("rows", "Returns rows")]
        public Row[] Rows() => [new Row { Name = "n", Other = "o" }];

        [AgentOperation("format", "Echoes the requested format")]
        public string Format(IOperationContext context) => context.OutputFormat ?? "(none)";

        [AgentOperation("types", "Shows types")]
        public string Types(IReadOnlyList<string> names, int limit, Kind kind, Paging paging, System.Text.Json.JsonElement raw, bool enabled = false) => string.Empty;
    }

    private sealed class SingleServiceProvider(object service) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
    }
}
