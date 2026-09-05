using DotNetAgentSurface.Core;
using Hangfire;
using Hangfire.States;

namespace DotNetAgentSurface.Hangfire;

/// <summary>Reports the current status of a background Hangfire job looked up by ID.</summary>
/// <param name="JobId">The Hangfire job ID that was looked up.</param>
/// <param name="State">
/// The job's current state name, for example <c>"Enqueued"</c>, <c>"Processing"</c>, <c>"Succeeded"</c>,
/// <c>"Failed"</c>, or <c>"Awaiting"</c> for a continuation still waiting on its parent job.
/// </param>
/// <param name="JobType">The full name of the job's declaring type, if it could be resolved.</param>
/// <param name="Method">The name of the job's target method, if it could be resolved.</param>
/// <param name="CreatedAt">The UTC time the job was created in storage.</param>
/// <param name="DashboardUrl">
/// The Hangfire dashboard URL for this job's details page, or null if no dashboard base URL was configured
/// via <see cref="HangfireJobStatusOperationsOptions.DashboardBaseUrl"/>. There is no reliable, generic way
/// for this library to discover where a dashboard is mounted, so the base URL must be supplied by the caller.
/// </param>
public sealed record HangfireJobStatus(
    string JobId,
    string State,
    string? JobType,
    string? Method,
    DateTime CreatedAt,
    string? DashboardUrl);

/// <summary>Configures the stable job-continuation and job-status operations.</summary>
public sealed class HangfireJobStatusOperationsOptions
{
    /// <summary>Gets or sets the category assigned to both operations.</summary>
    public string? Category { get; set; } = "Hangfire";

    /// <summary>Gets or sets the safety level assigned to the <c>continue-hangfire-job</c> operation.</summary>
    public AgentSafetyLevel ContinuationSafetyLevel { get; set; } = AgentSafetyLevel.Confirm;

    /// <summary>Gets or sets the safety level assigned to the <c>get-hangfire-job-status</c> operation.</summary>
    public AgentSafetyLevel StatusSafetyLevel { get; set; } = AgentSafetyLevel.Safe;

    /// <summary>
    /// Gets or sets the state the continuation job moves to once its parent job satisfies
    /// <see cref="ContinuationOptions"/>. Defaults to <see cref="EnqueuedState"/> when null, matching
    /// Hangfire's own <c>ContinueJobWith</c> default.
    /// </summary>
    public IState? NextState { get; set; }

    /// <summary>
    /// Gets or sets which parent job states trigger the continuation. Defaults to
    /// <see cref="JobContinuationOptions.OnlyOnSucceededState"/>, matching Hangfire's own
    /// <c>ContinueJobWith</c> default.
    /// </summary>
    public JobContinuationOptions ContinuationOptions { get; set; } = JobContinuationOptions.OnlyOnSucceededState;

    /// <summary>
    /// Gets or sets the base URL of the mounted Hangfire dashboard (for example
    /// <c>"https://ops.example.com/hangfire"</c>), used by <c>get-hangfire-job-status</c> to build a
    /// browsable <see cref="HangfireJobStatus.DashboardUrl"/>. There is no reliable, generic way to discover
    /// this at runtime, so it is left null (no dashboard URL is reported) unless supplied here.
    /// </summary>
    public string? DashboardBaseUrl { get; set; }
}
