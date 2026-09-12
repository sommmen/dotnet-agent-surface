using System.Reflection;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.Server;
using Hangfire.Storage;

namespace DotNetAgentSurface.Hangfire;

/// <summary>
/// Creates a real, working Hangfire <see cref="PerformContext"/> outside of an actual Hangfire server, for tests
/// and tools that need to construct a class-based job whose constructor requires one (e.g. a job base class that
/// uses <c>PerformContext</c> for Hangfire.Console progress logging or job metadata). <see
/// cref="HangfireWorkflowTestCatalogBuilderExtensions.RegisterWorkflowTests{TJobBase}"/> and its overloads use this
/// factory automatically; call it directly only when constructing job instances outside of workflow-test
/// registration (for example, from a bespoke test harness).
/// </summary>
/// <remarks>
/// The returned context is backed by a private, in-process <see cref="InMemoryStorage"/> instance and a
/// placeholder <see cref="Job"/>/<see cref="Hangfire.BackgroundJob"/> pair, mirroring the shape Hangfire's own
/// <c>InjectContextJobActivator</c> produces for real background execution. It is not connected to any real job
/// queue, and nothing enqueued or written through it is persisted anywhere. Dispose the returned <see
/// cref="SyntheticPerformContext"/> once the job invocation that used it has completed.
/// </remarks>
public static class SyntheticPerformContextFactory
{
    private static readonly MethodInfo PlaceholderMethod = typeof(PlaceholderJobTarget).GetMethod(nameof(PlaceholderJobTarget.Noop))!;

    /// <summary>
    /// Creates a synthetic <see cref="PerformContext"/> whose <see cref="PerformContext.CancellationToken"/>
    /// reports cancellation via <paramref name="cancellationToken"/>.
    /// </summary>
    /// <param name="cancellationToken">
    /// The token surfaced through the context's <c>PerformContext.CancellationToken.ShutdownToken</c>. Defaults to
    /// <see cref="CancellationToken.None"/>.
    /// </param>
    /// <param name="jobId">
    /// The synthetic job's id, surfaced through <c>PerformContext.BackgroundJob.Id</c>. Defaults to a new GUID.
    /// </param>
    public static SyntheticPerformContext Create(CancellationToken cancellationToken = default, string? jobId = null)
    {
        var storage = new InMemoryStorage();
        IStorageConnection connection;
        try
        {
            connection = storage.GetConnection();
        }
        catch
        {
            storage.Dispose();
            throw;
        }

        try
        {
            var job = new Job(typeof(PlaceholderJobTarget), PlaceholderMethod);
            var backgroundJob = new global::Hangfire.BackgroundJob(jobId ?? Guid.NewGuid().ToString("N"), job, DateTime.UtcNow);
            var jobCancellationToken = new SyntheticJobCancellationToken(cancellationToken);
            var context = new PerformContext(storage, connection, backgroundJob, jobCancellationToken);
            return new SyntheticPerformContext(context, storage, connection);
        }
        catch
        {
            connection.Dispose();
            storage.Dispose();
            throw;
        }
    }

    // Declaring type/method pair for the placeholder Job. Job's constructor requires the supplied type to be
    // derived from the method's declaring type and the argument count to match the method's parameter count, so a
    // dedicated zero-argument static method keeps construction independent of whatever job type is actually
    // under test.
    private static class PlaceholderJobTarget
    {
        public static void Noop()
        {
        }
    }

    // Hangfire's own JobCancellationToken.Null returns a literal null, not a usable instance, so PerformContext
    // requires a real IJobCancellationToken implementation wrapping the caller-supplied token.
    private sealed class SyntheticJobCancellationToken(CancellationToken shutdownToken) : global::Hangfire.IJobCancellationToken
    {
        public CancellationToken ShutdownToken { get; } = shutdownToken;

        public void ThrowIfCancellationRequested() => ShutdownToken.ThrowIfCancellationRequested();
    }
}
