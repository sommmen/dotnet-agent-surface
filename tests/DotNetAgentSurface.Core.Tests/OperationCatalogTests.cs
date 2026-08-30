namespace DotNetAgentSurface.Core.Tests;

public sealed class OperationCatalogTests
{
    [Fact]
    public void Discover_returns_operations_ordered_by_name()
    {
        var catalog = OperationCatalog.Discover(typeof(OrderedOperations));

        Assert.Equal(["alpha", "zeta"], catalog.Operations.Select(operation => operation.Name));
        Assert.Equal(typeof(OrderedOperations), catalog.Operations[0].ServiceType);
        Assert.Single(catalog.Operations[0].Parameters);
        Assert.True(catalog.Operations[0].Parameters[0].IsOptional);
        Assert.Equal("default", catalog.Operations[0].Parameters[0].DefaultValue);
    }

    [Fact]
    public void Discover_ignores_unannotated_methods()
    {
        var catalog = OperationCatalog.Discover(typeof(OrderedOperations));

        Assert.DoesNotContain(catalog.Operations, operation => operation.Method.Name == nameof(OrderedOperations.InternalOperation));
    }

    [Fact]
    public void Discover_rejects_duplicate_names_case_insensitively()
    {
        var exception = Assert.Throws<OperationCatalogException>(() => OperationCatalog.Discover(typeof(DuplicateOperations)));

        Assert.Contains("duplicated", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Discover_rejects_ref_parameters()
    {
        var exception = Assert.Throws<OperationCatalogException>(() => OperationCatalog.Discover(typeof(InvalidOperations)));

        Assert.Contains("ref or out", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class OrderedOperations
    {
        [AgentOperation("zeta", "Zeta operation")]
        public void Zeta()
        {
        }

        [AgentOperation("alpha", "Alpha operation", Category = "test", Examples = ["alpha --value hello"])]
        public void Alpha(string value = "default")
        {
        }

        public void InternalOperation()
        {
        }
    }

    private sealed class DuplicateOperations
    {
        [AgentOperation("duplicate", "First operation")]
        public static void First()
        {
        }

        [AgentOperation("DUPLICATE", "Second operation")]
        public static void Second()
        {
        }
    }

    private sealed class InvalidOperations
    {
        [AgentOperation("invalid", "Invalid operation")]
        public void Invalid(ref string value)
        {
        }
    }
}
