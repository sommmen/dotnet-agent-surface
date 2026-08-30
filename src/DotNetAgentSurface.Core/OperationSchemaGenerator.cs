using System.Text.Json;

namespace DotNetAgentSurface.Core;

public sealed class OperationSchemaGenerator
{
    public JsonDocument GenerateInputSchema(OperationDescriptor operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var properties = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        var required = new List<string>();

        foreach (var parameter in operation.Parameters.Where(static parameter => !parameter.IsCancellationToken))
        {
            properties.Add(parameter.Name, CreateSchema(parameter.ParameterType, parameter.IsNullable));
            if (!parameter.IsOptional && !parameter.IsNullable)
            {
                required.Add(parameter.Name);
            }
        }

        return JsonSerializer.SerializeToDocument(new
        {
            type = "object",
            properties,
            required = required.OrderBy(static name => name, StringComparer.Ordinal).ToArray(),
            additionalProperties = false
        });
    }

    private static object CreateSchema(Type type, bool isNullable = false)
    {
        var nullableType = Nullable.GetUnderlyingType(type);
        var effectiveType = nullableType ?? type;
        var schema = effectiveType switch
        {
            var value when value == typeof(string) || value == typeof(char) => new Dictionary<string, object?> { ["type"] = "string" },
            var value when value == typeof(bool) => new Dictionary<string, object?> { ["type"] = "boolean" },
            var value when value == typeof(byte) || value == typeof(short) || value == typeof(int) || value == typeof(long) || value == typeof(sbyte) || value == typeof(ushort) || value == typeof(uint) || value == typeof(ulong) => new Dictionary<string, object?> { ["type"] = "integer" },
            var value when value == typeof(float) || value == typeof(double) || value == typeof(decimal) => new Dictionary<string, object?> { ["type"] = "number" },
            var value when value.IsEnum => new Dictionary<string, object?> { ["type"] = "string", ["enum"] = Enum.GetNames(value) },
            var value when value.IsArray => new Dictionary<string, object?> { ["type"] = "array", ["items"] = CreateSchema(value.GetElementType()!) },
            var value when value.IsGenericType && value.GetGenericTypeDefinition() == typeof(IEnumerable<>) => new Dictionary<string, object?> { ["type"] = "array", ["items"] = CreateSchema(value.GetGenericArguments()[0]) },
            _ => new Dictionary<string, object?> { ["type"] = "object" }
        };

        if (nullableType is not null || isNullable)
        {
            ((Dictionary<string, object?>)schema)["nullable"] = true;
        }

        return schema;
    }
}
