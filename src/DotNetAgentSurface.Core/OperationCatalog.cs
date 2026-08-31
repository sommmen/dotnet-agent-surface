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

    private static void ValidateUniqueNames(IReadOnlyList<OperationDescriptor> operations)
    {
        var duplicate = operations
            .GroupBy(static operation => operation.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);

        if (duplicate is not null)
        {
            throw new OperationCatalogException($"Operation name '{duplicate.Key}' is duplicated.");
        }

        ValidateAliasCollisions(operations);
    }

    /// <summary>
    /// Ensures every canonical name and alias across the catalog is unique, case-insensitively. Plain
    /// name-to-name duplicates are already rejected by <see cref="ValidateUniqueNames"/> above, so this only
    /// needs to catch collisions that involve at least one alias.
    /// </summary>
    private static void ValidateAliasCollisions(IReadOnlyList<OperationDescriptor> operations)
    {
        var registrations = new Dictionary<string, (OperationDescriptor Operation, string Keyword)>(StringComparer.OrdinalIgnoreCase);

        foreach (var operation in operations)
        {
            RegisterKey(registrations, operation, operation.Name, "name");

            foreach (var alias in operation.Aliases)
            {
                RegisterKey(registrations, operation, alias, "alias");
            }
        }
    }

    private static void RegisterKey(
        Dictionary<string, (OperationDescriptor Operation, string Keyword)> registrations,
        OperationDescriptor operation,
        string key,
        string keyword)
    {
        if (registrations.TryGetValue(key, out var existing))
        {
            throw new OperationCatalogException(
                $"Operation '{operation.Name}' {keyword} '{key}' collides with operation '{existing.Operation.Name}' {existing.Keyword} '{key}'.");
        }

        registrations[key] = (operation, keyword);
    }
}

public sealed class OperationCatalogException : Exception
{
    public OperationCatalogException(string message)
        : base(message)
    {
    }
}
