using DotNetAgentSurface.CommandLine;

namespace DotNetAgentSurface.Core.Tests;

public sealed class OperationCommandLineAdapterTests
{
    [Fact]
    public async Task ExecuteAsync_renders_help_and_invokes_shared_pipeline()
    {
        var adapter = CreateAdapter();

        var help = await adapter.ExecuteAsync([]);
        var invocation = await adapter.ExecuteAsync(["echo", "--value", "hello"]);

        Assert.Equal(0, help.ExitCode);
        Assert.Contains("echo", help.Output);
        Assert.Equal(0, invocation.ExitCode);
        Assert.Equal("\"hello\"", invocation.Output);
    }

    [Fact]
    public async Task ExecuteAsync_returns_usage_error_for_malformed_input()
    {
        var result = await CreateAdapter().ExecuteAsync(["echo", "--value"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--name JSON-value", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_resolves_operation_by_alias()
    {
        var invocation = await CreateAdapter().ExecuteAsync(["say", "--value", "hello"]);

        Assert.Equal(0, invocation.ExitCode);
        Assert.Equal("\"hello\"", invocation.Output);
    }

    private static OperationCommandLineAdapter CreateAdapter()
    {
        var catalog = OperationCatalog.Discover(typeof(CliOperations));
        return new OperationCommandLineAdapter(catalog, new OperationInvoker(new SingleServiceProvider(new CliOperations())));
    }

    private sealed class CliOperations
    {
        [AgentOperation("echo", "Returns the supplied value", Aliases = ["say"])]
        public string Echo(string value) => value;
    }

    private sealed class SingleServiceProvider(object service) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
    }
}
