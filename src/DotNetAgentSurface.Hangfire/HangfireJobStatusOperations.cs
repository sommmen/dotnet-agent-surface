using System.Reflection;
using DotNetAgentSurface.Core;
using Hangfire;
using Hangfire.Common;
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

    /// <summary>
    /// Gets or sets the name of the operation added for chaining a follow-up job. Defaults to
    /// <c>"continue-hangfire-job"</c>. Override this (and typically <see cref="StatusOperationName"/>) when
    /// calling <c>AddHangfireJobStatusOperations</c> more than once in the same catalog — for example to
    /// expose several distinct continuation targets — since every operation in a catalog must have a unique
    /// name; registering the default name twice throws when the catalog is built.
    /// </summary>
    public string ContinuationOperationName { get; set; } = "continue-hangfire-job";

    /// <summary>
    /// Gets or sets the name of the operation added for looking up a job's status. Set to null to skip adding it,
    /// or to a non-blank string to register an operation with that name. Defaults to <c>"get-hangfire-job-status"</c>.
    /// When calling <c>AddHangfireJobStatusOperations</c> more than once to expose several continuation targets, set
    /// this to null on every call after the first — a single shared <c>get-hangfire-job-status</c> operation works
    /// for any job ID regardless of which call registered the continuation used to create it, and re-adding it under
    /// the same default name on a second call throws an <see cref="OperationCatalogException"/> when the catalog is
    /// built. When non-null, the value must not be blank or contain only whitespace; blank values throw
    /// <see cref="ArgumentException"/>.
    /// </summary>
    public string? StatusOperationName { get; set; } = "get-hangfire-job-status";
}

/// <summary>
/// Builds the continuation <see cref="Job"/> passed to
/// <see cref="HangfireJobStatusOperationCatalogBuilderExtensions.AddHangfireJobStatusOperations"/>, using the
/// same "find the public Execute/ExecuteAsync method by convention" discovery that
/// <see cref="HangfireJobRegistrationCatalogBuilderExtensions.RegisterJobs{TJobBase}"/> uses internally, so
/// callers do not need to hand-roll <see cref="Type.GetMethod(string, Type[])"/> plus a null-coalescing throw
/// for every continuation target.
/// </summary>
public static class HangfireJobStatusOperations
{
    /// <summary>
    /// Builds a <see cref="Job"/> that runs <typeparamref name="TJob"/>'s parameterless
    /// <c>Execute(CancellationToken)</c> or <c>ExecuteAsync(CancellationToken)</c> method with
    /// <see cref="CancellationToken.None"/>, suitable as the continuation target passed to
    /// <see cref="HangfireJobStatusOperationCatalogBuilderExtensions.AddHangfireJobStatusOperations"/>.
    /// </summary>
    /// <typeparam name="TJob">
    /// The job type to continue with. Must declare exactly one public instance <c>Execute</c> or
    /// <c>ExecuteAsync</c> method accepting only a <see cref="CancellationToken"/>.
    /// </typeparam>
    /// <exception cref="InvalidOperationException">
    /// No matching method was found, or more than one was found, on <typeparamref name="TJob"/>.
    /// </exception>
    public static Job ForJob<TJob>()
        where TJob : class
    {
        var method = SelectExecutionMethod(typeof(TJob), optionsType: null);
        return new Job(typeof(TJob), method, new object?[] { CancellationToken.None });
    }

    /// <summary>
    /// Builds a <see cref="Job"/> that runs <typeparamref name="TJob"/>'s options-based
    /// <c>Execute(TOptions, CancellationToken)</c> or <c>ExecuteAsync(TOptions, CancellationToken)</c> method
    /// with the supplied <paramref name="options"/> and <see cref="CancellationToken.None"/>, suitable as the
    /// continuation target passed to
    /// <see cref="HangfireJobStatusOperationCatalogBuilderExtensions.AddHangfireJobStatusOperations"/>.
    /// </summary>
    /// <typeparam name="TJob">
    /// The job type to continue with. Must declare exactly one public instance <c>Execute</c> or
    /// <c>ExecuteAsync</c> method accepting a <typeparamref name="TOptions"/> followed by a
    /// <see cref="CancellationToken"/>.
    /// </typeparam>
    /// <typeparam name="TOptions">The options type accepted by the job's execution method.</typeparam>
    /// <param name="options">
    /// The options value the continuation job is created with. Hangfire serializes this into the stored job
    /// arguments immediately; it has no bearing on when the continuation actually starts (that is governed by
    /// <see cref="HangfireJobStatusOperationsOptions.ContinuationOptions"/> and
    /// <see cref="HangfireJobStatusOperationsOptions.NextState"/>). Defaults to <c>default(TOptions)</c>
    /// (typically null for reference types) when omitted, matching the placeholder value consumers otherwise
    /// pass by hand.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// No matching method was found, or more than one was found, on <typeparamref name="TJob"/>.
    /// </exception>
    public static Job ForJob<TJob, TOptions>(TOptions? options = default)
        where TJob : class
    {
        var method = SelectExecutionMethod(typeof(TJob), typeof(TOptions));
        return new Job(typeof(TJob), method, new object?[] { options, CancellationToken.None });
    }

    private static MethodInfo SelectExecutionMethod(Type jobType, Type? optionsType)
    {
        var candidates = jobType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => (method.Name == "Execute" || method.Name == "ExecuteAsync") && IsValidExecutionMethod(method, jobType, optionsType))
            .OrderBy(method => method.Name == "ExecuteAsync" ? 0 : 1)
            .ThenBy(method => method.MetadataToken)
            .ToArray();

        var shape = optionsType is null ? "(CancellationToken)" : $"({optionsType.Name}, CancellationToken)";

        if (candidates.Length == 0)
        {
            throw new InvalidOperationException($"'{jobType.FullName}' has no public Execute or ExecuteAsync{shape} method.");
        }

        if (candidates.Length > 1)
        {
            throw new InvalidOperationException(
                $"'{jobType.FullName}' has multiple public Execute/ExecuteAsync{shape} methods; " +
                $"construct the continuation {nameof(Job)} explicitly instead of using {nameof(HangfireJobStatusOperations)}.{nameof(ForJob)}.");
        }

        return candidates[0];
    }

    private static bool IsValidExecutionMethod(MethodInfo method, Type jobType, Type? optionsType)
    {
        if (!method.IsPublic || method.IsStatic || method.ContainsGenericParameters || !method.DeclaringType!.IsAssignableFrom(jobType))
        {
            return false;
        }

        var parameters = method.GetParameters();
        if (optionsType is null)
        {
            return parameters.Length == 1 && parameters[0].ParameterType == typeof(CancellationToken);
        }

        return parameters.Length == 2 && parameters[0].ParameterType == optionsType && parameters[1].ParameterType == typeof(CancellationToken);
    }
}
