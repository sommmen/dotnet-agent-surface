using DotNetAgentSurface.Core;
using Hangfire;

namespace DotNetAgentSurface.Hangfire;

/// <summary>Describes a recurring job currently registered in Hangfire storage.</summary>
public sealed record RecurringHangfireJobInfo(
    string JobId,
    string? JobType,
    string? Method,
    string? Cron,
    DateTime? NextExecution,
    DateTime? LastExecution);

/// <summary>Acknowledges a request to trigger a recurring Hangfire job.</summary>
public sealed record TriggerRecurringHangfireResult(
    string JobId,
    string Status,
    string? EnqueueId,
    DateTimeOffset TriggeredAt);

/// <summary>Specifies where a recurring job trigger is executed.</summary>
public enum HangfireExecutionModel
{
    EnqueueOnConfiguredStorage,
    ExecuteUsingIsolatedInMemoryServer
}

/// <summary>Configures the stable recurring-job operations.</summary>
public sealed class HangfireRecurringOperationsOptions
{
    /// <summary>Gets or sets the category assigned to both operations.</summary>
    public string? Category { get; set; } = "Hangfire";

    /// <summary>Gets or sets the safety level assigned to the trigger operation.</summary>
    public AgentSafetyLevel TriggerSafetyLevel { get; set; } = AgentSafetyLevel.Confirm;

    /// <summary>Gets or sets where trigger requests are executed.</summary>
    public HangfireExecutionModel ExecutionModel { get; set; } = HangfireExecutionModel.EnqueueOnConfiguredStorage;

    /// <summary>Gets or sets the maximum time to wait for an isolated execution to finish.</summary>
    public TimeSpan IsolatedExecutionTimeout { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets or sets the <see cref="JobActivator"/> used to resolve job instances when
    /// <see cref="ExecutionModel"/> is <see cref="HangfireExecutionModel.ExecuteUsingIsolatedInMemoryServer"/>.
    /// Defaults to <c>null</c>, which lets the isolated server fall back to
    /// <see cref="JobActivator.Current"/>, the same activator the application already uses.
    /// </summary>
    public JobActivator? IsolatedJobActivator { get; set; }
}
