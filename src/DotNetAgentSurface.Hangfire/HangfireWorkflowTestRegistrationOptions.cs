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

    /// <summary>
    /// Gets or sets whether discovery diagnostics should cause registration to fail. Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Only the type-scanning overloads that enumerate many candidate types — <see cref="HangfireWorkflowTestCatalogBuilderExtensions.RegisterAllOptionsJobs"/>
    /// and <see cref="HangfireWorkflowTestCatalogBuilderExtensions.RegisterAttributeWorkflowTests"/> — consult this
    /// property and populate <see cref="DiscoveryReports"/>/<see cref="Diagnostics"/>. The closed-generic overloads,
    /// <see cref="HangfireWorkflowTestCatalogBuilderExtensions.RegisterWorkflowTests{TJobBase}"/> and
    /// <see cref="HangfireWorkflowTestCatalogBuilderExtensions.RegisterWorkflowTests{TJobBase, TOptions}"/>, already
    /// know the exact base type being registered, so an invalid execution method is always an
    /// <see cref="InvalidOperationException"/> for those overloads regardless of this setting.
    /// </remarks>
    public bool StrictValidation { get; set; }

    /// <summary>
    /// Gets a first-class report for every skipped, warning, and registered discovery outcome.
    /// </summary>
    /// <remarks>
    /// Populated only by <see cref="HangfireWorkflowTestCatalogBuilderExtensions.RegisterAllOptionsJobs"/> and
    /// <see cref="HangfireWorkflowTestCatalogBuilderExtensions.RegisterAttributeWorkflowTests"/>; see
    /// <see cref="StrictValidation"/> for why the closed-generic overloads do not use it.
    /// </remarks>
    public ICollection<HangfireJobDiscoveryReport> DiscoveryReports { get; } = new List<HangfireJobDiscoveryReport>();

    /// <summary>
    /// Gets the legacy diagnostics produced while types are inspected.
    /// </summary>
    /// <remarks>
    /// Populated only by <see cref="HangfireWorkflowTestCatalogBuilderExtensions.RegisterAllOptionsJobs"/> and
    /// <see cref="HangfireWorkflowTestCatalogBuilderExtensions.RegisterAttributeWorkflowTests"/>; see
    /// <see cref="StrictValidation"/> for why the closed-generic overloads do not use it.
    /// </remarks>
    public ICollection<HangfireJobRegistrationDiagnostic> Diagnostics { get; } = new List<HangfireJobRegistrationDiagnostic>();
}
