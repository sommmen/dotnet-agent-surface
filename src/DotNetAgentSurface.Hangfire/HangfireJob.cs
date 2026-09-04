namespace DotNetAgentSurface.Hangfire;

/// <summary>
/// Base type for a class-based Hangfire job without agent-supplied input. New job classes should prefer this base
/// class; pre-existing job hierarchies that cannot derive from it should implement <see cref="IHangfireJob"/>
/// directly instead (see <see cref="HangfireJobRegistrationCatalogBuilderExtensions.RegisterJobs{TJobBase}"/>).
/// </summary>
public abstract class HangfireJob : IHangfireJob
{
    /// <summary>
    /// Executes the job.
    /// </summary>
    public abstract Task ExecuteAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Base type for a class-based Hangfire job with agent-supplied input. New job classes should prefer this base
/// class; pre-existing job hierarchies that cannot derive from it should implement
/// <see cref="IHangfireJob{TOptions}"/> directly instead (see
/// <see cref="HangfireJobRegistrationCatalogBuilderExtensions.RegisterJobs{TJobBase, TOptions}"/>).
/// </summary>
/// <typeparam name="TOptions">The JSON-bindable input supplied when the job is queued.</typeparam>
public abstract class HangfireJobWithOptions<TOptions> : IHangfireJob<TOptions>
{
    /// <summary>
    /// Executes the job with the supplied input.
    /// </summary>
    public abstract Task ExecuteAsync(TOptions options, CancellationToken cancellationToken);
}
