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
        => AddMcpServerTools(
            builder,
            assembly,
            targetFactory,
            static options => options.SafetyMapping = McpToolSafetyMapping.DestructiveIsDangerous);

    /// <summary>
    /// Discovers native MCP SDK tools like <see cref="AddMcpServerTools(OperationCatalogBuilder, Assembly, Func{Type, object?}?)"/>
    /// and applies <paramref name="configure"/> to choose which tools to import and how SDK hints map to safety.
    /// </summary>
    /// <remarks>
    /// SDK hint attributes (<c>Destructive</c>, <c>ReadOnly</c>, <c>Idempotent</c>, <c>OpenWorld</c>, <c>Title</c>) are
    /// imported as MCP annotations and do not affect <see cref="AgentSafetyLevel"/> unless
    /// <see cref="McpToolImportOptions.SafetyMapping"/> says so; hint-only is the default here, whereas the overload
    /// without <paramref name="configure"/> keeps mapping destructive tools to <see cref="AgentSafetyLevel.Dangerous"/>.
    /// A <see cref="DescriptionAttribute"/> on a parameter
    /// becomes that parameter's description and wins over XML documentation.
    /// </remarks>
    /// <param name="builder">The catalog builder to populate.</param>
    /// <param name="assembly">Assembly containing native MCP SDK tool types.</param>
    /// <param name="targetFactory">Optional factory for instances of non-static tool types.</param>
    /// <param name="configure">Optional import options callback.</param>
    public static OperationCatalogBuilder AddMcpServerTools(
        this OperationCatalogBuilder builder,
        Assembly assembly,
        Func<Type, object?>? targetFactory,
        Action<McpToolImportOptions>? configure)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (assembly is null)
        {
            throw new ArgumentNullException(nameof(assembly));
        }

        var importOptions = new McpToolImportOptions();
        configure?.Invoke(importOptions);
        var include = importOptions.Include ?? (static _ => true);
        var surfaces = importOptions.Surfaces ?? (static _ => AgentSurfaces.All);

        foreach (var toolType in GetLoadableTypes(assembly)
                     .Where(static type => type.IsClass && type.IsDefined(typeof(McpServerToolTypeAttribute), inherit: false)))
        {
            var toolMethods = toolType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(static method => method.IsDefined(typeof(McpServerToolAttribute), inherit: false))
                .Where(include)
                .ToArray();

            object? target = null;
            if (toolMethods.Any(static method => !method.IsStatic))
            {
                target = targetFactory?.Invoke(toolType) ?? Activator.CreateInstance(toolType);
                if (target is null)
                {
                    throw new InvalidOperationException(
                        $"MCP tool type '{toolType.FullName}' has instance tools but no target could be created. " +
                        "Provide a targetFactory for AddMcpServerTools.");
                }
            }

            foreach (var method in toolMethods)
            {
                var attribute = method.GetCustomAttribute<McpServerToolAttribute>(inherit: false)!;
                var name = string.IsNullOrWhiteSpace(attribute.Name) ? method.Name : attribute.Name!;
                var description = method.GetCustomAttribute<DescriptionAttribute>(inherit: false)?.Description;
                description = string.IsNullOrWhiteSpace(description) ? attribute.Title : description;
                description = string.IsNullOrWhiteSpace(description) ? name : description!;

                builder.Add(name, description, CreateDelegate(method, method.IsStatic ? null : target), options =>
                {
                    options.SafetyLevel = importOptions.SafetyMapping == McpToolSafetyMapping.DestructiveIsDangerous && attribute.Destructive
                        ? AgentSafetyLevel.Dangerous
                        : AgentSafetyLevel.Safe;
                    options.IsIdempotent = attribute.Idempotent;
                    options.Surfaces = surfaces(method);
                    options.PolicyMetadata.Add(new McpToolHints(
                        string.IsNullOrWhiteSpace(attribute.Title) ? null : attribute.Title,
                        attribute.ReadOnly,
                        attribute.Destructive,
                        attribute.Idempotent,
                        attribute.OpenWorld));
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
