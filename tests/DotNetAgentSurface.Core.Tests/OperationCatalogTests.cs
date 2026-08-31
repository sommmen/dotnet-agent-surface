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

    [Fact]
    public void Discover_exposes_aliases_declared_on_the_attribute()
    {
        var catalog = OperationCatalog.Discover(typeof(AliasedOperations));

        var operation = catalog.Operations.Single();
        Assert.Equal(["fetch", "get"], operation.Aliases);
    }

    [Fact]
    public void Discover_rejects_empty_alias()
    {
        var exception = Assert.Throws<OperationCatalogException>(() => OperationCatalog.Discover(typeof(EmptyAliasOperations)));

        Assert.Contains("empty alias", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Discover_rejects_alias_matching_own_name()
    {
        var exception = Assert.Throws<OperationCatalogException>(() => OperationCatalog.Discover(typeof(SelfAliasOperations)));

        Assert.Contains("cannot alias its own name", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Discover_rejects_alias_colliding_with_another_operations_name()
    {
        var exception = Assert.Throws<OperationCatalogException>(() => OperationCatalog.Discover(typeof(AliasCollidesWithNameOperations)));

        Assert.Contains("collides", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Discover_rejects_aliases_colliding_case_insensitively_across_operations()
    {
        var exception = Assert.Throws<OperationCatalogException>(() => OperationCatalog.Discover(typeof(AliasCollidesWithAliasOperations)));

        Assert.Contains("collides", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("first", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("second", exception.Message, StringComparison.OrdinalIgnoreCase);
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

    private sealed class AliasedOperations
    {
        [AgentOperation("retrieve", "Retrieves a value", Aliases = ["fetch", "get"])]
        public static void Retrieve()
        {
        }
    }

    private sealed class EmptyAliasOperations
    {
        [AgentOperation("retrieve", "Retrieves a value", Aliases = ["  "])]
        public static void Retrieve()
        {
        }
    }

    private sealed class SelfAliasOperations
    {
        [AgentOperation("retrieve", "Retrieves a value", Aliases = ["RETRIEVE"])]
        public static void Retrieve()
        {
        }
    }

    private sealed class AliasCollidesWithNameOperations
    {
        [AgentOperation("retrieve", "Retrieves a value", Aliases = ["store"])]
        public static void Retrieve()
        {
        }

        [AgentOperation("store", "Stores a value")]
        public static void Store()
        {
        }
    }

    private sealed class AliasCollidesWithAliasOperations
    {
        [AgentOperation("first", "First operation", Aliases = ["shared"])]
        public static void First()
        {
        }

        [AgentOperation("second", "Second operation", Aliases = ["SHARED"])]
        public static void Second()
        {
        }
    }
}
