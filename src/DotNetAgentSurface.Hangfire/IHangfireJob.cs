using Hangfire;

namespace DotNetAgentSurface.Hangfire;

/// <summary>
/// Marks a class-based Hangfire job as discoverable by
/// <see cref="HangfireJobRegistrationCatalogBuilderExtensions.RegisterJobs{TJobBase}"/> without requiring the type
/// (or an existing shared base class) to derive from <see cref="HangfireJob"/>. Implement this interface directly
/// on a pre-existing job base class — including a CRTP-style generic self-referencing base class — to adopt
/// discovery-based registration without rewriting the inheritance chain of every job that already derives from it.
/// The job type may declare any constructor, including one that takes a Hangfire <c>PerformContext</c> or
/// arbitrary DI-injected dependencies: discovery never constructs job instances itself, it only inspects types via
/// reflection and hands Hangfire the job type/method pair. Hangfire's own <see cref="JobActivator"/> — the
/// same activator already used for jobs with injected dependencies — creates the instance when the enqueued job
/// actually executes.
/// </summary>
public interface IHangfireJob
{
    /// <summary>Executes the job.</summary>
    Task ExecuteAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Marks a class-based Hangfire job that accepts agent-supplied input as discoverable by
/// <see cref="HangfireJobRegistrationCatalogBuilderExtensions.RegisterJobs{TJobBase, TOptions}"/> without requiring
/// the type (or an existing shared base class) to derive from <see cref="HangfireJobWithOptions{TOptions}"/>. See
/// <see cref="IHangfireJob"/> for how pre-existing/brownfield job hierarchies — including ones with constructor
/// parameters or CRTP-style generic self-references — can adopt this interface directly.
/// </summary>
/// <typeparam name="TOptions">The JSON-bindable input supplied when the job is queued.</typeparam>
public interface IHangfireJob<in TOptions>
{
    /// <summary>Executes the job with the supplied input.</summary>
    Task ExecuteAsync(TOptions options, CancellationToken cancellationToken);
}
