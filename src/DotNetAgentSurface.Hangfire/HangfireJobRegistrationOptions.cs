using System.Reflection;
using DotNetAgentSurface.Core;

namespace DotNetAgentSurface.Hangfire;

/// <summary>
/// Configures reflection-based discovery and operation metadata for <see cref="HangfireJob"/>-based registrations.
/// This registration path is unsupported in trimmed and NativeAOT applications until a source-generated
/// registration path is available.
/// </summary>
public sealed class HangfireJobRegistrationOptions
{
    /// <summary>Gets or sets the category assigned to discovered operations.</summary>
    public string? Category { get; set; } = "Hangfire jobs";

    /// <summary>Gets or sets the default safety level for discovered operations.</summary>
    public AgentSafetyLevel SafetyLevel { get; set; } = AgentSafetyLevel.Confirm;

    /// <summary>Gets or sets a factory for stable operation names.</summary>
    public Func<Type, string>? NameFactory { get; set; }

    /// <summary>Gets or sets an explicit execution-method selector.</summary>
    public Func<Type, MethodInfo?>? MethodSelector { get; set; }

    /// <summary>Gets or sets a synchronous, deterministic metadata enrichment callback.</summary>
    public Action<Type, HangfireJobRegistrationMetadata>? Enrich { get; set; }

    /// <summary>Gets or sets a predicate that excludes a discovered job type.</summary>
    public Func<Type, bool>? Exclude { get; set; }

    /// <summary>Gets or sets whether discovery diagnostics should cause registration to fail.</summary>
    public bool StrictValidation { get; set; }

    /// <summary>Gets a first-class report for every skipped, warning, and registered discovery outcome.</summary>
    public ICollection<HangfireJobDiscoveryReport> DiscoveryReports { get; } = new List<HangfireJobDiscoveryReport>();

    /// <summary>Gets the legacy diagnostics produced while types are inspected.</summary>
    public ICollection<HangfireJobRegistrationDiagnostic> Diagnostics { get; } = new List<HangfireJobRegistrationDiagnostic>();
}

/// <summary>Mutable operation metadata supplied to job registration enrichment.</summary>
public sealed class HangfireJobRegistrationMetadata
{
    internal HangfireJobRegistrationMetadata(string name, string description, string? category, AgentSafetyLevel safetyLevel)
    {
        Name = name;
        Description = description;
        Category = category;
        SafetyLevel = safetyLevel;
    }

    public string Name { get; set; }
    public string Description { get; set; }
    public string? Category { get; set; }
    public AgentSafetyLevel SafetyLevel { get; set; }
    public IList<string> Aliases { get; } = new List<string>();
    public IList<string> Examples { get; } = new List<string>();
}

/// <summary>Describes a skipped or ambiguously discovered job type.</summary>
public sealed class HangfireJobRegistrationDiagnostic
{
    internal HangfireJobRegistrationDiagnostic(Type? jobType, string message)
    {
        JobType = jobType;
        Message = message;
    }

    public Type? JobType { get; }
    public string Message { get; }
}
