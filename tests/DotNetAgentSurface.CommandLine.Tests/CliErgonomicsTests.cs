using System.ComponentModel;
using DotNetAgentSurface.Core;

namespace DotNetAgentSurface.CommandLine.Tests;

public sealed class CliErgonomicsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"agent-surface-cli-{Guid.NewGuid():N}");

    public CliErgonomicsTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task String_results_are_verbatim_and_never_truncated_or_quoted()
    {
        var text = "line \"one\"\n" + new string('x', 2500);
        var operations = new Operations { Text = text };

        var toon = await CreateAdapter(operations, new ToonAgentOutputRenderer()).ExecuteAsync(["text"], TestContext.Current.CancellationToken);
        var json = await CreateAdapter(operations, new JsonAgentOutputRenderer()).ExecuteAsync(["text", "--output", "json"], TestContext.Current.CancellationToken);
        var legacy = await CreateAdapter(operations, renderer: null).ExecuteAsync(["text"], TestContext.Current.CancellationToken);

        Assert.Equal(text, toon.Output);
        Assert.Equal(text, json.Output);
        Assert.Equal(text, legacy.Output);
        Assert.True(toon.IsVerbatimOutput);
    }

    [Fact]
    public async Task Object_results_still_project_and_truncate()
    {
        var result = await CreateAdapter(new Operations(), new JsonAgentOutputRenderer()).ExecuteAsync(["long-object"], TestContext.Current.CancellationToken);

        Assert.Contains("truncated, showing 1000 of 1500 chars", result.Output);
    }

    [Fact]
    public async Task Operation_errors_keep_newlines_unless_flattening_is_requested()
    {
        var kept = await CreateAdapter(new Operations()).ExecuteAsync(["fail-plain"], TestContext.Current.CancellationToken);
        var flattened = await CreateAdapter(new Operations(), options: new OperationCommandLineOptions { FlattenOperationErrors = true }).ExecuteAsync(["fail-plain"], TestContext.Current.CancellationToken);

        Assert.Equal(1, kept.ExitCode);
        Assert.Equal("first\nsecond", kept.Error);
        Assert.Equal("first second", flattened.Error);
    }

    [Fact]
    public async Task Custom_renderer_sees_the_raw_value()
    {
        object? seen = null;
        var options = new OperationCommandLineOptions
        {
            ResultRenderer = (_, value) =>
            {
                seen = value;
                return "custom";
            },
        };

        var result = await CreateAdapter(new Operations(), new ToonAgentOutputRenderer(), options).ExecuteAsync(["long-object"], TestContext.Current.CancellationToken);

        Assert.Equal("custom", result.Output);
        Assert.Equal(1500, Assert.IsType<LongObject>(seen).Text.Length);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{\"a\":1}")]
    [InlineData("\"quoted\"")]
    public async Task String_parameters_receive_the_text_verbatim(string value)
    {
        var result = await CreateAdapter(new Operations()).ExecuteAsync(["echo", "--value", value], TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(value, result.Output);
    }

    [Fact]
    public async Task Guid_and_enum_parameters_are_not_json_parsed()
    {
        var id = Guid.NewGuid();
        var adapter = CreateAdapter(new Operations());

        var guid = await adapter.ExecuteAsync(["describe", "--id", id.ToString(), "--mode", "beta"], TestContext.Current.CancellationToken);
        var badMode = await adapter.ExecuteAsync(["describe", "--id", id.ToString(), "--mode", "nope"], TestContext.Current.CancellationToken);

        Assert.Equal($"{id}|Beta|False|0", guid.Output);
        Assert.Equal(2, badMode.ExitCode);
        Assert.Contains("Valid values: Alpha, Beta", badMode.Error);
    }

    [Fact]
    public async Task Supports_equals_syntax_and_complex_json_values()
    {
        var result = await CreateAdapter(new Operations()).ExecuteAsync(["sum", "--numbers=[1,2,3]"], TestContext.Current.CancellationToken);

        Assert.Equal("6", result.Output);
    }

    [Fact]
    public async Task Supports_bare_boolean_switches_and_negation()
    {
        var adapter = CreateAdapter(new Operations());
        var id = Guid.Empty.ToString();

        var on = await adapter.ExecuteAsync(["describe", "--id", id, "--include-all-builds"], TestContext.Current.CancellationToken);
        var onCamel = await adapter.ExecuteAsync(["describe", "--id", id, "--includeAllBuilds"], TestContext.Current.CancellationToken);
        var off = await adapter.ExecuteAsync(["describe", "--id", id, "--include-all-builds", "--no-include-all-builds"], TestContext.Current.CancellationToken);
        var explicitFalse = await adapter.ExecuteAsync(["describe", "--id", id, "--include-all-builds=false"], TestContext.Current.CancellationToken);

        Assert.EndsWith("|True|0", on.Output);
        Assert.EndsWith("|True|0", onCamel.Output);
        Assert.EndsWith("|False|0", off.Output);
        Assert.EndsWith("|False|0", explicitFalse.Output);
    }

    [Fact]
    public async Task Accepts_kebab_case_parameters_and_snake_case_operation_names()
    {
        var adapter = CreateAdapter(new Operations());

        var viaKebab = await adapter.ExecuteAsync(["run-query", "--context-name", "main"], TestContext.Current.CancellationToken);
        var viaSnake = await adapter.ExecuteAsync(["run_query", "--contextName", "main"], TestContext.Current.CancellationToken);
        var help = await adapter.ExecuteAsync(["run-query", "--help"], TestContext.Current.CancellationToken);
        var root = await adapter.ExecuteAsync([], TestContext.Current.CancellationToken);

        Assert.Equal("main", viaKebab.Output);
        Assert.Equal("main", viaSnake.Output);
        Assert.Contains("--context-name <string> (required", help.Output);
        Assert.Contains("run-query", root.Output);
        Assert.DoesNotContain("run_query", root.Output);
    }

    [Fact]
    public async Task Reads_values_from_files_stdin_and_escapes_literal_at_signs()
    {
        var file = Path.Combine(_directory, "query.sql");
        await File.WriteAllTextAsync(file, "SELECT 1\nFROM t", TestContext.Current.CancellationToken);
        var options = new OperationCommandLineOptions { Input = new StringReader("from stdin") };
        var adapter = CreateAdapter(new Operations(), options: options);

        var fromFile = await adapter.ExecuteAsync(["echo", "--value", "@" + file], TestContext.Current.CancellationToken);
        var fromStdin = await adapter.ExecuteAsync(["echo", "--value", "-"], TestContext.Current.CancellationToken);
        var literal = await adapter.ExecuteAsync(["echo", "--value", "@@user"], TestContext.Current.CancellationToken);
        var missing = await adapter.ExecuteAsync(["echo", "--value", "@" + Path.Combine(_directory, "absent.txt")], TestContext.Current.CancellationToken);

        Assert.Equal("SELECT 1\nFROM t", fromFile.Output);
        Assert.Equal("from stdin", fromStdin.Output);
        Assert.Equal("@user", literal.Output);
        Assert.Equal(2, missing.ExitCode);
    }

    [Fact]
    public async Task Repeatable_flags_build_arrays()
    {
        var adapter = CreateAdapter(new Operations());

        var repeated = await adapter.ExecuteAsync(["join", "--include", "A", "--include", "B"], TestContext.Current.CancellationToken);
        var json = await adapter.ExecuteAsync(["join", "--include", "[\"C\",\"D\"]"], TestContext.Current.CancellationToken);

        Assert.Equal("A,B", repeated.Output);
        Assert.Equal("C,D", json.Output);
    }

    [Fact]
    public async Task Global_options_inject_only_into_operations_that_declare_the_parameter()
    {
        var options = new OperationCommandLineOptions { GlobalOptions = [new CommandLineGlobalOption("--connection", InjectAs: "connectionName")] };
        var adapter = CreateAdapter(new Operations(), options: options);

        var declared = await adapter.ExecuteAsync(["--connection", "db1", "connect"], TestContext.Current.CancellationToken);
        var undeclared = await adapter.ExecuteAsync(["--connection", "db1", "echo", "--value", "x"], TestContext.Current.CancellationToken);
        var afterOperation = await adapter.ExecuteAsync(["connect", "--connection=db2"], TestContext.Current.CancellationToken);
        var explicitWins = await adapter.ExecuteAsync(["--connection", "db1", "connect", "--connection-name", "db3"], TestContext.Current.CancellationToken);

        Assert.Equal("db1", declared.Output);
        Assert.Equal(0, undeclared.ExitCode);
        Assert.Equal("x", undeclared.Output);
        Assert.Equal("db2", afterOperation.Output);
        Assert.Equal("db3", explicitWins.Output);
    }

    [Fact]
    public async Task Operation_parameters_win_over_adapter_flags_after_the_operation_name()
    {
        var adapter = CreateAdapter(new Operations(), new JsonAgentOutputRenderer());

        var parameter = await adapter.ExecuteAsync(["export", "--output", "folder", "--fields", "a,b"], TestContext.Current.CancellationToken);
        var global = await adapter.ExecuteAsync(["--output", "json", "export", "--output", "folder", "--fields", "a,b"], TestContext.Current.CancellationToken);
        var alias = await adapter.ExecuteAsync(["--format", "json", "echo", "--value", "v"], TestContext.Current.CancellationToken);

        Assert.Equal("folder|a,b", parameter.Output);
        Assert.Equal("folder|a,b", global.Output);
        Assert.Equal("v", alias.Output);
    }

    [Fact]
    public async Task Unknown_flag_before_operation_is_a_usage_error()
    {
        var result = await CreateAdapter(new Operations()).ExecuteAsync(["--bogus", "echo"], TestContext.Current.CancellationToken);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Unknown flag '--bogus'", result.Error);
    }

    [Fact]
    public void Configuration_values_are_read_from_global_options()
    {
        var values = OperationCommandLineAdapter.ReadConfigurationValues(
            ["--assembly=C:\\a.dll", "echo", "--value", "x", "--other", "y"],
            [new CommandLineGlobalOption("--assembly", ConfigKey: "TargetAssemblyPath"), new CommandLineGlobalOption("--other")]);

        Assert.Equal("C:\\a.dll", values["TargetAssemblyPath"]);
        Assert.Single(values);
    }

    [Fact]
    public async Task Hides_operations_that_are_not_exposed_on_the_cli()
    {
        var adapter = CreateAdapter(new Operations());

        var root = await adapter.ExecuteAsync([], TestContext.Current.CancellationToken);
        var call = await adapter.ExecuteAsync(["mcp-only"], TestContext.Current.CancellationToken);

        Assert.DoesNotContain("mcp-only", root.Output);
        Assert.Equal(2, call.ExitCode);
    }

    [Fact]
    public async Task Help_shows_descriptions_defaults_required_markers_and_examples()
    {
        var help = await CreateAdapter(new Operations()).ExecuteAsync(["sum", "--help"], TestContext.Current.CancellationToken);

        Assert.Contains("Usage: sum [--numbers <int[]>]", help.Output);
        Assert.Contains("(optional, repeatable) - Numbers to add.", help.Output);
        Assert.Contains("--offset <int> (optional, default: 1)", help.Output);
        Assert.Contains("Examples:", help.Output);
        Assert.Contains("  sum --numbers 1 --numbers 2", help.Output);
        Assert.Contains("--format", help.Output);
    }

    private static OperationCommandLineAdapter CreateAdapter(
        Operations operations,
        IAgentOutputRenderer? renderer = null,
        OperationCommandLineOptions? options = null)
    {
        var catalog = OperationCatalog.Discover(typeof(Operations));
        return new OperationCommandLineAdapter(
            catalog,
            new OperationInvoker(new SingleServiceProvider(operations)),
            renderer ?? new ToonAgentOutputRenderer(),
            options: options);
    }

    private enum Mode
    {
        Alpha,
        Beta,
    }

    private sealed record LongObject(string Text);

    private sealed class Operations
    {
        public string Text { get; init; } = string.Empty;

        [AgentOperation("text", "Returns text")]
        public string GetText() => Text;

        [AgentOperation("long-object", "Returns a long object")]
        public LongObject GetLongObject() => new(new string('y', 1500));

        [AgentOperation("echo", "Echoes")]
        public string Echo(string value) => value;

        [AgentOperation("fail-plain", "Fails")]
        public string FailPlain() => throw new InvalidOperationException("first\nsecond");

        [AgentOperation("describe", "Describes")]
        public string Describe(Guid id, Mode mode = Mode.Alpha, bool includeAllBuilds = false, int count = 0) =>
            $"{id}|{mode}|{includeAllBuilds}|{count}";

        [AgentOperation("sum", "Sums", Examples = ["--numbers 1 --numbers 2"])]
        public int Sum([Description("Numbers to add.")] int[]? numbers = null, int offset = 1) => (numbers ?? []).Sum();

        [AgentOperation("run_query", "Runs a query")]
        public string RunQuery(string contextName) => contextName;

        [AgentOperation("join", "Joins")]
        public string Join(string[] include) => string.Join(",", include);

        [AgentOperation("connect", "Connects")]
        public string Connect(string connectionName) => connectionName;

        [AgentOperation("export", "Exports")]
        public string Export(string output, string fields) => $"{output}|{fields}";

        [AgentOperation("mcp-only", "Hidden from the CLI", Surfaces = AgentSurfaces.Mcp)]
        public string McpOnly() => "x";
    }

    private sealed class SingleServiceProvider(object service) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
    }
}
