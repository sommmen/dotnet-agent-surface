using System.Reflection;
using DotNetAgentSurface.Core;

namespace DotNetAgentSurface.Hangfire;

/// <summary>
/// Configures direct, local execution of discovered Hangfire jobs for workflow testing.
/// </summary>
public sealed class HangfireWorkflowTestRegistrationOptions
{
    /// <summary>
    /// Gets or sets the operation category. Defaults to <c>Hangfire workflow tests</c>.
    /// </summary>
    public string Category { get; set; } = "Hangfire workflow tests";

    /// <summary>
    /// Gets or sets the safety level of generated operations. Defaults to <see cref="AgentSafetyLevel.Confirm"/>.
    /// </summary>
    public AgentSafetyLevel SafetyLevel { get; set; } = AgentSafetyLevel.Confirm;

    /// <summary>
    /// Gets or sets the maximum execution time for one workflow test. Defaults to five minutes.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets the directory where captured transcripts are persisted. When <see langword="null"/>, transcripts are returned but not written to disk.
    /// </summary>
    public string? ArtifactDirectory { get; set; }

    /// <summary>
    /// Gets or sets a function that determines each generated operation name.
    /// </summary>
    public Func<Type, string>? NameFactory { get; set; }

    /// <summary>
    /// Gets or sets a function that selects a job execution method.
    /// </summary>
    public Func<Type, MethodInfo?>? MethodSelector { get; set; }

    /// <summary>
    /// Gets or sets a predicate that excludes a discovered job type.
    /// </summary>
    public Func<Type, bool>? Exclude { get; set; }
}
