using System.Reflection;
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

    private static object CreateSchema(Type type, bool isNullable = false, ISet<Type>? ancestors = null)
    {
        var nullableType = Nullable.GetUnderlyingType(type);
        var effectiveType = nullableType ?? type;
        var schema = effectiveType switch
        {
            var value when value == typeof(string) || value == typeof(char) || value == typeof(Guid) || value == typeof(DateTime) || value == typeof(DateTimeOffset) => new Dictionary<string, object?> { ["type"] = "string" },
            var value when value == typeof(bool) => new Dictionary<string, object?> { ["type"] = "boolean" },
            var value when value == typeof(byte) || value == typeof(short) || value == typeof(int) || value == typeof(long) || value == typeof(sbyte) || value == typeof(ushort) || value == typeof(uint) || value == typeof(ulong) => new Dictionary<string, object?> { ["type"] = "integer" },
            var value when value == typeof(float) || value == typeof(double) || value == typeof(decimal) => new Dictionary<string, object?> { ["type"] = "number" },
            var value when value.IsEnum => new Dictionary<string, object?> { ["type"] = "string", ["enum"] = Enum.GetNames(value) },
            var value when value.IsArray => new Dictionary<string, object?> { ["type"] = "array", ["items"] = CreateSchema(value.GetElementType()!, ancestors: ancestors) },
            var value when value.IsGenericType && value.GetGenericTypeDefinition() == typeof(IEnumerable<>) => new Dictionary<string, object?> { ["type"] = "array", ["items"] = CreateSchema(value.GetGenericArguments()[0], ancestors: ancestors) },
            _ => CreateObjectSchema(effectiveType, ancestors)
        };

        if (nullableType is not null || isNullable)
        {
            schema["nullable"] = true;
        }

        return schema;
    }

    private static Dictionary<string, object?> CreateObjectSchema(Type type, ISet<Type>? ancestors)
    {
        if (type == typeof(object) || ancestors?.Contains(type) == true)
        {
            return new Dictionary<string, object?> { ["type"] = "object" };
        }

        var nestedAncestors = new HashSet<Type>(ancestors ?? new HashSet<Type>()) { type };
        var properties = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        var required = new List<string>();
        var nullabilityContext = new NullabilityInfoContext();

        foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                     .Where(static property => property.CanRead && property.GetIndexParameters().Length == 0)
                     .OrderBy(static property => property.Name, StringComparer.Ordinal))
        {
            var isNullable = Nullable.GetUnderlyingType(property.PropertyType) is not null ||
                (!property.PropertyType.IsValueType && nullabilityContext.Create(property).ReadState != NullabilityState.NotNull);
            properties.Add(property.Name, CreateSchema(property.PropertyType, isNullable, nestedAncestors));
            if (!isNullable)
            {
                required.Add(property.Name);
            }
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false
        };
    }
}
