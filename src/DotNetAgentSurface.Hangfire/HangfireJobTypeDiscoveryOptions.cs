using DotNetAgentSurface.Core;

namespace DotNetAgentSurface.Hangfire;

/// <summary>
/// Configures how class-based Hangfire jobs — discovered via reflection rather than read from Hangfire
/// storage — are represented in an operation catalog. Discovery never registers a recurring job; each
/// discovered type becomes an operation that enqueues a single background execution of that job on demand,
/// leaving any existing recurring-job configuration for the type untouched. This reflection-based path is
/// unsupported in trimmed and NativeAOT applications until a source-generated registration path is available.
/// </summary>
public sealed class HangfireJobTypeDiscoveryOptions
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
    /// Gets or sets a factory that derives the operation name for a discovered job type. The type's
    /// <see cref="Type.FullName"/> is used by default.
    /// </summary>
    public Func<Type, string>? OperationNameFactory { get; set; }

    /// <summary>
    /// Gets or sets an optional callback that enriches metadata for each discovered job type.
    /// </summary>
    public Action<Type, HangfireOperationRegistrationOptions>? Enrich { get; set; }

    /// <summary>Gets a first-class report for every registered discovery outcome.</summary>
    public ICollection<HangfireJobDiscoveryReport> DiscoveryReports { get; } = new List<HangfireJobDiscoveryReport>();
}
