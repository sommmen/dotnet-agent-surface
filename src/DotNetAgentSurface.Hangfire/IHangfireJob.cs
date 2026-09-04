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
/// <para>
/// <see cref="ExecuteAsync"/> must be implemented as a public method (implicit interface implementation).
/// Discovery only enumerates a job type's public <c>Execute</c>/<c>ExecuteAsync</c> methods via reflection, so an
/// explicit interface implementation (<c>Task IHangfireJob.ExecuteAsync(...)</c>) will not be found.
/// </para>
/// </summary>
public interface IHangfireJob
{
    /// <summary>
    /// Executes the job. Implement this as a public method — an explicit interface implementation is not
    /// discoverable by <see cref="HangfireJobRegistrationCatalogBuilderExtensions.RegisterJobs{TJobBase}"/>.
    /// </summary>
    Task ExecuteAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Marks a class-based Hangfire job that accepts agent-supplied input as discoverable by
/// <see cref="HangfireJobRegistrationCatalogBuilderExtensions.RegisterJobs{TJobBase, TOptions}"/> without requiring
/// the type (or an existing shared base class) to derive from <see cref="HangfireJobWithOptions{TOptions}"/>. See
/// <see cref="IHangfireJob"/> for how pre-existing/brownfield job hierarchies — including ones with constructor
/// parameters or CRTP-style generic self-references — can adopt this interface directly, and for the requirement
/// that <see cref="ExecuteAsync"/> be implemented as a public (not explicit interface implementation) method.
/// </summary>
/// <typeparam name="TOptions">The JSON-bindable input supplied when the job is queued.</typeparam>
/// <remarks>
/// <typeparamref name="TOptions"/> is intentionally invariant (not <c>in TOptions</c>). Reflection-based
/// assignability checks (<see cref="Type.IsAssignableFrom"/>), which is what discovery uses, treat a
/// contravariant interface's variance-compatible closed generic types as assignable to each other — e.g. a job
/// implementing <c>IHangfireJob&lt;BaseOptions&gt;</c> would be reported assignable to
/// <c>IHangfireJob&lt;DerivedOptions&gt;</c> — which would let discovery match the wrong <typeparamref
/// name="TOptions"/> for a job and misidentify its execution method. Keeping the parameter invariant ensures a
/// job type is only discovered for the exact <typeparamref name="TOptions"/> it implements.
/// </remarks>
public interface IHangfireJob<TOptions>
{
    /// <summary>
    /// Executes the job with the supplied input. Implement this as a public method — an explicit interface
    /// implementation is not discoverable by
    /// <see cref="HangfireJobRegistrationCatalogBuilderExtensions.RegisterJobs{TJobBase, TOptions}"/>.
    /// </summary>
    Task ExecuteAsync(TOptions options, CancellationToken cancellationToken);
}
