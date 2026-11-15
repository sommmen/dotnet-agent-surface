using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace DotNetAgentSurface.Mcp;

/// <summary>One difference found by <see cref="McpToolParity.Compare"/>.</summary>
/// <param name="Tool">The tool name.</param>
/// <param name="Field">What differs: <c>presence</c>, <c>title</c>, <c>description</c>, <c>inputSchema</c>, <c>outputSchema</c> or <c>annotations.*</c>.</param>
/// <param name="Expected">The SDK value (or <see langword="null"/> when absent).</param>
/// <param name="Actual">The adapter value (or <see langword="null"/> when absent).</param>
public sealed record McpToolDifference(string Tool, string Field, string? Expected, string? Actual)
{
    public override string ToString() => $"{Tool}: {Field} differs. Expected (SDK): {Expected ?? "<none>"}; actual (adapter): {Actual ?? "<none>"}";
}

/// <summary>
/// Diffs the <c>tools/list</c> output of an SDK <c>WithTools&lt;T&gt;()</c> server against the same tools served through the
/// operation adapter, as a mechanical migration check. Tools are matched by name; descriptions, titles, input and output schemas
/// (compared structurally, ignoring property order) and every annotation are reported.
/// </summary>
public static class McpToolParity
{
    /// <summary>Compares <paramref name="sdkTools"/> (expected) against <paramref name="adapterTools"/> (actual).</summary>
    /// <returns>The differences; empty when the two lists are equivalent.</returns>
    public static IReadOnlyList<McpToolDifference> Compare(IEnumerable<Tool> sdkTools, IEnumerable<Tool> adapterTools)
    {
        if (sdkTools is null)
        {
            throw new ArgumentNullException(nameof(sdkTools));
        }

        if (adapterTools is null)
        {
            throw new ArgumentNullException(nameof(adapterTools));
        }

        var differences = new List<McpToolDifference>();
        var expected = sdkTools.ToDictionary(static tool => tool.Name, StringComparer.Ordinal);
        var actual = adapterTools.ToDictionary(static tool => tool.Name, StringComparer.Ordinal);

        foreach (var name in expected.Keys.Union(actual.Keys).OrderBy(static name => name, StringComparer.Ordinal))
        {
            if (!expected.TryGetValue(name, out var sdk))
            {
                differences.Add(new McpToolDifference(name, "presence", null, "only in adapter"));
                continue;
            }

            if (!actual.TryGetValue(name, out var adapter))
            {
                differences.Add(new McpToolDifference(name, "presence", "only in SDK", null));
                continue;
            }

            Add(differences, name, "title", sdk.Title, adapter.Title);
            Add(differences, name, "description", sdk.Description, adapter.Description);
            AddSchema(differences, name, "inputSchema", sdk.InputSchema, adapter.InputSchema);
            AddSchema(differences, name, "outputSchema", sdk.OutputSchema, adapter.OutputSchema);
            Add(differences, name, "annotations.title", sdk.Annotations?.Title, adapter.Annotations?.Title);
            Add(differences, name, "annotations.readOnlyHint", sdk.Annotations?.ReadOnlyHint?.ToString(), adapter.Annotations?.ReadOnlyHint?.ToString());
            Add(differences, name, "annotations.destructiveHint", sdk.Annotations?.DestructiveHint?.ToString(), adapter.Annotations?.DestructiveHint?.ToString());
            Add(differences, name, "annotations.idempotentHint", sdk.Annotations?.IdempotentHint?.ToString(), adapter.Annotations?.IdempotentHint?.ToString());
            Add(differences, name, "annotations.openWorldHint", sdk.Annotations?.OpenWorldHint?.ToString(), adapter.Annotations?.OpenWorldHint?.ToString());
        }

        return differences;
    }

    private static void Add(List<McpToolDifference> differences, string tool, string field, string? expected, string? actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            differences.Add(new McpToolDifference(tool, field, expected, actual));
        }
    }

    private static void AddSchema(List<McpToolDifference> differences, string tool, string field, JsonElement? expected, JsonElement? actual)
    {
        var left = expected is { ValueKind: not JsonValueKind.Undefined } e ? Canonicalize(JsonNode.Parse(e.GetRawText())) : null;
        var right = actual is { ValueKind: not JsonValueKind.Undefined } a ? Canonicalize(JsonNode.Parse(a.GetRawText())) : null;
        Add(differences, tool, field, left, right);
    }

    // Sorts object members so semantically equal schemas compare equal; array order is significant and kept.
    private static string? Canonicalize(JsonNode? node) => node is null ? null : Sort(node)!.ToJsonString();

    private static JsonNode? Sort(JsonNode? node) => node switch
    {
        JsonObject obj => SortObject(obj),
        JsonArray array => new JsonArray(array.Select(static item => Sort(item)).ToArray()),
        null => null,
        _ => node.DeepClone(),
    };

    private static JsonObject SortObject(JsonObject obj)
    {
        var sorted = new JsonObject();
        foreach (var pair in obj.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            sorted[pair.Key] = Sort(pair.Value);
        }

        return sorted;
    }
}