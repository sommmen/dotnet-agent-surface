namespace DotNetAgentSurface.Core;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class AgentOperationAttribute : Attribute
{
    public AgentOperationAttribute(string name, string description)
    {
        Name = name;
        Description = description;
    }

    public string Name { get; }

    public string Description { get; }

    public string? Category { get; init; }

    public AgentSafetyLevel SafetyLevel { get; init; } = AgentSafetyLevel.Safe;

    public string[] Examples { get; init; } = [];

    public string[] Aliases { get; init; } = [];
}

public enum AgentSafetyLevel
{
    Safe,
    Confirm,
    Dangerous
}
