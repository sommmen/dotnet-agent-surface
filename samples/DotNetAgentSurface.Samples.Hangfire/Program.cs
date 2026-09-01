using DotNetAgentSurface.Core;
using DotNetAgentSurface.Hangfire;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.Storage;

// This sample demonstrates both Hangfire discovery satellites end to end:
//
//   1. Recurring-job discovery (AddHangfireRecurringJobs): jobs already registered with Hangfire's
//      recurring-job storage are cataloged as agent operations that trigger the job through
//      IRecurringJobManager, without ever invoking the job method body directly.
//   2. Class-based job-type discovery (AddHangfireJobTypes): concrete job classes are found via
//      reflection and cataloged as agent operations that enqueue a single background execution of
//      that job via IBackgroundJobClient.Create(...). This is the "not every job is a recurring job"
//      case — discovery here never creates a recurring-job entry (not even Cron.Never()); it behaves
//      exactly like calling BackgroundJob.Enqueue for the type, just picked up by reflection instead
//      of being hand-wired one type at a time.
//
// Hangfire.InMemory keeps the sample self-contained (no Redis/SQL Server backend required) so it can
// run with a plain `dotnet run`.
//
// Note: both IRecurringJobManager.Trigger(jobId) and IBackgroundJobClient.Create(...) only *enqueue*
// the job for execution; they do not run the job body inline. A real host would also run a
// BackgroundJobServer to dequeue and execute it. This sample stops at the enqueue step (verified via
// storage) and does not start a server, keeping each discovery satellite's contract obvious: the
// agent operation is "ask Hangfire to run this job now", not "run this job's code directly".
using var storage = new InMemoryStorage();
var jobManager = new RecurringJobManager(storage);

jobManager.AddOrUpdate("nightly-cleanup", Job.FromExpression(() => SampleJobs.CleanUp()), Cron.Daily());
jobManager.AddOrUpdate("hourly-report", Job.FromExpression(() => SampleJobs.SendReport()), Cron.Hourly());

var catalog = new OperationCatalogBuilder()
    .AddHangfireRecurringJobs(storage, jobManager, options =>
    {
        // Demonstrates per-job enrichment: the report job is downgraded to Safe since it has no side
        // effects beyond emitting a report, while the default Confirm level is left for nightly-cleanup.
        options.Enrich = (job, registration) =>
        {
            if (job.Id == "hourly-report")
            {
                registration.SafetyLevel = AgentSafetyLevel.Safe;
                registration.Description = "Triggers the hourly report job immediately, outside of its normal schedule.";
            }
        };
    })
    .Build();

Console.WriteLine("=== 1. Recurring-job discovery (AddHangfireRecurringJobs) ===");
Console.WriteLine($"Discovered {catalog.Operations.Count} Hangfire recurring job operation(s):");
foreach (var operation in catalog.Operations)
{
    Console.WriteLine($"  - {operation.Name} [{operation.Category}, {operation.SafetyLevel}]: {operation.Description}");
}

// Operations invoke jobManager.Trigger(jobId) as a static delegate, so no service provider lookups are
// required; NullServiceProvider only exists to satisfy the OperationInvoker constructor.
var invoker = new OperationInvoker(new NullServiceProvider());

Console.WriteLine();
Console.WriteLine("Triggering 'nightly-cleanup' on demand...");
var nightlyCleanup = catalog.Operations.Single(operation => operation.Name == "nightly-cleanup");
var result = await invoker.InvokeAsync(nightlyCleanup);
Console.WriteLine(result.Succeeded ? "  Enqueued successfully." : $"  Failed: {result.Error}");

Console.WriteLine("Triggering 'hourly-report' on demand...");
var hourlyReport = catalog.Operations.Single(operation => operation.Name == "hourly-report");
result = await invoker.InvokeAsync(hourlyReport);
Console.WriteLine(result.Succeeded ? "  Enqueued successfully." : $"  Failed: {result.Error}");

Console.WriteLine();
Console.WriteLine("Enqueued background jobs now waiting for a worker (none is running in this sample):");
var monitoring = storage.GetMonitoringApi();
foreach (var enqueuedJob in monitoring.EnqueuedJobs("default", 0, 20))
{
    Console.WriteLine($"  - job {enqueuedJob.Key}: {enqueuedJob.Value.Job.Method.Name}");
}

// === 2. Class-based job-type discovery (AddHangfireJobTypes) ===
//
// Not every job in a real application is wired up as a Hangfire recurring job — plenty of jobs only
// ever get enqueued on demand (e.g. from a controller action, another job, or here, an MCP tool call).
// AddHangfireJobTypes finds concrete classes that look like jobs by reflection and exposes each one as
// an operation that enqueues a single execution via IBackgroundJobClient, exactly like calling
// BackgroundJob.Enqueue<T>(x => x.Execute(...)) by hand. No recurring-job entry is created for these
// types — not even a Cron.Never() placeholder — so this satellite has zero effect on the recurring
// jobs registered above, and vice versa.
var backgroundJobClient = new BackgroundJobClient(storage);

var jobTypeCatalog = new OperationCatalogBuilder()
    .AddHangfireJobTypes(
        backgroundJobClient,
        [typeof(Program).Assembly],
        // Convention: any concrete class implementing one of the marker interfaces is a job. Discovery
        // is entirely caller-defined — a real app might instead match on an attribute or naming pattern.
        isJobType: type => typeof(ISampleJob).IsAssignableFrom(type) || typeof(ISampleJobWithOptions).IsAssignableFrom(type),
        // Convention: parameterless jobs expose ISampleJob.Execute(); options-based jobs expose
        // ISampleJobWithOptions.Execute(ArchiveOptions) instead.
        jobMethod: type => typeof(ISampleJob).IsAssignableFrom(type)
            ? type.GetMethod(nameof(ISampleJob.Execute))!
            : type.GetMethod(nameof(ISampleJobWithOptions.Execute))!,
        // Only options-based job types are asked for arguments; parameterless jobs get an empty array.
        argumentsFactory: type => typeof(ISampleJobWithOptions).IsAssignableFrom(type)
            ? [new ArchiveOptions(OlderThanDays: 90)]
            : [],
        configure: options =>
        {
            options.Category = "Ad-hoc jobs";

            // Demonstrates per-type enrichment and custom operation naming, mirroring what
            // AddHangfireRecurringJobs offers above.
            options.OperationNameFactory = type => $"job:{type.Name}";
            options.Enrich = (type, registration) =>
            {
                if (type == typeof(ArchiveOldRecordsJob))
                {
                    registration.SafetyLevel = AgentSafetyLevel.Dangerous;
                    registration.Description = "Archives records older than the configured retention window.";
                }
            };
        })
    .Build();

Console.WriteLine();
Console.WriteLine("=== 2. Class-based job-type discovery (AddHangfireJobTypes) ===");
Console.WriteLine($"Discovered {jobTypeCatalog.Operations.Count} Hangfire job-type operation(s):");
foreach (var operation in jobTypeCatalog.Operations)
{
    Console.WriteLine($"  - {operation.Name} [{operation.Category}, {operation.SafetyLevel}]: {operation.Description}");
}

Console.WriteLine();
Console.WriteLine("No jobs have been enqueued yet, and no recurring-job entries exist for these types");
Console.WriteLine($"(recurring jobs in storage: {storage.GetConnection().GetAllItemsFromSet("recurring-jobs").Count}, still just nightly-cleanup and hourly-report).");

Console.WriteLine();
Console.WriteLine("Invoking 'job:SendWelcomeEmailJob' (parameterless job) on demand...");
var welcomeEmail = jobTypeCatalog.Operations.Single(operation => operation.Name == "job:SendWelcomeEmailJob");
result = await invoker.InvokeAsync(welcomeEmail);
Console.WriteLine(result.Succeeded ? "  Enqueued successfully." : $"  Failed: {result.Error}");

Console.WriteLine("Invoking 'job:ArchiveOldRecordsJob' (options-based job) on demand...");
var archiveOldRecords = jobTypeCatalog.Operations.Single(operation => operation.Name == "job:ArchiveOldRecordsJob");
result = await invoker.InvokeAsync(archiveOldRecords);
Console.WriteLine(result.Succeeded ? "  Enqueued successfully." : $"  Failed: {result.Error}");

Console.WriteLine();
Console.WriteLine("Enqueued background jobs now waiting for a worker (none is running in this sample):");
foreach (var enqueuedJob in storage.GetMonitoringApi().EnqueuedJobs("default", 0, 20))
{
    var jobArgs = enqueuedJob.Value.Job.Args.Count > 0
        ? $" (args: {string.Join(", ", enqueuedJob.Value.Job.Args)})"
        : string.Empty;
    Console.WriteLine($"  - job {enqueuedJob.Key}: {enqueuedJob.Value.Job.Type.Name}.{enqueuedJob.Value.Job.Method.Name}{jobArgs}");
}

Console.WriteLine();
Console.WriteLine($"Recurring jobs in storage are still unchanged: {storage.GetConnection().GetAllItemsFromSet("recurring-jobs").Count} " +
    "(AbstractExcludedJob was skipped because it is abstract, and neither discovered type created a recurring entry).");

/// <summary>Static job methods stand in for real recurring jobs; Hangfire jobs are typically static or DI-resolved.</summary>
internal static class SampleJobs
{
    public static void CleanUp() => Console.WriteLine("  [job] nightly-cleanup executed.");

    public static void SendReport() => Console.WriteLine("  [job] hourly-report executed.");
}

/// <summary>Satisfies <see cref="OperationInvoker"/>'s constructor when every operation is a bound/static delegate.</summary>
internal sealed class NullServiceProvider : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}

/// <summary>Marker convention for parameterless class-based jobs, used by this sample's
/// <c>AddHangfireJobTypes</c> discovery predicate. A real application is free to use any convention it
/// likes (an attribute, a naming pattern, a base class, etc.) — the library itself has no opinion on
/// what makes something a "job".</summary>
internal interface ISampleJob
{
    void Execute();
}

/// <summary>Marker convention for class-based jobs that require an options argument.</summary>
internal interface ISampleJobWithOptions
{
    void Execute(ArchiveOptions options);
}

/// <summary>A parameterless class-based job, instantiated by Hangfire's job activator when it runs.</summary>
internal sealed class SendWelcomeEmailJob : ISampleJob
{
    public void Execute() => Console.WriteLine("  [job] SendWelcomeEmailJob executed.");
}

/// <summary>Options passed to <see cref="ArchiveOldRecordsJob.Execute"/>; supplied by the sample's
/// <c>argumentsFactory</c> delegate rather than guessed by the discovery library.</summary>
internal sealed record ArchiveOptions(int OlderThanDays);

/// <summary>An options-based class-based job.</summary>
internal sealed class ArchiveOldRecordsJob : ISampleJobWithOptions
{
    public void Execute(ArchiveOptions options) =>
        Console.WriteLine($"  [job] ArchiveOldRecordsJob executed (older than {options.OlderThanDays} days).");
}

/// <summary>Abstract job types are excluded automatically, even when they satisfy the discovery
/// predicate — only concrete, closed classes can be enqueued.</summary>
internal abstract class AbstractExcludedJob : ISampleJob
{
    public abstract void Execute();
}
