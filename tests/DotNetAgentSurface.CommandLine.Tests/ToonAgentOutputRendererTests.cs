using System.Text.Json.Nodes;

namespace DotNetAgentSurface.CommandLine.Tests;

public sealed class ToonAgentOutputRendererTests
{
    [Fact]
    public void Renders_empty_object_root_as_braces()
    {
        var renderer = new ToonAgentOutputRenderer();

        var result = renderer.Render(new JsonObject());

        Assert.Equal("{}", result);
    }

    [Fact]
    public void Renders_empty_array_root_as_brackets()
    {
        var renderer = new ToonAgentOutputRenderer();

        var result = renderer.Render(new JsonArray());

        Assert.Equal("[]", result);
    }

    [Fact]
    public void Renders_null_root_as_literal_null()
    {
        var renderer = new ToonAgentOutputRenderer();

        var result = renderer.Render(null);

        Assert.Equal("null", result);
    }

    [Fact]
    public void Renders_non_empty_object_using_toon_encoding()
    {
        var renderer = new ToonAgentOutputRenderer();

        var result = renderer.Render(new JsonObject { ["a"] = 1 });

        Assert.Equal("a: 1", result);
    }
}
