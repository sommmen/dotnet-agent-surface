using System.Text;

namespace DotNetAgentSurface.Core;

/// <summary>Naming conventions shared by the command-line adapter, help output, and skill generation.</summary>
public static class OperationNaming
{
    /// <summary>
    /// Converts a camelCase, PascalCase, or snake_case identifier to kebab-case, for example
    /// <c>contextName</c> and <c>context_name</c> both become <c>context-name</c>, and <c>run_query</c> becomes <c>run-query</c>.
    /// </summary>
    public static string ToKebabCase(string name)
    {
        Guard.ThrowIfNull(name);

        var builder = new StringBuilder(name.Length + 4);
        for (var index = 0; index < name.Length; index++)
        {
            var current = name[index];
            if (current == '_')
            {
                AppendSeparator(builder);
                continue;
            }

            if (char.IsUpper(current))
            {
                var previousIsLowerOrDigit = index > 0 && (char.IsLower(name[index - 1]) || char.IsDigit(name[index - 1]));
                var startsWordAfterAcronym = index > 0 && char.IsUpper(name[index - 1]) && index + 1 < name.Length && char.IsLower(name[index + 1]);
                if (previousIsLowerOrDigit || startsWordAfterAcronym)
                {
                    AppendSeparator(builder);
                }

                builder.Append(char.ToLowerInvariant(current));
                continue;
            }

            builder.Append(current);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Produces the comparison key used to match command-line names: case, <c>-</c>, and <c>_</c> are ignored, so
    /// <c>--context-name</c>, <c>--contextName</c>, and <c>--context_name</c> all match the parameter <c>contextName</c>.
    /// </summary>
    public static string ToMatchKey(string name)
    {
        Guard.ThrowIfNull(name);

        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            if (character is not ('-' or '_'))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static void AppendSeparator(StringBuilder builder)
    {
        if (builder.Length > 0 && builder[builder.Length - 1] != '-')
        {
            builder.Append('-');
        }
    }
}
