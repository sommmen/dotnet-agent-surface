namespace DotNetAgentSurface.Hangfire;

/// <summary>
/// Base type for a class-based Hangfire job without agent-supplied input.
/// </summary>
public abstract class HangfireJob
{
    /// <summary>
    /// Executes the job.
    /// </summary>
    public abstract Task ExecuteAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Base type for a class-based Hangfire job with agent-supplied input.
/// </summary>
/// <typeparam name="TOptions">The JSON-bindable input supplied when the job is queued.</typeparam>
public abstract class HangfireJobWithOptions<TOptions>
{
    /// <summary>
    /// Executes the job with the supplied input.
    /// </summary>
    public abstract Task ExecuteAsync(TOptions options, CancellationToken cancellationToken);
}
