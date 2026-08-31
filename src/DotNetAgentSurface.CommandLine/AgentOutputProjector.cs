using System.Text.Json;
using System.Text.Json.Nodes;

namespace DotNetAgentSurface.CommandLine;

/// <summary>Applies AXI-oriented output shaping before a result is handed to a format renderer.</summary>
public static class AgentOutputProjector
{
    /// <summary>Default maximum size of a string field value in agent-oriented output.</summary>
    public const int DefaultStringLengthLimit = 1_000;

    private const int DefaultSummaryFieldCount = 4;

    public static JsonNode? Project(
        object? value,
        IReadOnlyCollection<string>? requestedFields,
        bool includeFullValues,
        int stringLengthLimit = DefaultStringLengthLimit)
    {
        if (stringLengthLimit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(stringLengthLimit));
        }

        var normalized = JsonSerializer.SerializeToNode(value);
        var wasTruncated = false;
        normalized = ProjectNode(normalized, requestedFields, includeFullValues, stringLengthLimit, ref wasTruncated);

        if (normalized is JsonArray array)
        {
            var result = new JsonObject
            {
                ["count"] = array.Count,
                ["items"] = array,
            };

            if (array.Count == 0)
            {
                result["message"] = "No results.";
            }

            if (wasTruncated)
            {
                result["truncated"] = "Some string values were truncated. Use --full to include complete values.";
            }

            return result;
        }

        if (wasTruncated && normalized is JsonObject objectResult)
        {
            objectResult["truncated"] = "Some string values were truncated. Use --full to include complete values.";
        }

        return normalized;
    }

    private static JsonNode? ProjectNode(
        JsonNode? node,
        IReadOnlyCollection<string>? requestedFields,
        bool includeFullValues,
        int stringLengthLimit,
        ref bool wasTruncated)
    {
        if (node is JsonArray array)
        {
            var fields = requestedFields is { Count: > 0 }
                ? ResolveRequestedFields(array, requestedFields)
                : GetDefaultFields(array);
            var projected = new JsonArray();
            foreach (var item in array)
            {
                if (item is JsonObject itemObject && fields.Count > 0)
                {
                    var projectedItem = new JsonObject();
                    foreach (var field in fields)
                    {
                        if (itemObject.TryGetPropertyValue(field, out var fieldValue))
                        {
                            projectedItem[field] = ProjectNode(fieldValue, null, includeFullValues, stringLengthLimit, ref wasTruncated);
                        }
                    }

                    projected.Add(projectedItem);
                }
                else
                {
                    projected.Add(ProjectNode(item, null, includeFullValues, stringLengthLimit, ref wasTruncated));
                }
            }

            return projected;
        }

        if (node is JsonObject objectNode)
        {
            var projected = new JsonObject();
            foreach (var property in objectNode)
            {
                projected[property.Key] = ProjectNode(property.Value, null, includeFullValues, stringLengthLimit, ref wasTruncated);
            }

            return projected;
        }

        if (!includeFullValues && node is JsonValue valueNode && valueNode.TryGetValue<string>(out var stringValue) && stringValue.Length > stringLengthLimit)
        {
            wasTruncated = true;
            return $"{stringValue[..stringLengthLimit]}...[truncated, showing {stringLengthLimit} of {stringValue.Length} chars]";
        }

        return node?.DeepClone();
    }

    private static IReadOnlyCollection<string> GetDefaultFields(JsonArray array)
    {
        foreach (var item in array)
        {
            if (item is not JsonObject itemObject)
            {
                continue;
            }

            return itemObject
                .Where(static property => IsScalar(property.Value))
                .OrderBy(static property => IsIdentifyingField(property.Key) ? 0 : 1)
                .ThenBy(static property => property.Key, StringComparer.Ordinal)
                .Take(DefaultSummaryFieldCount)
                .Select(static property => property.Key)
                .ToArray();
        }

        return [];
    }

    /// <summary>
    /// Validates <c>--fields</c> against the actual result shape, failing clearly (rather than silently ignoring
    /// unknown names) for both unknown field names and results that are not lists of objects.
    /// </summary>
    private static IReadOnlyCollection<string> ResolveRequestedFields(JsonArray array, IReadOnlyCollection<string> requestedFields)
    {
        if (array.Count == 0)
        {
            // Nothing to validate field names against; honor the request trivially since there are no items.
            return requestedFields;
        }

        if (array.Any(static item => item is not JsonObject))
        {
            throw new CommandLineUsageException("The --fields flag only applies to list results made up of objects.");
        }

        var available = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in array.OfType<JsonObject>())
        {
            foreach (var property in item)
            {
                if (!available.ContainsKey(property.Key))
                {
                    available.Add(property.Key, property.Key);
                }
            }
        }

        var unknown = requestedFields.Where(field => !available.ContainsKey(field)).ToArray();
        if (unknown.Length > 0)
        {
            throw new CommandLineUsageException($"Unknown output field(s): {string.Join(", ", unknown)}. Available fields: {string.Join(", ", available.Values.OrderBy(static field => field, StringComparer.Ordinal))}.");
        }

        // Return the canonical (actual) property casing so downstream lookups against JsonObject succeed even
        // when the caller supplied different casing on --fields.
        return requestedFields.Select(field => available[field]).ToArray();
    }

    private static bool IsScalar(JsonNode? value) => value is null or JsonValue;

    private static bool IsIdentifyingField(string field) =>
        field.Equals("id", StringComparison.OrdinalIgnoreCase) ||
        field.EndsWith("id", StringComparison.OrdinalIgnoreCase) ||
        field.Equals("name", StringComparison.OrdinalIgnoreCase) ||
        field.Equals("title", StringComparison.OrdinalIgnoreCase);
}
