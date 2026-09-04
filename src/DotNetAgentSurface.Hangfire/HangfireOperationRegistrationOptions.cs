using DotNetAgentSurface.Core;

namespace DotNetAgentSurface.Hangfire;

/// <summary>Provides metadata overrides for a discovered Hangfire job type.</summary>
public sealed class HangfireOperationRegistrationOptions
{
    /// <summary>Gets or sets the operation name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the operation description.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the operation category.</summary>
    public string? Category { get; set; }

    /// <summary>Gets or sets the operation safety level.</summary>
    public AgentSafetyLevel? SafetyLevel { get; set; }

    /// <summary>Gets the aliases assigned to the operation.</summary>
    public IList<string> Aliases { get; } = new List<string>();

    /// <summary>Gets the examples assigned to the operation.</summary>
    public IList<string> Examples { get; } = new List<string>();
}