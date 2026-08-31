using System.Reflection;

namespace DotNetAgentSurface.Core;

public sealed class OperationCatalog
{
    private OperationCatalog(IReadOnlyList<OperationDescriptor> operations)
    {
        Operations = operations;
    }

    public IReadOnlyList<OperationDescriptor> Operations { get; }

    public static OperationCatalog Discover(params Type[] serviceTypes)
    {
        Guard.ThrowIfNull(serviceTypes);

        return CreateCatalog(serviceTypes.SelectMany(DiscoverOperations));
    }

    /// <summary>
    /// Sorts, validates, and finalizes a set of operation descriptors into an <see cref="OperationCatalog"/>.
    /// Shared by <see cref="Discover"/> and <see cref="OperationCatalogBuilder"/> so both registration paths
    /// enforce the same ordering and uniqueness guarantees.
    /// </summary>
    internal static OperationCatalog CreateCatalog(IEnumerable<OperationDescriptor> operations)
    {
        var sorted = operations
            .OrderBy(static operation => operation.Name, StringComparer.Ordinal)
            .ToArray();

        ValidateUniqueNames(sorted);
        return new OperationCatalog(Array.AsReadOnly(sorted));
    }

    internal static IEnumerable<OperationDescriptor> DiscoverOperations(Type serviceType)
    {
        Guard.ThrowIfNull(serviceType);

        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        return serviceType
            .GetMethods(flags)
            .Select(method => new { Method = method, Attribute = method.GetCustomAttribute<AgentOperationAttribute>() })
            .Where(static item => item.Attribute is not null)
            .Select(static item => CreateDescriptor(item.Method, item.Attribute!));
    }

    internal static OperationDescriptor CreateDescriptor(MethodInfo method, AgentOperationAttribute attribute)
    {
        if (string.IsNullOrWhiteSpace(attribute.Name))
        {
            throw new OperationCatalogException($"Operation on '{method.DeclaringType?.FullName}.{method.Name}' has an empty name.");
        }

        if (string.IsNullOrWhiteSpace(attribute.Description))
        {
            throw new OperationCatalogException($"Operation '{attribute.Name}' has an empty description.");
        }

        if (method.ContainsGenericParameters)
        {
            throw new OperationCatalogException($"Operation '{attribute.Name}' cannot be a generic method.");
        }

        foreach (var parameter in method.GetParameters())
        {
            if (parameter.ParameterType.IsByRef || parameter.IsOut)
            {
                throw new OperationCatalogException($"Operation '{attribute.Name}' cannot use ref or out parameter '{parameter.Name}'.");
            }
        }

        var aliasSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in attribute.Aliases)
        {
            if (string.IsNullOrWhiteSpace(alias))
            {
                throw new OperationCatalogException($"Operation '{attribute.Name}' has an empty alias.");
            }

            if (string.Equals(alias, attribute.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new OperationCatalogException($"Operation '{attribute.Name}' cannot alias its own name '{alias}'.");
            }

            if (!aliasSet.Add(alias))
            {
                throw new OperationCatalogException($"Operation '{attribute.Name}' has a duplicate alias '{alias}'.");
            }
        }

        return new OperationDescriptor(method, attribute);
    }

    /// <summary>
    /// Splits an operation's <see cref="OperationDescriptor.Category"/> into command-path segments. A category
    /// is treated as a whitespace-separated path, so <c>"projects archived"</c> becomes the two-level command
    /// group <c>projects archived</c> (invoked as <c>projects archived &lt;operation&gt;</c>); a null or blank
    /// category produces no segments, leaving the operation at the command-line root. This is the single source
    /// of truth for category-path splitting, shared by catalog collision validation here and by
    /// <c>OperationCommandLineAdapter</c>'s routing/help rendering so both always agree on category boundaries.
    /// </summary>
    public static string[] GetCategorySegments(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return [];
        }

        return category!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Formats an operation's full command path — its category segments (see <see cref="GetCategorySegments"/>)
    /// followed by <paramref name="leaf"/> (its name or an alias) — as a single space-separated string, used as
    /// the collision-detection key below.
    /// </summary>
    private static string FormatCommandPath(string? category, string leaf)
    {
        var segments = GetCategorySegments(category);
        return segments.Length == 0 ? leaf : string.Join(" ", segments) + " " + leaf;
    }

    /// <summary>
    /// Ensures every operation's full command path is unique across the catalog, case-insensitively. The full
    /// path is the operation's category chain (see <see cref="GetCategorySegments"/>) followed by its name, and
    /// separately followed by each alias. Operations in different categories may freely reuse the same leaf name
    /// or alias — only identical full paths collide, since categories are distinct command groups.
    /// </summary>
    private static void ValidateUniqueNames(IReadOnlyList<OperationDescriptor> operations)
    {
        var registrations = new Dictionary<string, (OperationDescriptor Operation, string Keyword)>(StringComparer.OrdinalIgnoreCase);

        foreach (var operation in operations)
        {
            RegisterCommandPath(registrations, operation, operation.Name, "name");

            foreach (var alias in operation.Aliases)
            {
                RegisterCommandPath(registrations, operation, alias, "alias");
            }
        }
    }

    private static void RegisterCommandPath(
        Dictionary<string, (OperationDescriptor Operation, string Keyword)> registrations,
        OperationDescriptor operation,
        string leaf,
        string keyword)
    {
        var path = FormatCommandPath(operation.Category, leaf);

        if (registrations.TryGetValue(path, out var existing))
        {
            var message = keyword == "name" && existing.Keyword == "name"
                ? $"Operation name '{path}' is duplicated by operations '{existing.Operation.Name}' and '{operation.Name}'."
                : $"Operation '{operation.Name}' {keyword} '{path}' collides with operation '{existing.Operation.Name}' {existing.Keyword} '{path}'.";

            throw new OperationCatalogException(message);
        }

        registrations[path] = (operation, keyword);
    }
}

public sealed class OperationCatalogException : Exception
{
    public OperationCatalogException(string message)
        : base(message)
    {
    }
}
