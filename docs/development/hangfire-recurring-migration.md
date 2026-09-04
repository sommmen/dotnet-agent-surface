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
| Register controlled parameterless job classes derived from `HangfireJob` | `RegisterJobs<TJobBase>(...)` | **Primary class-registration API.** It supplies conventions, method selection, metadata, diagnostics, and enqueueing for a supplied job base type. |
| Register controlled options-bearing job classes derived from `HangfireJobWithOptions<TOptions>` | `RegisterJobs<TJobBase, TOptions>(...)` | The primary typed variant: it adds JSON schema, input binding, and an explicit options contract. |
| Adapt legacy, non-conforming, or unusually shaped job classes | `AddHangfireJobTypes(...)` | Advanced escape hatch when the consumer needs a custom type predicate, method selector, argument factory, or metadata behavior that the opinionated APIs intentionally do not expose. |

`RegisterJobs` is the long-term primary API for new class-based jobs. Keep
`AddHangfireJobTypes` when its flexibility is required; it is not a deprecated
alias and no migration is needed merely because both APIs are available. The
class-registration APIs enqueue an on-demand execution and do not discover or
alter recurring-job configuration.
