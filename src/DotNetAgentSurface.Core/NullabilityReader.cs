using System.Reflection;

namespace DotNetAgentSurface.Core;

/// <summary>
/// Determines top-level reference-type nullability by reading the compiler-emitted
/// <c>System.Runtime.CompilerServices.NullableAttribute</c> / <c>NullableContextAttribute</c> metadata directly via
/// <see cref="CustomAttributeData"/>.
/// </summary>
/// <remarks>
/// <c>System.Reflection.NullabilityInfoContext</c> is unavailable on <c>netstandard2.0</c>. The C# compiler
/// emits these two attributes into IL metadata identically regardless of target framework, so reading them
/// directly reproduces <c>NullabilityInfoContext</c>'s top-level nullability detection uniformly across every
/// target framework this library supports, with no conditional compilation required.
/// </remarks>
internal static class NullabilityReader
{
    private const string NullableAttributeName = "System.Runtime.CompilerServices.NullableAttribute";
    private const string NullableContextAttributeName = "System.Runtime.CompilerServices.NullableContextAttribute";

    public static bool IsNullable(ParameterInfo parameter)
    {
        if (Nullable.GetUnderlyingType(parameter.ParameterType) is not null)
        {
            return true;
        }

        if (parameter.ParameterType.IsValueType)
        {
            return false;
        }

        if (TryReadNullableFlag(parameter.GetCustomAttributesData(), out var isNullable))
        {
            return isNullable;
        }

        return IsNullableFromContext(parameter.Member);
    }

    public static bool IsNullable(PropertyInfo property)
    {
        if (Nullable.GetUnderlyingType(property.PropertyType) is not null)
        {
            return true;
        }

        if (property.PropertyType.IsValueType)
        {
            return false;
        }

        if (TryReadNullableFlag(property.GetCustomAttributesData(), out var isNullable))
        {
            return isNullable;
        }

        return IsNullableFromContext(property);
    }

    private static bool IsNullableFromContext(MemberInfo? member)
    {
        for (var current = member; current is not null; current = current.DeclaringType)
        {
            if (TryReadNullableContextFlag(current.GetCustomAttributesData(), out var isNullable))
            {
                return isNullable;
            }
        }

        // No #nullable context found anywhere (fully-oblivious code): treat as nullable, matching
        // NullabilityInfoContext's Unknown state being treated as "not proven non-null".
        return true;
    }

    private static bool TryReadNullableFlag(IList<CustomAttributeData> attributes, out bool isNullable)
    {
        foreach (var attribute in attributes)
        {
            if (attribute.AttributeType.FullName != NullableAttributeName)
            {
                continue;
            }

            if (attribute.ConstructorArguments.Count != 1)
            {
                break;
            }

            var argument = attribute.ConstructorArguments[0];
            var flagByte = argument.Value switch
            {
                byte single => single,
                IReadOnlyList<CustomAttributeTypedArgument> array when array.Count > 0 => (byte)array[0].Value!,
                _ => (byte?)null
            };

            if (flagByte is byte flag)
            {
                // 1 = NotAnnotated (non-null), 2 = Annotated (nullable), 0 = Oblivious (treated as nullable).
                isNullable = flag != 1;
                return true;
            }

            break;
        }

        isNullable = false;
        return false;
    }

    private static bool TryReadNullableContextFlag(IList<CustomAttributeData> attributes, out bool isNullable)
    {
        foreach (var attribute in attributes)
        {
            if (attribute.AttributeType.FullName != NullableContextAttributeName)
            {
                continue;
            }

            if (attribute.ConstructorArguments.Count == 1 && attribute.ConstructorArguments[0].Value is byte flag)
            {
                isNullable = flag != 1;
                return true;
            }

            break;
        }

        isNullable = false;
        return false;
    }
}
