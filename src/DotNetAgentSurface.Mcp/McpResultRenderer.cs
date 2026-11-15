using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

namespace DotNetAgentSurface.Mcp;

/// <summary>Turns the value an operation returned into an MCP <see cref="CallToolResult"/>.</summary>
public interface IMcpResultRenderer
{
    CallToolResult Render(Core.OperationDescriptor operation, object? value);
}

/// <summary>
/// The built-in rendering rules:
/// <list type="bullet">
/// <item><description><see langword="null"/> becomes an empty result;</description></item>
/// <item><description>a <see cref="string"/> passes through verbatim as text (no JSON quoting);</description></item>
/// <item><description>a <see cref="CallToolResult"/>, <see cref="ContentBlock"/> or <see cref="IEnumerable{T}"/> of <see cref="ContentBlock"/> passes through untouched;</description></item>
/// <item><description>anything else is serialized as JSON text with the configured <see cref="JsonSerializerOptions"/> (Web defaults);</description></item>
/// <item><description>when structured content is enabled for the operation and it declares an output schema, the same JSON is also sent as <see cref="CallToolResult.StructuredContent"/>.</description></item>
/// </list>
/// Wrap or derive from it in a custom <see cref="IMcpResultRenderer"/> that only changes some results.
/// </summary>
public class DefaultMcpResultRenderer : IMcpResultRenderer
{
    private readonly JsonSerializerOptions _options;
    private readonly Func<Core.OperationDescriptor, bool> _useStructuredContent;

    public DefaultMcpResultRenderer(JsonSerializerOptions? options = null, Func<Core.OperationDescriptor, bool>? useStructuredContent = null)
    {
        _options = options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
        _useStructuredContent = useStructuredContent ?? (static _ => false);
    }

    public virtual CallToolResult Render(Core.OperationDescriptor operation, object? value)
    {
        switch (value)
        {
            case null:
            case JsonElement { ValueKind: JsonValueKind.Undefined }:
                return new CallToolResult { Content = [] };
            case CallToolResult callToolResult:
                return callToolResult;
            case string text:
                return new CallToolResult { Content = [new TextContentBlock { Text = text }] };
            case ContentBlock block:
                return new CallToolResult { Content = [block] };
            case IEnumerable<ContentBlock> blocks:
                return new CallToolResult { Content = [.. blocks] };
        }

        var element = value switch
        {
            JsonElement json => json,
            JsonNode node => JsonSerializer.SerializeToElement(node, _options),
            _ => JsonSerializer.SerializeToElement(value, value.GetType(), _options),
        };

        var result = new CallToolResult { Content = [new TextContentBlock { Text = element.GetRawText() }] };
        if (_useStructuredContent(operation) && element.ValueKind == JsonValueKind.Object && McpOutputSchema.TryCreate(operation, _options) is not null)
        {
            result.StructuredContent = element;
        }

        return result;
    }
}

internal static class McpOutputSchema
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<JsonSerializerOptions, System.Collections.Concurrent.ConcurrentDictionary<Core.OperationDescriptor, JsonElement?>> Cache = new();

    public static JsonElement? TryCreate(Core.OperationDescriptor operation, JsonSerializerOptions options)
        => Cache.GetValue(options, static _ => new()).GetOrAdd(operation, op => Create(op, options));

    private static JsonElement? Create(Core.OperationDescriptor operation, JsonSerializerOptions options)
    {
        var type = UnwrapReturnType(operation.DeclaredReturnType);
        if (type is null
            || type == typeof(string)
            || typeof(ContentBlock).IsAssignableFrom(type)
            || typeof(CallToolResult).IsAssignableFrom(type)
            || typeof(IEnumerable<ContentBlock>).IsAssignableFrom(type)
            || typeof(AIContent).IsAssignableFrom(type))
        {
            return null;
        }

        var schema = AIJsonUtilities.CreateJsonSchema(type, serializerOptions: options);
        return schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("type", out var kind)
            && kind.ValueKind == JsonValueKind.String
            && kind.GetString() == "object"
                ? schema
                : null;
    }

    private static Type? UnwrapReturnType(Type type)
    {
        if (type == typeof(void) || type == typeof(Task) || type == typeof(ValueTask))
        {
            return null;
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(Task<>) || definition == typeof(ValueTask<>))
            {
                return type.GetGenericArguments()[0];
            }
        }

        return type;
    }
}
