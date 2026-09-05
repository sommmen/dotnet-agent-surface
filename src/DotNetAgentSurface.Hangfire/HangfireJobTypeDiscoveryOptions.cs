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
    /// Gets or sets the safety level assigned when no per-job enrichment is supplied. Defaults to
    /// <see cref="AgentSafetyLevel.Confirm"/> because enqueuing a background job is rarely something an agent
    /// should be able to do without confirmation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This default does not, by itself, prevent unconfirmed execution.</b> <see cref="AgentSafetyLevel"/> is
    /// metadata only; it is only enforced if the <see cref="OperationInvoker"/> used to invoke discovered job
    /// operations is constructed with a policy implementing <see cref="IConfirmationEnforcingPolicy"/> (for
    /// example <see cref="DangerousOperationConfirmationPolicy"/>).
    /// </para>
    /// <para>
    /// Consumers who call <see cref="HangfireJobTypeOperationCatalogBuilderExtensions.AddHangfireJobTypes"/> and
    /// then construct an <see cref="OperationInvoker"/> without such a policy will find that
    /// <see cref="OperationInvoker.InvokeAsync"/> throws <see cref="ConfirmationPolicyMissingException"/> instead
    /// of silently enqueuing the job unconfirmed &#8212; wire a confirmation-enforcing policy into the invoker to
    /// enable (and gate) execution.
    /// </para>
    /// </remarks>
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
