# Migrating eager Hangfire recurring-job discovery

This guide migrates a prerelease consumer from the removed eager
`AddHangfireRecurringJobs(...)` API to the storage-lazy
`AddHangfireRecurringOperations(...)` API.

## What changed

The former API opened `JobStorage` while the operation catalog was being built,
read every recurring job, and added one operation for every row. That made
catalog construction and generated skills depend on live Hangfire storage.

The replacement always adds exactly two operations:

| Stable operation | Purpose | Default safety |
|---|---|---|
| `list-recurring-hangfire` | Read the recurring jobs that exist when it is invoked. | `Safe` |
| `trigger-recurring-hangfire` | Request one current recurring job by its `jobId` input. | `Confirm` |

Storage is accessed only when either operation is invoked. Building a catalog,
generating a skill, and checking a generated skill therefore do not require a
Hangfire storage connection.

## Direct API migration

Replace the eager registration:

```csharp
var catalog = new OperationCatalogBuilder()
    .AddHangfireRecurringJobs(storage, jobManager, options =>
    {
        options.Category = "Hangfire";
        options.SafetyLevel = AgentSafetyLevel.Confirm;
        options.Enrich = (recurringJob, metadata) =>
        {
            metadata.Name = $"run-{recurringJob.Id}";
            metadata.Category = "Operations maintenance";
        };
    })
    .Build();
```

with stable recurring operations:

```csharp
var catalog = new OperationCatalogBuilder()
    .AddHangfireRecurringOperations(storage, jobManager, options =>
    {
        options.Category = "Operations maintenance";
        options.TriggerSafetyLevel = AgentSafetyLevel.Confirm;
    })
    .Build();
```

`AddHangfireRecurringJobs(...)`, `HangfireDiscoveryOptions`, and its per-row
`Enrich` callback were intentionally removed; there is no compatibility shim.
Move metadata that applied consistently to all recurring actions to
`HangfireRecurringOperationsOptions`. Per-job names, aliases, descriptions,
and categories have no replacement because recurring jobs are now runtime data,
not generated operations.

## Category, command path, and recurring IDs

Under the eager API, the operation name defaulted to the recurring job ID (or
to `metadata.Name` after enrichment). A job named `nightly-cleanup` could
therefore produce a distinct command such as:

```text
your-host operations maintenance run-nightly-cleanup
```

With the stable API, the category is shared by both operations and forms the
same command-group path. The recurring job ID belongs to the `jobId` input of
the trigger operation:

```text
your-host operations maintenance list-recurring-hangfire
your-host operations maintenance trigger-recurring-hangfire --jobId nightly-cleanup
```

Do not derive a command name from a recurring job ID or preserve an old
per-job alias. A job may be added, removed, or renamed after the catalog has
been built; pass the current identifier using `--job-id` (CLI) or `jobId`
(MCP) when invoking `trigger-recurring-hangfire`.

## Adopting a pre-existing (brownfield) job hierarchy

Most Hangfire adopters already have a job base class before they add this
package — often one with a generic self-reference (CRTP) and a constructor
that takes a Hangfire `PerformContext` or other DI-resolvable dependencies.
That shape is incompatible with `HangfireJob`/`HangfireJobWithOptions<TOptions>`
(plain abstract classes with an implicit parameterless constructor), so
`RegisterJobs<TJobBase>` used to require either rewriting every job's base
class or falling back to the `AddHangfireJobTypes(...)` escape hatch for what
is actually the common case.

`RegisterJobs<TJobBase>`/`RegisterJobs<TJobBase, TOptions>` now accept any
`TJobBase` that implements `IHangfireJob`/`IHangfireJob<TOptions>` — including
`HangfireJob`/`HangfireJobWithOptions<TOptions>` themselves, which implement
these interfaces. A pre-existing base class can implement the interface
directly, without changing its inheritance chain or constructor signature.
Discovery only inspects types via reflection; it never constructs a job
instance itself, so the job type can declare any constructor. Hangfire's own
`JobActivator` (the same activator already used for jobs with
constructor-injected dependencies) constructs the instance when the enqueued
job actually executes.

For example, a CRTP-style base class that takes a `PerformContext` through its
constructor:

```csharp
using Hangfire;
using DotNetAgentSurface.Hangfire;

// Pre-existing brownfield hierarchy — unchanged except for adding `: IHangfireJob`.
public interface IOpgJob
{
    Task RunAsync(CancellationToken cancellationToken);
}

public abstract class OpgJobBase<TSelf> : IOpgJob, IHangfireJob
    where TSelf : OpgJobBase<TSelf>
{
    private readonly PerformContext _context;

    protected OpgJobBase(PerformContext context)
    {
        _context = context;
    }

    protected PerformContext Context => _context;

    public abstract Task RunAsync(CancellationToken cancellationToken);

    // IHangfireJob's conventional ExecuteAsync(CancellationToken) shape forwards
    // to the pre-existing RunAsync(...) member so no existing job needs to change.
    // This must be a public method (not an explicit interface implementation):
    // discovery only considers public Execute/ExecuteAsync methods via reflection.
    public Task ExecuteAsync(CancellationToken cancellationToken) => RunAsync(cancellationToken);
}

public sealed class NightlyReconciliationJob : OpgJobBase<NightlyReconciliationJob>
{
    private readonly IReconciliationService _service;

    public NightlyReconciliationJob(PerformContext context, IReconciliationService service)
        : base(context)
    {
        _service = service;
    }

    public override Task RunAsync(CancellationToken cancellationToken) =>
        _service.ReconcileAsync(cancellationToken);
}
```

Registration is identical to the greenfield case, just with the brownfield
base class as `TJobBase`:

```csharp
var catalog = new OperationCatalogBuilder()
    .RegisterJobs<OpgJobBase<NightlyReconciliationJob>>(backgroundJobClient, [typeof(NightlyReconciliationJob).Assembly])
    .Build();
```

`NightlyReconciliationJob` is discovered exactly as a `HangfireJob` subclass
would be — enqueued through `IBackgroundJobClient`, constructed by Hangfire's
`JobActivator` (which resolves `PerformContext` and `IReconciliationService`
from the application's DI container, just as it already would for a
recurring job) — without OPG's `OpgJobBase<TSelf>`/`IOpgJob` hierarchy ever
being rewritten to derive from `HangfireJob`.

The same applies to options-bearing brownfield jobs: implement
`IHangfireJob<TOptions>` on the existing base class/interface instead of
`IHangfireJob`, with an `ExecuteAsync(TOptions options, CancellationToken
cancellationToken)` method, and register with
`RegisterJobs<TJobBase, TOptions>(...)`.

## Generated-skill migration

The eager catalog generated one skill entry per recurring-job row. Its skill
snapshot changed whenever storage contents changed and could not be generated
offline.

After migration, generate and check the skill from the stable catalog:

```powershell
your-host generate --output skill
your-host check --output skill
```

The snapshot contains the two stable operations above, never an entry named
for `nightly-cleanup` (or any other recurring ID). Updating schedules or
recurring IDs requires no skill regeneration; clients list the current jobs,
then send the selected ID as the trigger input. If an old snapshot is checked,
regenerate it after replacing the registration.

## Confirmation when triggering

`trigger-recurring-hangfire` defaults to `Confirm`. Invoke it with `--confirm`
in a generated CLI, or send the MCP confirmation metadata described in the
[non-interactive operation confirmation contract](operation-confirmation.md).
A missing approval returns the stable denial result before Hangfire is called;
the CLI exits with code `1` and does not prompt.

## Choosing the right Hangfire integration

The following APIs deliberately cover distinct workloads; do not combine their
registration mechanisms.

| Consumer need | Choose | Why |
|---|---|---|
| List or trigger schedules already owned by Hangfire storage | `AddHangfireRecurringOperations(...)` | The storage-lazy, stable two-operation surface. It does not create schedules or expose one command per storage row. |
| Register job classes that take no agent-supplied input (no options), greenfield or brownfield | `RegisterJobs<TJobBase>(...)` | **Primary class-registration API.** `TJobBase` may be `HangfireJob` for new job code, or the `IHangfireJob` interface itself — or any pre-existing base class/interface that implements it — to adopt an existing job hierarchy (including one with constructor parameters or a CRTP-style generic self-reference) without rewriting its inheritance chain. See [Adopting a pre-existing (brownfield) job hierarchy](#adopting-a-pre-existing-brownfield-job-hierarchy). |
| Register options-bearing job classes, greenfield or brownfield | `RegisterJobs<TJobBase, TOptions>(...)` | The primary typed variant: it adds JSON schema, input binding, and an explicit options contract. `TJobBase` may be `HangfireJobWithOptions<TOptions>` or the `IHangfireJob<TOptions>` interface, with the same brownfield-adoption support as above. |
| Adapt job classes with a fully custom shape (non-conventional method name/signature, custom argument binding) | `AddHangfireJobTypes(...)` | Escape hatch for callers who need a custom type predicate, method selector, or argument factory that `RegisterJobs` intentionally does not expose. Most pre-existing job hierarchies do not need this — implementing `IHangfireJob`/`IHangfireJob<TOptions>` on the existing base class/interface and using `RegisterJobs` is simpler. |

`RegisterJobs` is the long-term primary API for new class-based jobs. Keep
`AddHangfireJobTypes` when its flexibility is required; it is not a deprecated
alias and no migration is needed merely because both APIs are available. The
class-registration APIs enqueue an on-demand execution and do not discover or
alter recurring-job configuration.
