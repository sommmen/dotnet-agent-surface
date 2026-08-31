using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
#if NET10_0
using Cysharp.AI;
#endif

namespace DotNetAgentSurface.CommandLine;

/// <summary>Renders normalized output using the Token-Oriented Object Notation (TOON) format.</summary>
/// <remarks>
/// ToonEncoder targets .NET 10 only. The <c>net10.0</c> implementation uses it directly; the retained
/// <c>netstandard2.0</c> implementation below is a deliberately small TOON writer for JSON-compatible values so
/// this multi-targeted package keeps its default output format on both target frameworks.
/// </remarks>
public sealed class ToonAgentOutputRenderer : IAgentOutputRenderer
{
    public string Render(JsonNode? normalizedValue)
    {
#if NET10_0
        using var document = JsonDocument.Parse(normalizedValue?.ToJsonString() ?? "null");
        return ToonEncoder.Encode(document.RootElement);
#else
        var output = new StringBuilder();
        WriteValue(output, normalizedValue, 0, root: true);
        return output.ToString();
#endif
    }

#if !NET10_0
    private static void WriteValue(StringBuilder output, JsonNode? value, int indent, bool root = false)
    {
        switch (value)
        {
            case null:
                output.Append("null");
                break;
            case JsonValue scalar:
                WriteScalar(output, scalar);
                break;
            case JsonArray array:
                WriteArray(output, array, indent, root);
                break;
            case JsonObject objectValue:
                WriteObject(output, objectValue, indent, root);
                break;
            default:
                output.Append("null");
                break;
        }
    }

    private static void WriteObject(StringBuilder output, JsonObject value, int indent, bool root)
    {
        var properties = value.ToArray();
        if (properties.Length == 0)
        {
            output.Append("{}");
            return;
        }

        for (var index = 0; index < properties.Length; index++)
        {
            if (index > 0)
            {
                output.Append('\n');
            }

            AppendIndent(output, indent);
            output.Append(EncodeKey(properties[index].Key)).Append(": ");
            var child = properties[index].Value;
            if (child is JsonObject or JsonArray)
            {
                output.Append('\n');
                WriteValue(output, child, indent + 2);
            }
            else
            {
                WriteValue(output, child, indent + 2);
            }
        }
    }

    private static void WriteArray(StringBuilder output, JsonArray value, int indent, bool root)
    {
        if (value.Count == 0)
        {
            output.Append("[]");
            return;
        }

        if (value.All(static item => item is JsonObject) && HaveSameKeys(value))
        {
            var first = (JsonObject)value[0]!;
            var fields = first.Select(static property => property.Key).ToArray();
            output.Append("items[").Append(value.Count).Append("]{").Append(string.Join(",", fields.Select(EncodeKey))).Append("}:");
            foreach (var item in value.Cast<JsonObject>())
            {
                output.Append('\n');
                AppendIndent(output, indent + 2);
                for (var fieldIndex = 0; fieldIndex < fields.Length; fieldIndex++)
                {
                    if (fieldIndex > 0)
                    {
                        output.Append(',');
                    }

                    WriteCsvValue(output, item[fields[fieldIndex]]);
                }
            }

            return;
        }

        output.Append("items[").Append(value.Count).Append("]:");
        foreach (var item in value)
        {
            output.Append('\n');
            AppendIndent(output, indent + 2).Append("- ");
            WriteValue(output, item, indent + 2);
        }
    }

    private static bool HaveSameKeys(JsonArray array)
    {
        var fields = new HashSet<string>(((JsonObject)array[0]!).Select(static property => property.Key), StringComparer.Ordinal);
        return array.Cast<JsonObject>().All(item => item.Count == fields.Count && item.All(property => fields.Contains(property.Key)));
    }

    private static void WriteCsvValue(StringBuilder output, JsonNode? value)
    {
        if (value is JsonValue scalar)
        {
            var text = ScalarText(scalar);
            output.Append(NeedsQuotes(text, ',') ? Quote(text) : text);
            return;
        }

        output.Append(Quote(value?.ToJsonString() ?? "null"));
    }

    private static void WriteScalar(StringBuilder output, JsonValue value) => output.Append(ScalarText(value));

    private static string ScalarText(JsonValue value)
    {
        if (value.TryGetValue<string>(out var text))
        {
            return NeedsQuotes(text, ':') ? Quote(text) : text;
        }

        if (value.TryGetValue<bool>(out var boolean))
        {
            return boolean ? "true" : "false";
        }

        if (value.TryGetValue<decimal>(out var decimalValue))
        {
            return decimalValue.ToString(CultureInfo.InvariantCulture);
        }

        return value.ToJsonString();
    }

    private static string EncodeKey(string key) => NeedsQuotes(key, ':') ? Quote(key) : key;

    private static bool NeedsQuotes(string value, char delimiter) => string.IsNullOrEmpty(value) || value.Any(character => char.IsWhiteSpace(character) || character is '"' or '\\' or '[' or ']' or '{' or '}' or ':' || character == delimiter);

    private static string Quote(string value) => JsonSerializer.Serialize(value);

    private static StringBuilder AppendIndent(StringBuilder output, int indent) => output.Append(' ', indent);
#endif
}
