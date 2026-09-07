namespace DotNetAgentSurface.Hangfire;

/// <summary>
/// The captured outcome of a direct Hangfire workflow test.
/// </summary>
public sealed record HangfireWorkflowTestResult(
    string Job,
    HangfireWorkflowTestStatus Status,
    TimeSpan Elapsed,
    string Transcript,
    string? ArtifactPath,
    string? Error);

/// <summary>
/// Describes how a direct Hangfire workflow test ended.
/// </summary>
public enum HangfireWorkflowTestStatus
{
    Succeeded,
    Failed,
    TimedOut,
    Cancelled,
}
