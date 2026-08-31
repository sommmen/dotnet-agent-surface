using DotNetAgentSurface.Core;
using DotNetAgentSurface.Hangfire;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.Storage;

// This sample demonstrates the Hangfire discovery satellite end to end: recurring jobs registered with
// Hangfire are cataloged as agent operations that trigger the job through IRecurringJobManager, without
// ever invoking the job method body directly. Hangfire.InMemory keeps the sample self-contained (no
// Redis/SQL Server backend required) so it can run with a plain `dotnet run`.
//
// Note: IRecurringJobManager.Trigger(jobId) only *enqueues* the job for execution; it does not run the
// job body inline. A real host would also run a BackgroundJobServer to dequeue and execute it. This
// sample stops at the enqueue step (verified via storage) and does not start a server, keeping the
// discovery satellite's contract obvious: the agent operation is "ask Hangfire to run this job now",
// not "run this job's code directly".
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
