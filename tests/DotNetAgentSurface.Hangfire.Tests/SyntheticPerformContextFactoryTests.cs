namespace DotNetAgentSurface.Hangfire.Tests;

public sealed class SyntheticPerformContextFactoryTests
{
    [Fact]
    public void Create_returns_a_context_backed_by_a_real_background_job_and_storage()
    {
        using var synthetic = SyntheticPerformContextFactory.Create();

        Assert.NotNull(synthetic.Context);
        Assert.NotNull(synthetic.Context.BackgroundJob);
        Assert.NotEmpty(synthetic.Context.BackgroundJob.Id);
        Assert.NotNull(synthetic.Context.Connection);
        Assert.False(synthetic.JobCancellationToken.ShutdownToken.IsCancellationRequested);
    }

    [Fact]
    public void Create_uses_the_supplied_job_id()
    {
        using var synthetic = SyntheticPerformContextFactory.Create(jobId: "custom-job-id");

        Assert.Equal("custom-job-id", synthetic.Context.BackgroundJob.Id);
    }

    [Fact]
    public void Create_surfaces_cancellation_through_the_shutdown_token()
    {
        using var cts = new CancellationTokenSource();
        using var synthetic = SyntheticPerformContextFactory.Create(cts.Token);

        Assert.False(synthetic.JobCancellationToken.ShutdownToken.IsCancellationRequested);

        cts.Cancel();

        Assert.True(synthetic.JobCancellationToken.ShutdownToken.IsCancellationRequested);
        Assert.Throws<OperationCanceledException>(() => synthetic.JobCancellationToken.ThrowIfCancellationRequested());
    }

    [Fact]
    public void Dispose_can_be_called_without_throwing()
    {
        var synthetic = SyntheticPerformContextFactory.Create();

        var exception = Record.Exception(synthetic.Dispose);

        Assert.Null(exception);
    }
}
