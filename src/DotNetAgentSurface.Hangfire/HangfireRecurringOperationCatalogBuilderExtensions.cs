using DotNetAgentSurface.Core;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.States;
using Hangfire.Storage;

namespace DotNetAgentSurface.Hangfire;

/// <summary>Adds stable operations for listing and triggering recurring Hangfire jobs.</summary>
public static class HangfireRecurringOperationCatalogBuilderExtensions
{
    private static readonly TimeSpan IsolatedPollingInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Adds the <c>list-recurring-hangfire</c> and <c>trigger-recurring-hangfire</c> operations.
    /// Recurring-job storage is accessed only when an operation is invoked, never while the
    /// catalog is built.
    /// </summary>
    public static OperationCatalogBuilder AddHangfireRecurringOperations(
        this OperationCatalogBuilder builder,
        JobStorage storage,
        IRecurringJobManager jobManager,
        Action<HangfireRecurringOperationsOptions>? configure = null)
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

        var options = new HangfireRecurringOperationsOptions();
        configure?.Invoke(options);

        if (options.ExecutionModel == HangfireExecutionModel.ExecuteUsingIsolatedInMemoryServer &&
            options.IsolatedExecutionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configure),
                "The isolated execution timeout must be greater than zero.");
        }

        builder.Add(
            "list-recurring-hangfire",
            "Lists the recurring jobs currently registered in Hangfire storage.",
            (Func<IReadOnlyList<RecurringHangfireJobInfo>>)(() => ListRecurringJobs(storage)),
            operation => operation.Category = options.Category);

        builder.Add(
            "trigger-recurring-hangfire",
            "Requests that a currently registered recurring job be triggered. By default the job is " +
            "enqueued on the application's configured Hangfire storage; the acknowledgement never claims " +
            "that execution completed. When configured for isolated execution, the job runs to completion " +
            "or failure on a short-lived in-memory Hangfire server that never touches configured storage.",
            (Func<string, CancellationToken, Task<TriggerRecurringHangfireResult>>)(
                (jobId, cancellationToken) => TriggerRecurringJobAsync(storage, jobManager, jobId, options, cancellationToken)),
            operation =>
            {
                operation.Category = options.Category;
                operation.SafetyLevel = options.TriggerSafetyLevel;
            });

        return builder;
    }

    private static IReadOnlyList<RecurringHangfireJobInfo> ListRecurringJobs(JobStorage storage)
    {
        using var connection = storage.GetConnection();
        return connection.GetRecurringJobs()
            .Select(job => new RecurringHangfireJobInfo(
                job.Id,
                job.Job?.Type?.FullName,
                job.Job?.Method?.Name,
                job.Cron,
                job.NextExecution,
                job.LastExecution))
            .OrderBy(job => job.JobId, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<TriggerRecurringHangfireResult> TriggerRecurringJobAsync(
        JobStorage storage,
        IRecurringJobManager jobManager,
        string jobId,
        HangfireRecurringOperationsOptions options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return Rejected(jobId);
        }

        var registeredJob = FindRegisteredJob(storage, jobId);
        if (registeredJob is null)
        {
            return Rejected(jobId);
        }

        if (options.ExecutionModel == HangfireExecutionModel.ExecuteUsingIsolatedInMemoryServer)
        {
            return await TriggerIsolatedAsync(registeredJob, options, cancellationToken).ConfigureAwait(false);
        }

        return TriggerOnConfiguredStorage(jobManager, registeredJob.Id);
    }

    private static RecurringJobDto? FindRegisteredJob(JobStorage storage, string jobId)
    {
        using var connection = storage.GetConnection();
        return connection.GetRecurringJobs()
            .FirstOrDefault(job => string.Equals(job.Id, jobId, StringComparison.OrdinalIgnoreCase));
    }

    private static TriggerRecurringHangfireResult TriggerOnConfiguredStorage(IRecurringJobManager jobManager, string jobId)
    {
        if (jobManager is IRecurringJobManagerV2 managerV2)
        {
            // Fail closed: if the job disappeared between the lookup above and this call,
            // TriggerJob returns null and we must not report a false "enqueued" acknowledgement.
            var enqueueId = managerV2.TriggerJob(jobId)
                ?? throw new InvalidOperationException(
                    $"Recurring Hangfire job '{jobId}' was no longer available to trigger.");

            return new TriggerRecurringHangfireResult(jobId, "enqueued", enqueueId, DateTimeOffset.UtcNow);
        }

        jobManager.Trigger(jobId);
        return new TriggerRecurringHangfireResult(jobId, "enqueued", null, DateTimeOffset.UtcNow);
    }

    private static async Task<TriggerRecurringHangfireResult> TriggerIsolatedAsync(
        RecurringJobDto registeredJob,
        HangfireRecurringOperationsOptions options,
        CancellationToken cancellationToken)
    {
        if (registeredJob.Job is null)
        {
            throw new InvalidOperationException(
                $"Recurring Hangfire job '{registeredJob.Id}' could not be loaded for isolated execution.");
        }

        using var isolatedStorage = new InMemoryStorage();
        var serverOptions = new BackgroundJobServerOptions
        {
            ServerName = $"dotnet-agent-surface-isolated-{Guid.NewGuid():N}",
            WorkerCount = 1,
            Activator = options.IsolatedJobActivator,
            // An isolated, on-demand trigger reports a single attempt's outcome; Hangfire's default
            // global filters include automatic retries, which would delay a failure result and make
            // it indistinguishable from a slow success. No filters means no retries.
            FilterProvider = new JobFilterCollection()
        };

        using var server = new BackgroundJobServer(serverOptions, isolatedStorage);
        var client = new BackgroundJobClient(isolatedStorage);
        var enqueueId = client.Create(registeredJob.Job, new EnqueuedState());

        using var timeoutCts = new CancellationTokenSource(options.IsolatedExecutionTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        await WaitForCompletionAsync(isolatedStorage, enqueueId, linkedCts.Token).ConfigureAwait(false);

        return new TriggerRecurringHangfireResult(registeredJob.Id, "succeeded", enqueueId, DateTimeOffset.UtcNow);
    }

    private static async Task WaitForCompletionAsync(JobStorage isolatedStorage, string jobId, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using (var connection = isolatedStorage.GetConnection())
            {
                var jobData = connection.GetJobData(jobId);
                if (jobData is not null)
                {
                    if (string.Equals(jobData.State, SucceededState.StateName, StringComparison.Ordinal))
                    {
                        return;
                    }

                    if (string.Equals(jobData.State, FailedState.StateName, StringComparison.Ordinal) ||
                        string.Equals(jobData.State, DeletedState.StateName, StringComparison.Ordinal))
                    {
                        var stateData = connection.GetStateData(jobId);
                        var reason = stateData?.Reason;
                        throw new InvalidOperationException(
                            string.IsNullOrEmpty(reason)
                                ? $"Isolated execution of recurring Hangfire job ended in state '{jobData.State}'."
                                : $"Isolated execution of recurring Hangfire job ended in state '{jobData.State}': {reason}");
                    }
                }
            }

            await Task.Delay(IsolatedPollingInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static TriggerRecurringHangfireResult Rejected(string jobId) =>
        new(jobId, "rejected", null, DateTimeOffset.UtcNow);
}
