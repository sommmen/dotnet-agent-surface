using System.Text.Json;
using System.Text.Json.Nodes;
using ToonFormat;

namespace DotNetAgentSurface.CommandLine;

/// <summary>Renders normalized output using the Token-Oriented Object Notation (TOON) format.</summary>
/// <remarks>
/// Backed by <see href="https://github.com/CharlesHunt/ToonDotNet">Toon.DotNet</see>, which targets
/// <c>net8.0</c>, <c>net9.0</c>, <c>net10.0</c>, <c>net11.0</c>, and <c>netstandard2.0</c>, so the same
/// implementation is used on both of this package's target frameworks.
/// </remarks>
public sealed class ToonAgentOutputRenderer : IAgentOutputRenderer
{
    public string Render(JsonNode? normalizedValue)
    {
        using var document = JsonDocument.Parse(normalizedValue?.ToJsonString() ?? "null");
        return Toon.Encode(document.RootElement);
    }
}
