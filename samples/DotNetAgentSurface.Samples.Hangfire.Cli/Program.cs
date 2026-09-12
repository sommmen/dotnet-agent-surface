using DotNetAgentSurface.CommandLine;
using DotNetAgentSurface.Core;
using DotNetAgentSurface.Hangfire;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.Server;
using Microsoft.Extensions.Logging;

using var storage = new InMemoryStorage();
var jobManager = new RecurringJobManager(storage);
jobManager.AddOrUpdate("nightly-cleanup", Job.FromExpression(() => SampleJobs.CleanUp()), Cron.Daily());
jobManager.AddOrUpdate("hourly-report", Job.FromExpression(() => SampleJobs.SendReport()), Cron.Hourly());

var catalog = new OperationCatalogBuilder()
    .UseXmlDocumentation()
    .AddHangfireRecurringOperations(storage, jobManager)
    // Runs class-based jobs directly (no BackgroundJobServer/storage round-trip) and captures their full
    // ILogger transcript, so an agent can debug a job's behavior without spinning up the whole host app.
    // See docs/development/testing-and-open-decisions.md item 27 for the design rationale.
    .RegisterWorkflowTests<WorkflowJob, WorkflowJobOptions>(new NullServiceProvider(), [typeof(DamageSyncJob).Assembly], configure: options =>
    {
        options.Timeout = TimeSpan.FromMinutes(2);
        // When set, each run's transcript is also persisted to disk so it can be attached/retrieved as an
        // artifact after the process exits, e.g. from a CI job or a long-lived agent workspace.
        options.ArtifactDirectory = Environment.GetEnvironmentVariable("HANGFIRE_WORKFLOW_TEST_ARTIFACT_DIR");
    })
    // Demonstrates a job base class whose constructor requires a Hangfire PerformContext (e.g. for
    // Hangfire.Console-style progress logging or job metadata) — the workflow-test harness synthesizes a
    // real PerformContext automatically, so no bespoke IServiceProvider is needed even here.
    .RegisterWorkflowTests<PerformContextWorkflowJob>(new NullServiceProvider(), [typeof(ArchiveOldRecordsJob).Assembly])
    .Build();
var invoker = new OperationInvoker(
    new NullServiceProvider(),
    policies: [new DangerousOperationConfirmationPolicy()]);
var adapter = new OperationCommandLineAdapter(catalog, invoker);

var result = SkillGeneratorCommand.CanHandle(args)
    ? await SkillGeneratorCommand.ExecuteAsync(args, catalog, outputDirectoryDefault: "skill")
    : await adapter.ExecuteAsync(args);

if (!string.IsNullOrEmpty(result.Output))
{
    Console.Out.WriteLine(result.Output);
}

if (!string.IsNullOrEmpty(result.Error))
{
    Console.Error.WriteLine(result.Error);
}

return result.ExitCode;

internal static class SampleJobs
{
    public static void CleanUp()
    {
    }

    public static void SendReport()
    {
    }
}

internal sealed class NullServiceProvider : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}

/// <summary>
/// Shared base class for jobs registered via <see cref="RegisterWorkflowTests{TJobBase, TOptions}"/> in this
/// sample. Any pre-existing job base class works here too — see <see cref="IHangfireJob{TOptions}"/>.
/// </summary>
internal abstract class WorkflowJob : HangfireJobWithOptions<WorkflowJobOptions>;

/// <summary>
/// Agent-supplied input for <see cref="DamageSyncJob"/>, mirroring the shape of a typical "sync everything
/// changed since X" job: <c>hangfire-cli-sample damage-sync-job --till-ago "00:15:00"</c>.
/// </summary>
internal sealed record WorkflowJobOptions(TimeSpan TillAgo);

/// <summary>
/// Stand-in for a real, larger sync job (e.g. the "damage sync" job referenced in this feature's design
/// discussion). Demonstrates constructor-injected <see cref="ILogger{TCategoryName}"/> — the workflow-test
/// harness activates this type via <c>ActivatorUtilities</c> even without a full DI container registered,
/// and captures everything logged here into the returned transcript/artifact.
/// </summary>
internal sealed class DamageSyncJob(ILogger<DamageSyncJob> logger) : WorkflowJob
{
    public override async Task ExecuteAsync(WorkflowJobOptions options, CancellationToken cancellationToken)
    {
        var since = DateTimeOffset.UtcNow - options.TillAgo;
        logger.LogInformation("Starting damage sync for changes since {Since:O}.", since);

        for (var batch = 1; batch <= 3; batch++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            logger.LogInformation("Synced batch {Batch} of 3.", batch);
        }

        logger.LogInformation("Damage sync completed.");
    }
}

/// <summary>
/// Shared base class for a job that requires a Hangfire <see cref="PerformContext"/> in its constructor — a
/// common pattern for jobs that want Hangfire.Console-style progress logging or job metadata (id, creation
/// time) alongside their regular DI dependencies. <see cref="RegisterWorkflowTests{TJobBase}"/> synthesizes a
/// real (but otherwise unconnected) <see cref="PerformContext"/> for this constructor parameter automatically;
/// see <see cref="SyntheticPerformContextFactory"/> for details.
/// </summary>
internal abstract class PerformContextWorkflowJob : HangfireJob;

/// <summary>
/// Stand-in for a job that logs its synthesized <see cref="PerformContext"/>'s job id, proving the
/// workflow-test harness constructs <see cref="PerformContext"/>-requiring jobs without any bespoke
/// <see cref="IServiceProvider"/> from the host.
/// </summary>
internal sealed class ArchiveOldRecordsJob(PerformContext context, ILogger<ArchiveOldRecordsJob> logger) : PerformContextWorkflowJob
{
    public override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Archiving old records for synthesized job {JobId}.", context.BackgroundJob.Id);
        return Task.CompletedTask;
    }
}
