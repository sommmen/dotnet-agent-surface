using DotNetAgentSurface.Core;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;

namespace DotNetAgentSurface.Hangfire;

/// <summary>
/// Adds stable operations for chaining a follow-up background job onto an existing one and for
/// looking up a background job's current status by ID.
/// </summary>
public static class HangfireJobStatusOperationCatalogBuilderExtensions
{
    /// <summary>
    /// Adds the <c>continue-hangfire-job</c> operation, which enqueues <paramref name="job"/> to run once the
    /// background job identified by a supplied parent job ID reaches a matching state, and the
    /// <c>get-hangfire-job-status</c> operation, which reports the current status of any background job by ID.
    /// </summary>
    /// <param name="builder">The catalog builder to add operations to.</param>
    /// <param name="backgroundJobClient">
    /// Used by <c>continue-hangfire-job</c> to create the continuation job via
    /// <see cref="IBackgroundJobClient.Create"/>, the same way Hangfire's own <c>ContinueJobWith</c>
    /// extension methods do internally, but starting from a caller-supplied <see cref="Job"/> instead of a
    /// method-call expression so it composes with the same discovery-built <see cref="Job"/> instances used
    /// elsewhere in this library.
    /// </param>
    /// <param name="storage">
    /// Used by <c>get-hangfire-job-status</c> to read job state from Hangfire storage via
    /// <see cref="IStorageConnection.GetJobData(string)"/>. This never touches the job that
    /// <paramref name="job"/> represents until an operation is actually invoked.
    /// </param>
    /// <param name="job">
    /// The job to enqueue as a continuation once its parent job ID (supplied at invocation time) reaches a
    /// matching state. Build this the same way <c>AddHangfireJobTypes</c> does, for example
    /// <c>new Job(typeof(MyJob), typeof(MyJob).GetMethod(nameof(MyJob.Execute)))</c>, or use
    /// <see cref="HangfireJobStatusOperations.ForJob{TJob}()"/>/<see cref="HangfireJobStatusOperations.ForJob{TJob, TOptions}(TOptions)"/>
    /// to build it via the same convention-based method discovery <c>RegisterJobs</c> uses internally.
    /// </param>
    /// <param name="configure">Optional callback to customize category, safety level, operation names, and continuation defaults.</param>
    /// <remarks>
    /// <para>
    /// Both operations default to <see cref="AgentSafetyLevel.Confirm"/> and <see cref="AgentSafetyLevel.Safe"/>
    /// respectively (see <see cref="HangfireJobStatusOperationsOptions"/>), but <see cref="AgentSafetyLevel"/> is
    /// metadata only. Invoking <c>continue-hangfire-job</c> through an <see cref="OperationInvoker"/> without a
    /// policy implementing <see cref="IConfirmationEnforcingPolicy"/> (for example
    /// <see cref="DangerousOperationConfirmationPolicy"/>) will throw <see cref="ConfirmationPolicyMissingException"/>
    /// rather than silently bypassing confirmation.
    /// </para>
    /// <para>
    /// Each call to this method binds <c>continue-hangfire-job</c> to exactly one <paramref name="job"/>. To
    /// expose several distinct continuation targets from one CLI, call this method once per target and set
    /// <see cref="HangfireJobStatusOperationsOptions.ContinuationOperationName"/> to a distinct name each time
    /// (for example <c>"continue-with-report-job"</c>, <c>"continue-with-cleanup-job"</c>) — every operation in
    /// a catalog must have a unique name, so registering the default <c>"continue-hangfire-job"</c> name twice
    /// throws an <see cref="OperationCatalogException"/> when the catalog is built. Only the first call needs to
    /// add <c>get-hangfire-job-status</c>; later calls can set
    /// <see cref="HangfireJobStatusOperationsOptions.StatusOperationName"/> to null to skip re-adding it, since a
    /// single status-lookup operation already works for any job ID regardless of which call created it.
    /// </para>
    /// </remarks>
    public static OperationCatalogBuilder AddHangfireJobStatusOperations(
        this OperationCatalogBuilder builder,
        IBackgroundJobClient backgroundJobClient,
        JobStorage storage,
        Job job,
        Action<HangfireJobStatusOperationsOptions>? configure = null)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (backgroundJobClient is null)
        {
            throw new ArgumentNullException(nameof(backgroundJobClient));
        }

        if (storage is null)
        {
            throw new ArgumentNullException(nameof(storage));
        }

        if (job is null)
        {
            throw new ArgumentNullException(nameof(job));
        }

        var options = new HangfireJobStatusOperationsOptions();
        configure?.Invoke(options);

        if (string.IsNullOrWhiteSpace(options.ContinuationOperationName))
        {
            throw new ArgumentException(
                $"{nameof(HangfireJobStatusOperationsOptions.ContinuationOperationName)} must not be null or blank.",
                nameof(configure));
        }

        builder.Add(
            options.ContinuationOperationName,
            "Enqueues a follow-up background job that runs once the background job identified by " +
            "'parentJobId' reaches a matching state (succeeded, by default). Returns the continuation job's " +
            "own ID, which is created immediately but stays in the 'Awaiting' state until the parent " +
            "job finishes.",
            (Func<string, string>)(parentJobId => ContinueJobWith(backgroundJobClient, parentJobId, job, options)),
            operation =>
            {
                operation.Category = options.Category;
                operation.SafetyLevel = options.ContinuationSafetyLevel;
            });

        var statusOperationName = options.StatusOperationName;
        if (!string.IsNullOrWhiteSpace(statusOperationName))
        {
            builder.Add(
                statusOperationName!,
                "Looks up the current status of a background job by its Hangfire job ID, for example one " +
                "returned by enqueuing or continuing a job through this catalog. Returns null if no job with " +
                "that ID exists in storage (it may have already been expired and removed), but throws if " +
                "'jobId' itself is missing or blank.",
                (Func<string, HangfireJobStatus?>)(jobId => GetJobStatus(storage, jobId, options)),
                operation =>
                {
                    operation.Category = options.Category;
                    operation.SafetyLevel = options.StatusSafetyLevel;
                });
        }

        return builder;
    }

    private static string ContinueJobWith(
        IBackgroundJobClient backgroundJobClient,
        string parentJobId,
        Job job,
        HangfireJobStatusOperationsOptions options)
    {
        if (string.IsNullOrWhiteSpace(parentJobId))
        {
            throw new ArgumentException("A parent job ID is required.", nameof(parentJobId));
        }

        var awaitingState = new AwaitingState(parentJobId, options.NextState ?? new EnqueuedState(), options.ContinuationOptions);
        return backgroundJobClient.Create(job, awaitingState);
    }

    private static HangfireJobStatus? GetJobStatus(JobStorage storage, string jobId, HangfireJobStatusOperationsOptions options)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new ArgumentException("A job ID is required.", nameof(jobId));
        }

        using var connection = storage.GetConnection();
        var jobData = connection.GetJobData(jobId);
        if (jobData is null)
        {
            return null;
        }

        var dashboardUrl = options.DashboardBaseUrl is null
            ? null
            : $"{options.DashboardBaseUrl.TrimEnd('/')}/jobs/details/{jobId}";

        return new HangfireJobStatus(
            jobId,
            jobData.State,
            jobData.Job?.Type?.FullName,
            jobData.Job?.Method?.Name,
            jobData.CreatedAt,
            dashboardUrl);
    }
}
