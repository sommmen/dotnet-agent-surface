using System.Text.Json;

namespace DotNetAgentSurface.Core.Tests;

public sealed class OperationCatalogBuilderTests
{
    [Fact]
    public async Task Add_registers_a_delegate_based_operation_invokable_end_to_end()
    {
        var catalog = new OperationCatalogBuilder()
            .Add("double", "Doubles a number", Double)
            .Build();

        var operation = Assert.Single(catalog.Operations);
        Assert.Equal("double", operation.Name);
        Assert.True(operation.Method.IsStatic);

        var invoker = new OperationInvoker(new SingleServiceProvider(new object()));
        var inputs = new Dictionary<string, JsonElement> { ["value"] = JsonDocument.Parse("21").RootElement.Clone() };

        var result = await invoker.InvokeAsync(operation, inputs);

        Assert.True(result.Succeeded);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Add_configures_category_safety_level_examples_and_aliases()
    {
        var catalog = new OperationCatalogBuilder()
            .Add("double", "Doubles a number", Double, options =>
            {
                options.Category = "math";
                options.SafetyLevel = AgentSafetyLevel.Confirm;
                options.Examples.Add("double --value 5");
                options.Aliases.Add("dbl");
                options.IsIdempotent = true;
            })
            .Build();

        var operation = Assert.Single(catalog.Operations);
        Assert.Equal("math", operation.Category);
        Assert.Equal(AgentSafetyLevel.Confirm, operation.SafetyLevel);
        Assert.Equal(["double --value 5"], operation.Examples);
        Assert.Equal(["dbl"], operation.Aliases);
        Assert.True(operation.IsIdempotent);
    }

    [Fact]
    public void Add_defaults_IsIdempotent_to_false()
    {
        var catalog = new OperationCatalogBuilder()
            .Add("double", "Doubles a number", Double)
            .Build();

        var operation = Assert.Single(catalog.Operations);
        Assert.False(operation.IsIdempotent);
    }

    [Fact]
    public void AddFromType_produces_equivalent_results_to_Discover()
    {
        var discovered = OperationCatalog.Discover(typeof(SampleOperations));
        var built = new OperationCatalogBuilder().AddFromType(typeof(SampleOperations)).Build();
        var builtGeneric = new OperationCatalogBuilder().AddFromType<SampleOperations>().Build();

        Assert.Equal(discovered.Operations.Select(static operation => operation.Name), built.Operations.Select(static operation => operation.Name));
        Assert.Equal(discovered.Operations.Select(static operation => operation.Name), builtGeneric.Operations.Select(static operation => operation.Name));
        Assert.Equal(discovered.Operations.Select(static operation => operation.Method), built.Operations.Select(static operation => operation.Method));
    }

    [Fact]
    public void Build_combines_type_discovery_and_delegate_registrations()
    {
        var catalog = new OperationCatalogBuilder()
            .AddFromType(typeof(SampleOperations))
            .Add("double", "Doubles a number", Double)
            .Build();

        Assert.Equal(["alpha", "double"], catalog.Operations.Select(static operation => operation.Name));
    }

    [Fact]
    public void Build_rejects_alias_colliding_with_existing_name()
    {
        var builder = new OperationCatalogBuilder()
            .AddFromType(typeof(SampleOperations))
            .Add("clone", "Clones a value", Double, options => options.Aliases.Add("alpha"));

        var exception = Assert.Throws<OperationCatalogException>(() => builder.Build());

        Assert.Contains("collides", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_rejects_names_that_normalize_to_the_same_value_case_insensitively()
    {
        var builder = new OperationCatalogBuilder()
            .Add("double", "Doubles a number", Double)
            .Add("DOUBLE", "Doubles a number, again", Double);

        var exception = Assert.Throws<OperationCatalogException>(() => builder.Build());

        Assert.Contains("duplicated", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_rejects_aliases_that_normalize_to_the_same_value_case_insensitively()
    {
        var builder = new OperationCatalogBuilder()
            .Add("first", "First operation", Double, options => options.Aliases.Add("shared"))
            .Add("second", "Second operation", Double, options => options.Aliases.Add("SHARED"));

        var exception = Assert.Throws<OperationCatalogException>(() => builder.Build());

        Assert.Contains("collides", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("first", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("second", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static int Double(int value) => value * 2;

    private sealed class SampleOperations
    {
        [AgentOperation("alpha", "Alpha operation")]
        public static void Alpha()
        {
        }
    }

    private sealed class SingleServiceProvider(object service) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
    }
}
