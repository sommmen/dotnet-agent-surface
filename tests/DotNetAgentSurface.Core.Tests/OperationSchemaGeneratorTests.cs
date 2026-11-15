using System.Text.Json;

namespace DotNetAgentSurface.Core.Tests;

public sealed class OperationSchemaGeneratorTests
{
    private static JsonElement Generate(Type type) =>
        new OperationSchemaGenerator().GenerateInputSchema(OperationCatalog.Discover(type).Operations.Single()).RootElement.Clone();

    [Fact]
    public void GenerateInputSchema_is_stable_and_excludes_ambient_parameters()
    {
        var schema = Generate(typeof(SchemaOperations));
        var properties = schema.GetProperty("properties");

        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal("integer", properties.GetProperty("count").GetProperty("type").GetString());
        Assert.Equal("string", properties.GetProperty("title").GetProperty("type").GetString());
        Assert.Equal("array", properties.GetProperty("tags").GetProperty("type").GetString());
        Assert.Equal("object", properties.GetProperty("settings").GetProperty("type").GetString());
        Assert.False(properties.TryGetProperty("cancellationToken", out _));
        Assert.False(properties.TryGetProperty("operationContext", out _));
        Assert.False(properties.TryGetProperty("progress", out _));
        Assert.False(properties.TryGetProperty("services", out _));
    }

    [Fact]
    public void GenerateInputSchema_uses_json_schema_nullability_and_web_property_names()
    {
        var properties = Generate(typeof(SchemaOperations)).GetProperty("properties");

        Assert.Equal(["string", "null"], properties.GetProperty("name").GetProperty("type").EnumerateArray().Select(item => item.GetString()));
        var settings = properties.GetProperty("settings").GetProperty("properties");
        Assert.True(settings.TryGetProperty("label", out _));
        Assert.Equal(["string", "null"], settings.GetProperty("notes").GetProperty("type").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void GenerateInputSchema_covers_collections_dictionaries_json_elements_and_defaults()
    {
        var properties = Generate(typeof(WideOperations)).GetProperty("properties");

        Assert.Equal("array", properties.GetProperty("list").GetProperty("type").GetString());
        Assert.Equal("array", properties.GetProperty("readOnlyList").GetProperty("type").GetString());
        Assert.Equal("array", properties.GetProperty("set").GetProperty("type").GetString());
        var map = properties.GetProperty("map");
        Assert.Equal("object", map.GetProperty("type").GetString());
        Assert.Equal("string", properties.GetProperty("duration").GetProperty("type").GetString());
        Assert.Equal(5, properties.GetProperty("limit").GetProperty("default").GetInt32());
    }

    [Fact]
    public void GenerateInputSchema_prefers_parameter_description_attribute()
    {
        var properties = Generate(typeof(WideOperations)).GetProperty("properties");

        Assert.Equal("The limit", properties.GetProperty("limit").GetProperty("description").GetString());
    }

    [Fact]
    public void Parameter_IsRequired_matches_the_schema_required_list()
    {
        var operation = OperationCatalog.Discover(typeof(RequiredOperations)).Operations.Single();
        var schema = Generate(typeof(RequiredOperations));
        var required = schema.GetProperty("required").EnumerateArray().Select(static item => item.GetString()).Order().ToArray();

        Assert.Equal(required, operation.Parameters.Where(static parameter => parameter.IsRequired).Select(static parameter => parameter.Name).Order().ToArray());
        Assert.Equal(["a", "b", "c", "d"], required);
    }

    private sealed class RequiredOperations
    {
        [AgentOperation("required", "Required parameters")]
        public void Required(string a, string? b, int c, int? d, int e = 1, string? f = null, string g = "x", CancellationToken cancellationToken = default)
        {
        }
    }

    private sealed class SchemaOperations
    {
        [AgentOperation("schema", "Creates a schema")]
        public void Schema(
            int count,
            string title,
            string? name,
            IEnumerable<string> tags,
            SchemaSettings settings,
            IOperationContext operationContext,
            IOperationProgress progress,
            IServiceProvider services,
            CancellationToken cancellationToken)
        {
        }
    }

    private sealed class WideOperations
    {
        [AgentOperation("wide", "Wide types")]
        public void Wide(
            List<string> list,
            IReadOnlyList<int> readOnlyList,
            HashSet<string> set,
            Dictionary<string, JsonElement> map,
            TimeSpan duration,
            [System.ComponentModel.Description("The limit")] int limit = 5)
        {
        }
    }

    private sealed record SchemaSettings(string Label, string? Notes);
}
