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
        var root = document.RootElement;

        // Toon.Encode has no explicit representation for empty root containers: an empty object encodes to an
        // empty string (indistinguishable from no output at all) and an empty array encodes to "[0]:" without
        // conveying that it is an array. Special-case both so a successful operation always produces visible,
        // unambiguous stdout, matching the JSON renderer and the previous hand-rolled TOON writer.
        if (root.ValueKind == JsonValueKind.Object && !root.EnumerateObject().MoveNext())
        {
            return "{}";
        }

        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() == 0)
        {
            return "[]";
        }

        return Toon.Encode(root);
    }
}
