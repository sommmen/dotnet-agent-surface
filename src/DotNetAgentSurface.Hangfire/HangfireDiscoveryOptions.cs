using DotNetAgentSurface.Core;
using Hangfire.Storage;

namespace DotNetAgentSurface.Hangfire;

/// <summary>
/// Configures how recurring Hangfire jobs are represented in an operation catalog.
/// </summary>
public sealed class HangfireDiscoveryOptions
{
    /// <summary>
    /// Gets or sets the category assigned to discovered operations.
    /// </summary>
    public string? Category { get; set; } = "Hangfire";

    /// <summary>
    /// Gets or sets the safety level assigned when no per-job enrichment is supplied.
    /// </summary>
    public AgentSafetyLevel SafetyLevel { get; set; } = AgentSafetyLevel.Confirm;

    /// <summary>
    /// Gets or sets an optional callback that enriches metadata for each discovered recurring job.
    /// </summary>
    public Action<RecurringJobDto, HangfireOperationRegistrationOptions>? Enrich { get; set; }
}

/// <summary>
/// Provides per-job metadata overrides for a discovered Hangfire recurring job.
/// </summary>
public sealed class HangfireOperationRegistrationOptions
{
    /// <summary>
    /// Gets or sets the operation name. The recurring job identifier is used by default.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the operation description. A description derived from the Hangfire job is used by default.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the category. The discovery category is used by default.
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    /// Gets or sets the safety level. The discovery safety level is used by default.
    /// </summary>
    public AgentSafetyLevel? SafetyLevel { get; set; }

    /// <summary>
    /// Gets the aliases assigned to the operation.
    /// </summary>
    public List<string> Aliases { get; } = [];

    /// <summary>
    /// Gets the invocation examples assigned to the operation.
    /// </summary>
    public List<string> Examples { get; } = [];
}
