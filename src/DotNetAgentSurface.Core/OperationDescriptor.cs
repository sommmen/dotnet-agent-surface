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
        OperationDocumentationOptions? documentationOptions = null)
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
        PolicyMetadata = Array.AsReadOnly((policyMetadata ?? []).ToArray());
        InvocationPolicies = Array.AsReadOnly((invocationPolicies ?? []).ToArray());
        Parameters = Array.AsReadOnly(method.GetParameters().Select(parameter =>
        {
            var paramName = parameter.Name ?? throw new ArgumentException($"Method parameter at index {Array.IndexOf(method.GetParameters(), parameter)} has no name.");
            return new OperationParameterDescriptor(parameter, documentation?.Parameters.TryGetValue(paramName, out var description) == true ? description : null);
        }).ToArray());
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
        IsNullable = NullabilityReader.IsNullable(parameter);
    }

    public string Name { get; }

    public string? Description { get; }

    public Type ParameterType { get; }

    public bool IsOptional { get; }

    public object? DefaultValue { get; }

    public bool IsCancellationToken { get; }

    public bool IsNullable { get; }
}
