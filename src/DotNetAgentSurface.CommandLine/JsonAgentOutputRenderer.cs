using System.Text.Json;
using System.Text.Json.Nodes;

namespace DotNetAgentSurface.CommandLine;

/// <summary>Renders normalized output as compact JSON.</summary>
public sealed class JsonAgentOutputRenderer : IAgentOutputRenderer
{
    public string Render(JsonNode? normalizedValue) => normalizedValue?.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) ?? "null";
}
