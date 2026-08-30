namespace DotNetAgentSurface.Core.Tests;

public sealed class OperationSchemaGeneratorTests
{
    [Fact]
    public void GenerateInputSchema_is_stable_and_excludes_cancellation_tokens()
    {
        var operation = OperationCatalog.Discover(typeof(SchemaOperations)).Operations.Single();
        var schema = new OperationSchemaGenerator().GenerateInputSchema(operation);

        Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
        Assert.Equal("integer", schema.RootElement.GetProperty("properties").GetProperty("count").GetProperty("type").GetString());
        Assert.Equal("string", schema.RootElement.GetProperty("properties").GetProperty("title").GetProperty("type").GetString());
        Assert.Equal("string", schema.RootElement.GetProperty("properties").GetProperty("name").GetProperty("type").GetString());
        Assert.True(schema.RootElement.GetProperty("properties").GetProperty("name").GetProperty("nullable").GetBoolean());
        Assert.Equal("array", schema.RootElement.GetProperty("properties").GetProperty("tags").GetProperty("type").GetString());
        var settings = schema.RootElement.GetProperty("properties").GetProperty("settings");
        Assert.Equal("object", settings.GetProperty("type").GetString());
        Assert.Equal("string", settings.GetProperty("properties").GetProperty("Label").GetProperty("type").GetString());
        Assert.True(settings.GetProperty("properties").GetProperty("Notes").GetProperty("nullable").GetBoolean());
        Assert.Equal("Label", settings.GetProperty("required")[0].GetString());
        Assert.False(schema.RootElement.GetProperty("properties").TryGetProperty("cancellationToken", out _));
        Assert.Collection(
            schema.RootElement.GetProperty("required").EnumerateArray(),
            item => Assert.Equal("count", item.GetString()),
            item => Assert.Equal("settings", item.GetString()),
            item => Assert.Equal("tags", item.GetString()),
            item => Assert.Equal("title", item.GetString()));
    }

    private sealed class SchemaOperations
    {
        [AgentOperation("schema", "Creates a schema")]
        public void Schema(int count, string title, string? name, IEnumerable<string> tags, SchemaSettings settings, CancellationToken cancellationToken)
        {
        }
    }

    private sealed record SchemaSettings(string Label, string? Notes);
}
