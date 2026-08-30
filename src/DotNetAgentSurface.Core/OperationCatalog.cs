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

        var operations = serviceTypes
            .SelectMany(DiscoverOperations)
            .OrderBy(static operation => operation.Name, StringComparer.Ordinal)
            .ToArray();

        ValidateUniqueNames(operations);
        return new OperationCatalog(Array.AsReadOnly(operations));
    }

    private static IEnumerable<OperationDescriptor> DiscoverOperations(Type serviceType)
    {
        Guard.ThrowIfNull(serviceType);

        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        return serviceType
            .GetMethods(flags)
            .Select(method => new { Method = method, Attribute = method.GetCustomAttribute<AgentOperationAttribute>() })
            .Where(static item => item.Attribute is not null)
            .Select(static item => CreateDescriptor(item.Method, item.Attribute!));
    }

    private static OperationDescriptor CreateDescriptor(MethodInfo method, AgentOperationAttribute attribute)
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

        return new OperationDescriptor(method, attribute);
    }

    private static void ValidateUniqueNames(IEnumerable<OperationDescriptor> operations)
    {
        var duplicate = operations
            .GroupBy(static operation => operation.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);

        if (duplicate is not null)
        {
            throw new OperationCatalogException($"Operation name '{duplicate.Key}' is duplicated.");
        }
    }
}

public sealed class OperationCatalogException : Exception
{
    public OperationCatalogException(string message)
        : base(message)
    {
    }
}
