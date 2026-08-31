using System.ComponentModel;
using System.Linq.Expressions;
using System.Reflection;
using DotNetAgentSurface.Core;
using ModelContextProtocol.Server;

namespace DotNetAgentSurface.Mcp;

/// <summary>Registers native MCP SDK tools with an operation catalog builder.</summary>
public static class McpOperationCatalogBuilderExtensions
{
    /// <summary>
    /// Discovers public <see cref="McpServerToolAttribute"/> methods on types marked with
    /// <see cref="McpServerToolTypeAttribute"/> in <paramref name="assembly"/>.
    /// </summary>
    /// <param name="builder">The catalog builder to populate.</param>
    /// <param name="assembly">Assembly containing native MCP SDK tool types.</param>
    /// <param name="targetFactory">
    /// Optional factory for instances of non-static tool types. When omitted, a public parameterless
    /// constructor is used.
    /// </param>
    public static OperationCatalogBuilder AddMcpServerTools(
        this OperationCatalogBuilder builder,
        Assembly assembly,
        Func<Type, object?>? targetFactory = null)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (assembly is null)
        {
            throw new ArgumentNullException(nameof(assembly));
        }

        foreach (var toolType in GetLoadableTypes(assembly)
                     .Where(static type => type.IsClass && type.IsDefined(typeof(McpServerToolTypeAttribute), inherit: false)))
        {
            object? target = null;
            var instanceMethods = toolType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(static method => method.IsDefined(typeof(McpServerToolAttribute), inherit: false))
                .ToArray();

            if (instanceMethods.Length > 0)
            {
                target = targetFactory?.Invoke(toolType) ?? Activator.CreateInstance(toolType);
                if (target is null)
                {
                    throw new InvalidOperationException(
                        $"MCP tool type '{toolType.FullName}' has instance tools but no target could be created. " +
                        "Provide a targetFactory for AddMcpServerTools.");
                }
            }

            foreach (var method in toolType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                         .Where(static method => method.IsDefined(typeof(McpServerToolAttribute), inherit: false)))
            {
                var attribute = method.GetCustomAttribute<McpServerToolAttribute>(inherit: false)!;
                var name = string.IsNullOrWhiteSpace(attribute.Name) ? method.Name : attribute.Name;
                var description = method.GetCustomAttribute<DescriptionAttribute>(inherit: false)?.Description;
                description = string.IsNullOrWhiteSpace(description) ? attribute.Title : description;
                description = string.IsNullOrWhiteSpace(description) ? name : description;

                builder.Add(name, description, CreateDelegate(method, method.IsStatic ? null : target), options =>
                {
                    options.SafetyLevel = attribute.Destructive
                        ? AgentSafetyLevel.Dangerous
                        : AgentSafetyLevel.Safe;
                    options.IsIdempotent = attribute.Idempotent;
                });
            }
        }

        return builder;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(static type => type is not null)!;
        }
    }

    private static Delegate CreateDelegate(MethodInfo method, object? target)
    {
        var signature = method.GetParameters().Select(static parameter => parameter.ParameterType)
            .Append(method.ReturnType)
            .ToArray();
        var delegateType = Expression.GetDelegateType(signature);
        return target is null
            ? method.CreateDelegate(delegateType)
            : method.CreateDelegate(delegateType, target);
    }
}
