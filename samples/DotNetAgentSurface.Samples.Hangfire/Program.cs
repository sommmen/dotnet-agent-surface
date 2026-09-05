using System.Text.Json;
using DotNetAgentSurface.Core;
using DotNetAgentSurface.Hangfire;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.Storage;

// This sample demonstrates four Hangfire discovery satellites end to end:
//
//   1. Stable recurring-job operations (AddHangfireRecurringOperations): rather than cataloging one
//      operation per job at startup, this registers exactly two stable operations —
//      list-recurring-hangfire and trigger-recurring-hangfire — that query Hangfire's recurring-job
//      storage at invocation time. Adding, removing, or renaming recurring jobs never requires
//      rebuilding the catalog. Triggering defaults to enqueueing on the application's configured
//      storage (an acknowledgement only, never a completion claim); an opt-in isolated execution
//      model is also demonstrated further below.
//   2. Class-based job-type discovery (AddHangfireJobTypes): concrete job classes are found via
//      reflection and cataloged as agent operations that enqueue a single background execution of
//      that job via IBackgroundJobClient.Create(...). This is the "not every job is a recurring job"
//      case — discovery here never creates a recurring-job entry (not even Cron.Never()); it behaves
//      exactly like calling BackgroundJob.Enqueue for the type, just picked up by reflection instead
//      of being hand-wired one type at a time.
//   3. Attributed base-class job discovery (RegisterJobs<TJobBase>() / RegisterJobs<TJobBase, TOptions>()):
//      concrete classes deriving from HangfireJob or HangfireJobWithOptions<TOptions> are discovered
//      automatically — no caller-supplied predicate or method selector is required for the common case.
//      Options-based jobs get an automatically generated JSON input schema and binding, reusing the same
//      OperationCatalogBuilder.Add(...) reflection machinery as any other operation.
//   4. Job continuation and status lookup (AddHangfireJobStatusOperations): continue-hangfire-job
//      enqueues a follow-up job that only starts once a given parent job ID succeeds, and
//      get-hangfire-job-status reports a job's current state (and, if configured, its dashboard URL)
//      by ID. Both operations work with a job ID returned by any of the satellites above.
//
// Hangfire.InMemory keeps the sample self-contained (no Redis/SQL Server backend required) so it can
// run with a plain `dotnet run`.
//
// Note: both trigger-recurring-hangfire's default mode and IBackgroundJobClient.Create(...) only
// *enqueue* the job for execution; they do not run the job body inline. A real host would also run a
// BackgroundJobServer to dequeue and execute it. This sample stops at the enqueue step (verified via
// storage) and does not start a server for those cases, keeping each discovery satellite's contract
// obvious: the agent operation is "ask Hangfire to run this job now", not "run this job's code
// directly" — except where the isolated execution model is explicitly demonstrated below.
using var storage = new InMemoryStorage();
var jobManager = new RecurringJobManager(storage);

jobManager.AddOrUpdate("nightly-cleanup", Job.FromExpression(() => SampleJobs.CleanUp()), Cron.Daily());
jobManager.AddOrUpdate("hourly-report", Job.FromExpression(() => SampleJobs.SendReport()), Cron.Hourly());

var catalog = new OperationCatalogBuilder()
    .AddHangfireRecurringOperations(storage, jobManager)
    .Build();

Console.WriteLine("=== 1. Stable recurring-job operations (AddHangfireRecurringOperations) ===");
Console.WriteLine($"Registered {catalog.Operations.Count} stable Hangfire operation(s) (independent of how many recurring jobs exist):");
foreach (var operation in catalog.Operations)
{
    Console.WriteLine($"  - {operation.Name} [{operation.Category}, {operation.SafetyLevel}]: {operation.Description}");
}

// Operations resolve storage/manager dependencies from static delegates, so no service provider
// lookups are required; NullServiceProvider only exists to satisfy the OperationInvoker constructor.
//
// Every operation registered by this sample defaults to AgentSafetyLevel.Confirm or higher (trigger-
// recurring-hangfire, the AddHangfireJobTypes discoveries, and RegisterJobs<>()). That SafetyLevel is
// metadata only: OperationInvoker does not enforce it by itself, so a policy implementing
// IConfirmationEnforcingPolicy (here, DangerousOperationConfirmationPolicy) must be supplied, or
// OperationInvoker throws ConfirmationPolicyMissingException instead of silently allowing unconfirmed
// invocations. This sample auto-approves every confirmation for demonstration purposes only; a real
// host should gate the callback on genuine user/operator approval.
var invoker = new OperationInvoker(
    new NullServiceProvider(),
    policies: [new DangerousOperationConfirmationPolicy((_, _, _) => ValueTask.FromResult(true))]);

Console.WriteLine();
Console.WriteLine("Listing recurring jobs currently in storage...");
var list = catalog.Operations.Single(operation => operation.Name == "list-recurring-hangfire");
var listResult = await invoker.InvokeAsync(list);
foreach (var job in (IReadOnlyList<RecurringHangfireJobInfo>)listResult.Value!)
{
    Console.WriteLine($"  - {job.JobId}: {job.JobType}.{job.Method} ({job.Cron})");
}

Console.WriteLine();
Console.WriteLine("Triggering 'nightly-cleanup' on demand (enqueues on configured storage)...");
var trigger = catalog.Operations.Single(operation => operation.Name == "trigger-recurring-hangfire");
var result = await invoker.InvokeAsync(trigger, TriggerInputs.JobId("nightly-cleanup"));
if (result.Succeeded)
{
    var triggerResult = (TriggerRecurringHangfireResult)result.Value!;
    Console.WriteLine($"  Status: {triggerResult.Status} (enqueue id: {triggerResult.EnqueueId ?? "n/a"}).");
}
else
{
    Console.WriteLine($"  Failed: {result.Error}");
}

Console.WriteLine();
Console.WriteLine("Enqueued background jobs now waiting for a worker (none is running in this sample):");
var monitoring = storage.GetMonitoringApi();
foreach (var enqueuedJob in monitoring.EnqueuedJobs("default", 0, 20))
{
    Console.WriteLine($"  - job {enqueuedJob.Key}: {enqueuedJob.Value.Job.Method.Name}");
}

Console.WriteLine();
Console.WriteLine("Requesting an unknown job id (rejected without ever calling Hangfire)...");
result = await invoker.InvokeAsync(trigger, TriggerInputs.JobId("does-not-exist"));
var unknownTrigger = (TriggerRecurringHangfireResult)result.Value!;
Console.WriteLine($"  Status: {unknownTrigger.Status}.");

// The isolated execution model is an explicit opt-in for short-lived CLI/local scenarios: it creates a
// separate in-memory Hangfire storage/server, enqueues the job through Hangfire's normal activation
// pipeline, waits for the job to finish, and reports completion or failure. It never touches the
// application's configured storage (the 'hourly-report' recurring-job entry above is left untouched).
var isolatedCatalog = new OperationCatalogBuilder()
    .AddHangfireRecurringOperations(storage, jobManager, options =>
    {
        options.ExecutionModel = HangfireExecutionModel.ExecuteUsingIsolatedInMemoryServer;
        options.IsolatedExecutionTimeout = TimeSpan.FromSeconds(30);
    })
    .Build();

Console.WriteLine();
Console.WriteLine("Triggering 'hourly-report' with the isolated in-memory execution model...");
var isolatedTrigger = isolatedCatalog.Operations.Single(operation => operation.Name == "trigger-recurring-hangfire");
result = await invoker.InvokeAsync(isolatedTrigger, TriggerInputs.JobId("hourly-report"));
if (result.Succeeded)
{
    var isolatedResult = (TriggerRecurringHangfireResult)result.Value!;
    Console.WriteLine($"  Status: {isolatedResult.Status} (ran to completion on an isolated in-memory server).");
}
else
{
    Console.WriteLine($"  Failed: {result.Error}");
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
// AddHangfireJobTypes/RegisterJobs register operations returning the enqueued Hangfire job ID (a
// string), not null, so callers can chain a continuation or poll status with it (see part 4 below).
var welcomeEmailJobId = result.Succeeded ? (string?)result.Value : null;
Console.WriteLine(result.Succeeded ? $"  Enqueued successfully as job {welcomeEmailJobId}." : $"  Failed: {result.Error}");

Console.WriteLine("Invoking 'job:ArchiveOldRecordsJob' (options-based job) on demand...");
var archiveOldRecords = jobTypeCatalog.Operations.Single(operation => operation.Name == "job:ArchiveOldRecordsJob");
result = await invoker.InvokeAsync(archiveOldRecords);
Console.WriteLine(result.Succeeded ? $"  Enqueued successfully as job {result.Value}." : $"  Failed: {result.Error}");

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

var attributedCatalog = new OperationCatalogBuilder()
    .RegisterJobs<SendDigestJob>(backgroundJobClient, [typeof(Program).Assembly])
    .RegisterJobs<PurgeCacheJobBase, PurgeCacheOptions>(backgroundJobClient, [typeof(Program).Assembly])
    .Build();

Console.WriteLine();
Console.WriteLine("=== 3. Attributed base-class job discovery (RegisterJobs<TJobBase>()) ===");
Console.WriteLine($"Discovered {attributedCatalog.Operations.Count} Hangfire job operation(s) from HangfireJob/HangfireJobWithOptions<> base types:");
foreach (var operation in attributedCatalog.Operations)
{
    Console.WriteLine($"  - {operation.Name} [{operation.Category}, {operation.SafetyLevel}]: {operation.Description}");
}

Console.WriteLine();
Console.WriteLine("Invoking 'send-digest-job' (parameterless HangfireJob) on demand...");
var sendDigest = attributedCatalog.Operations.Single(operation => operation.Name == "send-digest-job");
result = await invoker.InvokeAsync(sendDigest);
Console.WriteLine(result.Succeeded ? "  Enqueued successfully." : $"  Failed: {result.Error}");

Console.WriteLine("Invoking 'purge-cache-job' with JSON-bound options on demand...");
var purgeCache = attributedCatalog.Operations.Single(operation => operation.Name == "purge-cache-job");
var purgeInputs = new Dictionary<string, JsonElement>
{
    ["options"] = JsonDocument.Parse("""{"olderThanDays":30}""").RootElement.Clone()
};
result = await invoker.InvokeAsync(purgeCache, purgeInputs);
Console.WriteLine(result.Succeeded ? "  Enqueued successfully." : $"  Failed: {result.Error}");

// === 4. Chaining a follow-up job and polling status (AddHangfireJobStatusOperations) ===
//
// continue-hangfire-job and get-hangfire-job-status are independent of how the parent job was
// enqueued: any job ID returned by AddHangfireJobTypes, RegisterJobs<>(), or trigger-recurring-hangfire
// works as the parentId/jobId input below.
//
// HangfireJobStatusOperations.ForJob<TJob>() builds the continuation Job via the same "find the
// public Execute/ExecuteAsync method by convention" discovery RegisterJobs<TJobBase>() uses
// internally, so no manual GetMethod(...)-plus-null-check boilerplate is needed here.
var digestFollowUpJob = HangfireJobStatusOperations.ForJob<SendDigestJob>();
var statusCatalog = new OperationCatalogBuilder()
    .AddHangfireJobStatusOperations(backgroundJobClient, storage, digestFollowUpJob, options =>
    {
        options.Category = "Ad-hoc jobs";
        // No public Hangfire API exposes the dashboard's mounted base path at runtime, so callers
        // supply it explicitly if a dashboard is hosted; omit this to skip reporting a URL.
        options.DashboardBaseUrl = "https://ops.example.com/hangfire";
    })
    .Build();

Console.WriteLine();
Console.WriteLine("=== 4. Chaining a follow-up job and polling status (AddHangfireJobStatusOperations) ===");
Console.WriteLine($"Discovered {statusCatalog.Operations.Count} Hangfire job-status operation(s):");
foreach (var operation in statusCatalog.Operations)
{
    Console.WriteLine($"  - {operation.Name} [{operation.Category}, {operation.SafetyLevel}]: {operation.Description}");
}

var jobStatus = statusCatalog.Operations.Single(operation => operation.Name == "get-hangfire-job-status");
if (welcomeEmailJobId is null)
{
    // continue-hangfire-job/get-hangfire-job-status both reject a missing job ID rather than
    // silently treating it as "not found" (see HangfireJobStatusOperationCatalogBuilderExtensions),
    // so the demo below only runs when the earlier enqueue actually returned an ID.
    Console.WriteLine();
    Console.WriteLine("Skipping the continuation/status demo: no welcome-email job ID is available " +
        "because the earlier enqueue failed.");
}
else
{
    Console.WriteLine();
    Console.WriteLine($"Enqueuing a follow-up job to run once job {welcomeEmailJobId} succeeds...");
    var continueJob = statusCatalog.Operations.Single(operation => operation.Name == "continue-hangfire-job");
    var continueInputs = new Dictionary<string, JsonElement>
    {
        ["parentJobId"] = JsonDocument.Parse(JsonSerializer.Serialize(welcomeEmailJobId)).RootElement.Clone()
    };
    result = await invoker.InvokeAsync(continueJob, continueInputs);
    var continuationJobId = result.Succeeded ? (string?)result.Value : null;
    Console.WriteLine(result.Succeeded ? $"  Enqueued continuation as job {continuationJobId}." : $"  Failed: {result.Error}");

    Console.WriteLine();
    Console.WriteLine($"Looking up the status of job {welcomeEmailJobId}...");
    var jobStatusInputs = new Dictionary<string, JsonElement>
    {
        ["jobId"] = JsonDocument.Parse(JsonSerializer.Serialize(welcomeEmailJobId)).RootElement.Clone()
    };
    result = await invoker.InvokeAsync(jobStatus, jobStatusInputs);
    if (result is { Succeeded: true, Value: HangfireJobStatus status })
    {
        Console.WriteLine($"  State: {status.State}, dashboard: {status.DashboardUrl ?? "n/a"}.");
    }
    else
    {
        Console.WriteLine(result.Succeeded ? "  Job not found." : $"  Failed: {result.Error}");
    }
}

Console.WriteLine();
Console.WriteLine("Looking up an unknown job id (returns null instead of throwing)...");
var unknownStatusInputs = new Dictionary<string, JsonElement>
{
    ["jobId"] = JsonDocument.Parse("""  "does-not-exist"  """).RootElement.Clone()
};
result = await invoker.InvokeAsync(jobStatus, unknownStatusInputs);
Console.WriteLine(result.Succeeded ? $"  Status: {result.Value ?? "null (not found)"}." : $"  Failed: {result.Error}");

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

/// <summary>Builds the <c>jobId</c> input dictionary expected by <c>trigger-recurring-hangfire</c>.</summary>
internal static class TriggerInputs
{
    public static IReadOnlyDictionary<string, JsonElement> JobId(string jobId) =>
        new Dictionary<string, JsonElement>
        {
            ["jobId"] = JsonDocument.Parse(JsonSerializer.Serialize(jobId)).RootElement.Clone()
        };
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

/// <summary>A parameterless job discovered via <see cref="HangfireJob"/>. No predicate, method selector,
/// or manual wiring is required — deriving from the base class is enough for RegisterJobs&lt;TJobBase&gt;
/// to find, name, and enqueue it.</summary>
internal sealed class SendDigestJob : HangfireJob
{
    public override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("  [job] SendDigestJob executed.");
        return Task.CompletedTask;
    }
}

/// <summary>Options bound from JSON input and passed to the job at enqueue time.</summary>
internal sealed record PurgeCacheOptions(int OlderThanDays);

/// <summary>An intermediate, non-generic base is enough for RegisterJobs&lt;TJobBase, TOptions&gt; to
/// locate the closed <see cref="HangfireJobWithOptions{TOptions}"/> options type.</summary>
internal abstract class PurgeCacheJobBase : HangfireJobWithOptions<PurgeCacheOptions> { }

/// <summary>An options-based job discovered via <see cref="HangfireJobWithOptions{TOptions}"/>; its input
/// schema is generated automatically from <see cref="PurgeCacheOptions"/>.</summary>
internal sealed class PurgeCacheJob : PurgeCacheJobBase
{
    public override Task ExecuteAsync(PurgeCacheOptions options, CancellationToken cancellationToken)
    {
        Console.WriteLine($"  [job] PurgeCacheJob executed (older than {options.OlderThanDays} days).");
        return Task.CompletedTask;
    }
}
