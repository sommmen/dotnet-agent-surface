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

    [Fact]
    public async Task ExecuteAsync_routes_nested_category_operations()
    {
        var adapter = CreateCategoryAdapter();

        var invocation = await adapter.ExecuteAsync(["projects", "archived", "list", "--value", "hello"]);

        Assert.Equal(0, invocation.ExitCode);
        Assert.Equal("\"hello\"", invocation.Output);
    }

    [Fact]
    public async Task ExecuteAsync_still_routes_root_operations_when_categories_exist()
    {
        var adapter = CreateCategoryAdapter();

        var invocation = await adapter.ExecuteAsync(["ping", "--value", "hello"]);

        Assert.Equal(0, invocation.ExitCode);
        Assert.Equal("\"hello\"", invocation.Output);
    }

    [Fact]
    public async Task ExecuteAsync_returns_category_scoped_help_for_partial_category_path()
    {
        var adapter = CreateCategoryAdapter();

        var invocation = await adapter.ExecuteAsync(["customers"]);

        Assert.Equal(0, invocation.ExitCode);
        Assert.Contains("find-customer", invocation.Output);
        Assert.DoesNotContain("Unknown operation", invocation.Output);
    }

    [Fact]
    public async Task ExecuteAsync_returns_category_scoped_help_for_nested_category_via_help_flag()
    {
        var adapter = CreateCategoryAdapter();

        var invocation = await adapter.ExecuteAsync(["projects", "--help"]);

        Assert.Equal(0, invocation.ExitCode);
        Assert.Contains("archived", invocation.Output);
    }

    [Fact]
    public async Task ExecuteAsync_fails_clearly_for_unknown_category()
    {
        var adapter = CreateCategoryAdapter();

        var invocation = await adapter.ExecuteAsync(["bogus-category", "leaf"]);

        Assert.Equal(1, invocation.ExitCode);
        Assert.Contains("Unknown operation 'bogus-category'", invocation.Error);
    }

    [Fact]
    public async Task ExecuteAsync_fails_clearly_for_unknown_operation_within_known_category()
    {
        var adapter = CreateCategoryAdapter();

        var invocation = await adapter.ExecuteAsync(["customers", "bogus-op"]);

        Assert.Equal(1, invocation.ExitCode);
        Assert.Contains("Unknown operation 'bogus-op'", invocation.Error);
    }

    [Fact]
    public async Task ExecuteAsync_routes_same_leaf_name_operations_in_different_categories()
    {
        var adapter = CreateCategoryAdapter();

        var customersList = await adapter.ExecuteAsync(["customers", "list-items", "--value", "customers-list"]);
        var projectsList = await adapter.ExecuteAsync(["projects", "list-items", "--value", "projects-list"]);

        Assert.Equal(0, customersList.ExitCode);
        Assert.Equal("\"customers-list\"", customersList.Output);
        Assert.Equal(0, projectsList.ExitCode);
        Assert.Equal("\"projects-list\"", projectsList.Output);
    }

    [Fact]
    public async Task ExecuteAsync_root_help_lists_categories_and_root_operations()
    {
        var adapter = CreateCategoryAdapter();

        var help = await adapter.ExecuteAsync([]);

        Assert.Equal(0, help.ExitCode);
        Assert.Contains("ping", help.Output);
        Assert.Contains("customers", help.Output);
        Assert.Contains("projects", help.Output);
    }

    private static OperationCommandLineAdapter CreateAdapter()
    {
        var catalog = OperationCatalog.Discover(typeof(CliOperations));
        return new OperationCommandLineAdapter(catalog, new OperationInvoker(new SingleServiceProvider(new CliOperations())));
    }

    private static OperationCommandLineAdapter CreateCategoryAdapter()
    {
        var catalog = OperationCatalog.Discover(typeof(CategoryOperations));
        return new OperationCommandLineAdapter(catalog, new OperationInvoker(new SingleServiceProvider(new CategoryOperations())));
    }

    private sealed class CliOperations
    {
        [AgentOperation("echo", "Returns the supplied value", Aliases = ["say"])]
        public string Echo(string value) => value;
    }

    private sealed class CategoryOperations
    {
        [AgentOperation("ping", "Root-level ping")]
        public string Ping(string value) => value;

        [AgentOperation("find-customer", "Finds a customer", Category = "customers")]
        public string FindCustomer(string value) => value;

        [AgentOperation("list-items", "Lists customer items", Category = "customers")]
        public string ListCustomerItems(string value) => value;

        [AgentOperation("list-items", "Lists project items", Category = "projects")]
        public string ListProjectItems(string value) => value;

        [AgentOperation("list", "Lists archived projects", Category = "projects archived")]
        public string ListArchivedProjects(string value) => value;
    }

    private sealed class SingleServiceProvider(object service) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
    }
}
