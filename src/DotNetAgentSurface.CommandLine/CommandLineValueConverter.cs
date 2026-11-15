using System.Text.Json;

namespace DotNetAgentSurface.CommandLine;

/// <summary>
/// Converts raw command-line text to the JSON element the invoker binds to a parameter. Text is only parsed as JSON
/// when the target type needs it; string-like targets receive the text verbatim so values such as <c>123</c>,
/// <c>null</c>, or <c>true</c> bind to a <see cref="string"/> parameter.
/// </summary>
internal static class CommandLineValueConverter
{
    public static bool IsBoolean(Type type) => (Nullable.GetUnderlyingType(type) ?? type) == typeof(bool);

    public static bool TryGetCollectionElementType(Type type, out Type elementType)
    {
        if (type.IsArray)
        {
            elementType = type.GetElementType()!;
            return true;
        }

        if (type != typeof(string) && type.IsGenericType)
        {
            var arguments = type.GetGenericArguments();
            if (arguments.Length == 1 && typeof(IEnumerable<>).MakeGenericType(arguments[0]).IsAssignableFrom(type))
            {
                elementType = arguments[0];
                return true;
            }
        }

        elementType = typeof(object);
        return false;
    }

    public static JsonElement ToJson(Type type, string text)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;

        if (target == typeof(bool))
        {
            if (bool.TryParse(text, out var flag))
            {
                return JsonSerializer.SerializeToElement(flag);
            }

            throw new CommandLineUsageException($"'{text}' is not a valid boolean. Expected 'true' or 'false'.");
        }

        if (target.IsEnum)
        {
            return EnumToJson(target, text);
        }

        if (IsStringLike(target))
        {
            return JsonSerializer.SerializeToElement(text);
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(text);
        }
    }

    public static JsonElement ToJsonArray(Type elementType, IReadOnlyList<string> values)
    {
        if (values.Count == 1)
        {
            var single = values[0].TrimStart();
            if (single.StartsWith("[", StringComparison.Ordinal))
            {
                try
                {
                    using var document = JsonDocument.Parse(single);
                    if (document.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        return document.RootElement.Clone();
                    }
                }
                catch (JsonException)
                {
                    // Not a JSON array: treat the text as a single element.
                }
            }
        }

        return JsonSerializer.SerializeToElement(values.Select(value => ToJson(elementType, value)).ToList());
    }

    private static bool IsStringLike(Type type) =>
        type == typeof(string)
        || type == typeof(char)
        || type == typeof(Guid)
        || type == typeof(DateTime)
        || type == typeof(DateTimeOffset)
        || type == typeof(TimeSpan)
        || type == typeof(Uri)
        || type == typeof(Version)
        || type.FullName is "System.DateOnly" or "System.TimeOnly";

    private static JsonElement EnumToJson(Type enumType, string text)
    {
        object parsed;
        try
        {
            parsed = Enum.Parse(enumType, text.Trim(), ignoreCase: true);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            throw new CommandLineUsageException(
                $"'{text}' is not a valid {enumType.Name}. Valid values: {string.Join(", ", Enum.GetNames(enumType))}.");
        }

        return Enum.GetUnderlyingType(enumType) == typeof(ulong)
            ? JsonSerializer.SerializeToElement(Convert.ToUInt64(parsed))
            : JsonSerializer.SerializeToElement(Convert.ToInt64(parsed));
    }
}
