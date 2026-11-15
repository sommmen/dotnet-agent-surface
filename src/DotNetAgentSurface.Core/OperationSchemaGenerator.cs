using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace DotNetAgentSurface.Core;

/// <summary>
/// Generates the JSON Schema for an operation's inputs with <see cref="AIJsonUtilities"/>, the same generator the
/// ModelContextProtocol SDK uses for <c>McpServerTool</c>, so schemas match it for the same method and
/// <see cref="JsonSerializerOptions"/>. Ambient (host-injected) parameters are excluded.
/// </summary>
public sealed class OperationSchemaGenerator
{
    private readonly JsonSerializerOptions _serializerOptions;

    /// <param name="serializerOptions">
    /// The options that drive property naming, converters and ignore rules. They should be the options the invoker uses
    /// to bind inputs. Defaults to <see cref="JsonSerializerDefaults.Web"/>.
    /// </param>
    public OperationSchemaGenerator(JsonSerializerOptions? serializerOptions = null)
    {
        _serializerOptions = serializerOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
        _serializerOptions.MakeReadOnly(populateMissingResolver: true);
    }

    public JsonDocument GenerateInputSchema(OperationDescriptor operation)
    {
        Guard.ThrowIfNull(operation);

        var ambient = new HashSet<string>(
            operation.Parameters.Where(static parameter => parameter.IsAmbient).Select(static parameter => parameter.Name),
            StringComparer.Ordinal);
        var inference = new AIJsonSchemaCreateOptions
        {
            IncludeParameter = parameter => parameter.Name is null || !ambient.Contains(parameter.Name),
        };

        var schema = AIJsonUtilities.CreateFunctionJsonSchema(
            operation.Method,
            title: string.Empty,
            description: string.Empty,
            serializerOptions: _serializerOptions,
            inferenceOptions: inference);

        if (JsonNode.Parse(schema.GetRawText()) is JsonObject root
            && root["properties"] is JsonObject properties)
        {
            foreach (var parameter in operation.Parameters)
            {
                if (!string.IsNullOrWhiteSpace(parameter.Description)
                    && properties[parameter.Name] is JsonObject property
                    && !property.ContainsKey("description"))
                {
                    property["description"] = parameter.Description;
                }
            }

            return JsonDocument.Parse(root.ToJsonString());
        }

        return JsonDocument.Parse(schema.GetRawText());
    }
}
