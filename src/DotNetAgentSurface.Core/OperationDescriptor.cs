using System.ComponentModel;
using System.Reflection;

namespace DotNetAgentSurface.Core;

public sealed class OperationDescriptor
{
    internal OperationDescriptor(
        MethodInfo method,
        AgentOperationAttribute operation,
        object? boundTarget = null,
        IReadOnlyList<object>? policyMetadata = null,
        IReadOnlyList<IOperationInvocationPolicy>? invocationPolicies = null,
        IOperationDocumentationSource? documentationSource = null,
        OperationDocumentationOptions? documentationOptions = null,
        OperationHints? hints = null)
    {
        Method = method;
        BoundTarget = boundTarget;
        Name = operation.Name;
        var documentation = documentationSource?.GetDocumentation(method);
        Description = ResolveDescription(operation.Description, documentation, documentationOptions);
        Category = operation.Category;
        SafetyLevel = operation.SafetyLevel;
        Examples = Array.AsReadOnly(operation.Examples);
        Aliases = Array.AsReadOnly(operation.Aliases);
        IsIdempotent = operation.IsIdempotent;
        Hints = (hints ?? OperationHints.Empty).WithFallback(operation.GetHints());
        PolicyMetadata = Array.AsReadOnly((policyMetadata ?? []).ToArray());
        InvocationPolicies = Array.AsReadOnly((invocationPolicies ?? []).ToArray());
        Parameters = Array.AsReadOnly(method.GetParameters().Select(parameter =>
        {
            var paramName = parameter.Name ?? throw new ArgumentException($"Method parameter at index {Array.IndexOf(method.GetParameters(), parameter)} has no name.");
            return new OperationParameterDescriptor(parameter, ResolveParameterDescription(parameter, documentation, paramName));
        }).ToArray());
    }

    /// <summary>
    /// A <see cref="DescriptionAttribute"/> on the parameter wins over the XML documentation <c>&lt;param&gt;</c> text;
    /// the XML text is the fallback when the attribute is absent or blank.
    /// </summary>
    private static string? ResolveParameterDescription(ParameterInfo parameter, OperationDocumentation? documentation, string paramName)
    {
        var attributeDescription = parameter.GetCustomAttribute<DescriptionAttribute>(inherit: false)?.Description;
        if (!string.IsNullOrWhiteSpace(attributeDescription))
        {
            return attributeDescription;
        }

        return documentation?.Parameters.TryGetValue(paramName, out var description) == true ? description : null;
    }

    private static string ResolveDescription(string? explicitDescription, OperationDocumentation? documentation, OperationDocumentationOptions? options)
    {
        if (explicitDescription is not null)
        {
            return explicitDescription;
        }

        var summary = documentation?.Summary;
        var remarks = documentation?.Remarks;
        if (options?.IncludeRemarks == true && !string.IsNullOrWhiteSpace(remarks))
        {
            return string.IsNullOrWhiteSpace(summary) ? remarks! : summary + " " + remarks;
        }

        return summary ?? string.Empty;
    }

    public string Name { get; }

    public string Description { get; }

    public string? Category { get; }

    /// <summary>
    /// Gets the advertised safety level for this operation. See <see cref="AgentSafetyLevel"/> for why this is
    /// metadata only and must be paired with a confirmation-enforcing <see cref="IOperationInvocationPolicy"/>
    /// to actually be enforced by <see cref="OperationInvoker"/>.
    /// </summary>
    public AgentSafetyLevel SafetyLevel { get; }

    public IReadOnlyList<string> Examples { get; }

    public IReadOnlyList<string> Aliases { get; }

    public bool IsIdempotent { get; }

    /// <summary>Gets the independent descriptive hints (title, read-only, destructive, idempotent, open-world) for this operation.</summary>
    public OperationHints Hints { get; }

    public MethodInfo Method { get; }

    internal object? BoundTarget { get; }

    public Type? ServiceType => Method.IsStatic ? null : Method.DeclaringType;

    public IReadOnlyList<OperationParameterDescriptor> Parameters { get; }

    /// <summary>Host-defined metadata consumed by invocation policies.</summary>
    public IReadOnlyList<object> PolicyMetadata { get; }

    /// <summary>Policies attached by the source that registered this operation.</summary>
    public IReadOnlyList<IOperationInvocationPolicy> InvocationPolicies { get; }

    public Type DeclaredReturnType => Method.ReturnType;
}

public sealed class OperationParameterDescriptor
{
    internal OperationParameterDescriptor(ParameterInfo parameter, string? description = null)
    {
        Name = parameter.Name ?? throw new ArgumentException("Operation parameters must have names.", nameof(parameter));
        Description = description;
        ParameterType = parameter.ParameterType;
        IsOptional = parameter.IsOptional;
        DefaultValue = parameter.IsOptional ? parameter.DefaultValue : null;
        IsCancellationToken = parameter.ParameterType == typeof(CancellationToken);
        IsAmbient = AmbientParameters.IsAmbient(parameter.ParameterType);
        IsNullable = NullabilityReader.IsNullable(parameter);
    }

    public string Name { get; }

    public string? Description { get; }

    public Type ParameterType { get; }

    public bool IsOptional { get; }

    public object? DefaultValue { get; }

    public bool IsCancellationToken { get; }

    /// <summary>
    /// Gets a value indicating whether the parameter is supplied by the host (cancellation token, <see cref="IServiceProvider"/>,
    /// <see cref="IOperationContext"/>, <see cref="IOperationProgress"/>, <see cref="System.Security.Claims.ClaimsPrincipal"/> and the MCP
    /// SDK request types) instead of by the caller. Ambient parameters are excluded from schemas, help and skill output.
    /// </summary>
    public bool IsAmbient { get; }

    public bool IsNullable { get; }
}
