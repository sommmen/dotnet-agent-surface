using Hangfire.InMemory;
using Hangfire.Server;
using Hangfire.Storage;

namespace DotNetAgentSurface.Hangfire;

/// <summary>
/// A real Hangfire <see cref="Hangfire.Server.PerformContext"/> created by <see cref="SyntheticPerformContextFactory"/>,
/// together with the private in-memory storage and connection backing it. Dispose this once the job invocation
/// that used <see cref="Context"/> has completed.
/// </summary>
public sealed class SyntheticPerformContext(PerformContext context, InMemoryStorage storage, IStorageConnection connection) : IDisposable
{
    /// <summary>
    /// Gets the synthetic <see cref="Hangfire.Server.PerformContext"/>.
    /// </summary>
    public PerformContext Context { get; } = context;

    /// <summary>
    /// Gets the <see cref="Hangfire.IJobCancellationToken"/> supplied to <see cref="Context"/>, exposed directly so
    /// it can also be injected into job constructors that request it (or a plain <see cref="CancellationToken"/>)
    /// independently of <see cref="Context"/>.
    /// </summary>
    public global::Hangfire.IJobCancellationToken JobCancellationToken => Context.CancellationToken;

    /// <inheritdoc />
    public void Dispose()
    {
        connection.Dispose();
        storage.Dispose();
    }
}
