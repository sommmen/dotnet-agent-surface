using System.Reflection;

namespace DotNetAgentSurface.Core;

public sealed class OperationDescriptor
{
    internal OperationDescriptor(MethodInfo method, AgentOperationAttribute operation)
    {
        Method = method;
        Name = operation.Name;
        Description = operation.Description;
        Category = operation.Category;
        SafetyLevel = operation.SafetyLevel;
        Examples = Array.AsReadOnly(operation.Examples);
        Aliases = Array.AsReadOnly(operation.Aliases);
        IsIdempotent = operation.IsIdempotent;
        Parameters = Array.AsReadOnly(method.GetParameters().Select(static parameter => new OperationParameterDescriptor(parameter)).ToArray());
    }

    public string Name { get; }

    public string Description { get; }

    public string? Category { get; }

    public AgentSafetyLevel SafetyLevel { get; }

    public IReadOnlyList<string> Examples { get; }

    public IReadOnlyList<string> Aliases { get; }

    public bool IsIdempotent { get; }

    public MethodInfo Method { get; }

    public Type? ServiceType => Method.IsStatic ? null : Method.DeclaringType;

    public IReadOnlyList<OperationParameterDescriptor> Parameters { get; }

    public Type DeclaredReturnType => Method.ReturnType;
}

public sealed class OperationParameterDescriptor
{
    internal OperationParameterDescriptor(ParameterInfo parameter)
    {
        Name = parameter.Name ?? throw new ArgumentException("Operation parameters must have names.", nameof(parameter));
        ParameterType = parameter.ParameterType;
        IsOptional = parameter.IsOptional;
        DefaultValue = parameter.IsOptional ? parameter.DefaultValue : null;
        IsCancellationToken = parameter.ParameterType == typeof(CancellationToken);
        IsNullable = NullabilityReader.IsNullable(parameter);
    }

    public string Name { get; }

    public Type ParameterType { get; }

    public bool IsOptional { get; }

    public object? DefaultValue { get; }

    public bool IsCancellationToken { get; }

    public bool IsNullable { get; }
}
