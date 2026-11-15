using System.ComponentModel;
using System.Linq.Expressions;
using System.Reflection;
using DotNetAgentSurface.Core;
using ModelContextProtocol.Protocol;
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

        foreach (var toolType in GetLoadableTypes(assembly)
                     .Where(static type => type.IsClass && type.IsDefined(typeof(McpServerToolTypeAttribute), inherit: false)))
        {
            var toolMethods = toolType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(static method => method.IsDefined(typeof(McpServerToolAttribute), inherit: false))
                .Where(include)
                .ToArray();

            object? target = null;
            if (!importOptions.ResolveTargetsFromServices && toolMethods.Any(static method => !method.IsStatic))
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
                var name = string.IsNullOrWhiteSpace(attribute.Name) ? DeriveName(method) : attribute.Name!;
                var description = method.GetCustomAttribute<DescriptionAttribute>(inherit: false)?.Description;
                description = string.IsNullOrWhiteSpace(description) ? attribute.Title : description;
                description = string.IsNullOrWhiteSpace(description) ? name : description!;
                var sdkAnnotations = TryGetSdkAnnotations(method);

                void Configure(OperationRegistrationOptions options)
                {
                    options.SafetyLevel = importOptions.SafetyMapping == McpToolSafetyMapping.DestructiveIsDangerous && attribute.Destructive
                        ? AgentSafetyLevel.Dangerous
                        : AgentSafetyLevel.Safe;
                    options.IsIdempotent = attribute.Idempotent;
                    options.Title = string.IsNullOrWhiteSpace(attribute.Title) ? null : attribute.Title;
                    options.ReadOnly = sdkAnnotations?.ReadOnlyHint;
                    options.Destructive = sdkAnnotations?.DestructiveHint;
                    options.Idempotent = sdkAnnotations?.IdempotentHint;
                    options.OpenWorld = sdkAnnotations?.OpenWorldHint;
                    if (attribute.UseStructuredContent)
                    {
                        options.PolicyMetadata.Add(new McpStructuredContentHint());
                    }
                }

                if (importOptions.ResolveTargetsFromServices)
                {
                    builder.AddMethod(method, name, description, Configure);
                }
                else
                {
                    builder.Add(name, description, CreateDelegate(method, method.IsStatic ? null : target), Configure);
                }
            }
        }
        return builder;
    }

    // The SDK keeps "was this hint set explicitly" private; asking it to build the tool is the supported way to read it.
    private static ToolAnnotations? TryGetSdkAnnotations(MethodInfo method)
    {
        try
        {
            return McpServerTool.Create(method, static _ => throw new InvalidOperationException(), options: null).ProtocolTool.Annotations;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceWarning(
                $"Could not read MCP SDK annotations for '{method.DeclaringType?.FullName}.{method.Name}'; its hints are not imported. {exception.GetType().Name}: {exception.Message}");
            return null;
        }
    }

    /// <summary>Derives the tool name the way the SDK does: no <c>Async</c> suffix, non-alphanumerics to underscores, snake_case.</summary>
    internal static string DeriveName(MethodInfo method)
    {
        var name = method.Name;
        var type = method.ReturnType;
        var isAsync = type == typeof(Task) || type == typeof(ValueTask)
            || (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ValueTask<>)));
        if (isAsync && name.EndsWith("Async", StringComparison.Ordinal) && name.Length > "Async".Length)
        {
            name = name.Substring(0, name.Length - "Async".Length);
        }

        name = System.Text.RegularExpressions.Regex.Replace(name, "[^0-9A-Za-z]+", "_").Trim('_');
        if (name.Length == 0)
        {
            name = method.Name;
        }

        return SnakeCaseLower.ConvertName(name);
    }

    private static readonly System.Text.Json.JsonNamingPolicy SnakeCaseLower = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;

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
