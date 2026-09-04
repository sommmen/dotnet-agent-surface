using System.Reflection;

namespace DotNetAgentSurface.Hangfire;

/// <summary>Describes the outcome of inspecting a Hangfire job type during reflection-based discovery.</summary>
public sealed record HangfireJobDiscoveryReport(
    Assembly? Assembly,
    Type? JobType,
    string Reason,
    string? Method,
    string? OperationName,
    HangfireJobDiscoveryDisposition Disposition,
    bool StrictValidation);

/// <summary>Identifies how reflection discovery handled an inspected item.</summary>
public enum HangfireJobDiscoveryDisposition
{
    Registered,
    Skipped,
    Warning,
    Failed,
}
