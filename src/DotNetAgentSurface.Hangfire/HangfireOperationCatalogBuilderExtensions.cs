using DotNetAgentSurface.Core;
using Hangfire;
using Hangfire.Storage;

namespace DotNetAgentSurface.Hangfire;

/// <summary>
/// Adds operations that trigger recurring jobs registered in Hangfire storage.
/// </summary>
public static class HangfireOperationCatalogBuilderExtensions
{
    /// <summary>
    /// Discovers recurring jobs from <paramref name="storage"/> and registers one operation for each job.
    /// Each operation triggers its job through Hangfire's <see cref="IRecurringJobManager"/> rather than
    /// invoking the job method directly.
    /// </summary>
    public static OperationCatalogBuilder AddHangfireRecurringJobs(
        this OperationCatalogBuilder builder,
        JobStorage storage,
        IRecurringJobManager jobManager,
        Action<HangfireDiscoveryOptions>? configure = null)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (storage is null)
        {
            throw new ArgumentNullException(nameof(storage));
        }

        if (jobManager is null)
        {
            throw new ArgumentNullException(nameof(jobManager));
        }

        var options = new HangfireDiscoveryOptions();
        configure?.Invoke(options);

        using var connection = storage.GetConnection();
        foreach (var recurringJob in connection.GetRecurringJobs())
        {
            var registration = new HangfireOperationRegistrationOptions();
            options.Enrich?.Invoke(recurringJob, registration);

            var jobId = recurringJob.Id;
            builder.Add(
                registration.Name ?? jobId,
                registration.Description ?? CreateDescription(recurringJob),
                (Action)(() => jobManager.Trigger(jobId)),
                operation =>
                {
                    operation.Category = registration.Category ?? options.Category;
                    operation.SafetyLevel = registration.SafetyLevel ?? options.SafetyLevel;
                    operation.Aliases.AddRange(registration.Aliases);
                    operation.Examples.AddRange(registration.Examples);
                });
        }

        return builder;
    }

    private static string CreateDescription(RecurringJobDto recurringJob)
    {
        var job = recurringJob.Job;
        return job is null
            ? $"Triggers the '{recurringJob.Id}' recurring job."
            : $"Triggers the '{recurringJob.Id}' recurring job ({job.Type.FullName}.{job.Method.Name}).";
    }
}
