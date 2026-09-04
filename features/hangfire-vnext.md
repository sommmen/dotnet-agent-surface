# Hangfire vNext integration

## Status: P0 — implementation-ready plan

This document defines the next Hangfire satellite iteration. It deliberately
separates current preview behavior from the vNext API and is intended to be
split into the milestones/issues below.

## Problem and goals

`AddHangfireRecurringJobs(...)` opens storage while the catalog is built, calls
`GetRecurringJobs()`, and materializes one operation and skill entry per row.
That makes startup depend on storage, turns runtime configuration into static
metadata, and leaves the catalog stale when jobs differ between environments
or change after startup. It also prevents an offline consumer from building
the rest of its catalog.

The storage-discovery rationale in
[`discovery-satellites.md`](discovery-satellites.md) remains valid: recurring
jobs are registered imperatively and storage is authoritative. The vNext change
is to defer the query to invocation time.

Goals:

- expose a small stable surface independent of storage contents;
- query and validate current storage state at invocation time;
- support an explicit execution model: normal Hangfire enqueueing by default, with
  an opt-in isolated in-memory Hangfire execution path for fast local/CLI runs;
- discover explicitly opted-in job classes, including options-bearing jobs;
- preserve deterministic metadata, safe defaults, diagnostics, and adapter
  equivalence.

Non-goals: silently executing inline, inventing schedules, changing the
configured provider, or replacing runtime recurring-job discovery with blind
reflection. An execution model is selected by configuration and must be visible
in the result contract.

## Stable recurring-job operations

Use the repository's kebab-case convention:

```text
hangfire list-recurring-hangfire
hangfire trigger-recurring-hangfire --job-id nightly-cleanup
```

The category is configurable, but operation names are stable and never contain
a job id.

### `list-recurring-hangfire`

The implementation receives `JobStorage` (or an injected accessor), opens a
connection only when invoked, calls `GetRecurringJobs()`, and projects:

```csharp
public sealed record RecurringHangfireJobInfo(
    string JobId,
    string? JobType,
    string? Method,
    string? Cron,
    DateTime? NextExecution,
    DateTime? LastExecution);
```

Results are sorted by `JobId` using ordinal comparison. Missing optional fields
remain null. Storage failures are structured operation errors, never an empty
successful list.

### `trigger-recurring-hangfire`

The required `jobId` is handled at invocation time:

1. validate non-empty input and load current recurring jobs;
2. compare ids using the adapter's normal normalization;
3. deny missing jobs without calling Hangfire;
4. apply configured safety (default `Confirm`, optionally `Dangerous`);
5. call `IRecurringJobManager.Trigger(jobId)`;
6. return:

```csharp
public sealed record TriggerRecurringHangfireResult(
    string JobId,
    string Status,
    string? EnqueueId,
    DateTimeOffset TriggeredAt);
```

`Status` is `enqueued` or `rejected`; `EnqueueId` is nullable because
`Trigger` does not expose one on every Hangfire version. The result never
claims that execution completed. The registration exposes an execution model:

```csharp
public enum HangfireExecutionModel
{
    EnqueueOnConfiguredStorage,
    ExecuteUsingIsolatedInMemoryServer
}
```

`EnqueueOnConfiguredStorage` is the default and follows the normal Hangfire
flow, allowing the application's configured workers to execute the job.
`ExecuteUsingIsolatedInMemoryServer` is an explicit opt-in for short-lived CLI
or local scenarios: it creates an isolated in-memory storage/server, enqueues
the selected job through Hangfire's normal activation pipeline, waits for the
job result (with a cancellation/timeout bound), and reports completion or
failure. It must not be the implicit behavior of a production host, and it
must not mutate the application's configured storage.

Trigger is fail-closed for stale or ambiguous selection. A job deleted between
validation and trigger is reported as failure; no wildcard/fallback matching
is allowed. Provider transaction/re-read protection is an acceptance item, not
a reason to hide an error.

## Class-based job discovery

Recurring jobs and on-demand class jobs are separate concepts. Add an explicit
fluent registration API, later usable by a source generator:

```csharp
builder.RegisterJobs<ReportJobBase>();
builder.RegisterJobs<ReportJobWithOptionsBase<ReportJobOptionsBase>>();
```

The preferred contract is an attributed generic base class plus fluent
configuration. The base class supplies an opt-in boundary; fluent options
support existing or third-party types. Supporting both is additive.

Proposed shape:

```csharp
public abstract class HangfireJob
{
    public abstract Task ExecuteAsync(CancellationToken cancellationToken);
}

public abstract class HangfireJobWithOptions<TOptions>
{
    public abstract Task ExecuteAsync(TOptions options, CancellationToken cancellationToken);
}

public sealed class HangfireJobRegistrationOptions
{
    public string? Category { get; set; } = "Hangfire jobs";
    public AgentSafetyLevel SafetyLevel { get; set; } = AgentSafetyLevel.Confirm;
    public Func<Type, string>? NameFactory { get; set; }
    public Func<Type, MethodInfo?>? MethodSelector { get; set; }
    public Func<Type, HangfireJobRegistrationMetadata, ValueTask>? EnrichAsync { get; set; }
    public Func<Type, bool>? Exclude { get; set; }
}
```

The exact base signatures may support synchronous methods too; the invariant is
that the selected method is explicit and enqueued as a Hangfire `Job`, never
invoked by reflection.

### Discovery and binding rules

- inspect only supplied assemblies/types;
- include concrete, closed types assignable to the requested base;
- include inherited types once, deduplicated by `Type`;
- skip open generics, abstract types, and inaccessible methods with
  diagnostics by default; offer a strict validation option that turns those
  diagnostics into catalog-build failures;
- reject multiple equally valid execution methods only in strict mode; in
  permissive mode select the conventionally named method deterministically and
  emit a warning when ambiguity remains;
- default selection is declared `Execute`/`ExecuteAsync`; a selector is required
  for overloads or alternate methods;
- options use Core's JSON schema generator; invocation binds JSON to the
  options object and passes it as the Hangfire argument;
- parameterless jobs use an empty argument list; cancellation and Hangfire
  context parameters are supplied by Hangfire at execution time;
- activation is delegated to Hangfire's `JobActivator`/DI integration;
- names default to stable kebab-case type names and use normal category routing;
- normalized full-path collisions fail construction and identify both types;
- exclusions/enrichment run before `Add`; enrichment cannot downgrade dangerous;
- ordering is ordinal by normalized path, then declaring type full name.

### Reflection, source generation, and trimming

Reflection is first because it matches the existing satellite and supports
preview consumers. Warn when an assembly cannot enumerate types. A source
generator is the AOT follow-up: it emits closed registrations, avoids runtime
scanning, and preserves schemas under trimming. NativeAOT consumers must use
generated registration or receive a deterministic missing-metadata diagnostic.

## Safety and CLI behavior

The current `DangerousOperationConfirmationPolicy` only checks
`AgentSafetyLevel.Dangerous`; `Confirm` passes through. vNext must replace or
extend it so both levels require explicit approval, with Dangerous optionally
requiring a stronger callback. Missing confirmation always denies.

CLI hosts must use one standard non-interactive mechanism, for example
`--confirm` for Confirm and `--yes` plus `--confirm` for Dangerous. The flags
belong to shared host configuration, not operation-specific parsers. MCP
callers supply equivalent explicit metadata. Missing approval returns a stable
denial and non-zero exit code; the process never prompts.

## Host composition

The current skill command can be composed by a host, but global options and
policies require parallel parser wiring. Add:

```csharp
var host = AgentSurfaceHost.Create(args)
    .AddGlobalOptions()
    .AddCatalogFactory(() => BuildCatalog())
    .UsePolicy(policy)
    .UseSkillGeneration(skillOptions)
    .UseOutput(renderer)
    .UseExitCodeHandling();
return await host.RunAsync();
```

It must build one catalog, apply one policy pipeline, route global output and
confirmation options to every surface, and own stdout/stderr and exit codes.
Existing adapters remain independently usable.

## Migration and removal

This is a prerelease package with no supported installed base. Remove
`AddHangfireRecurringJobs` in the vNext implementation rather than carrying a
compatibility window or adding an obsolete shim. The replacement is the stable
runtime operation registration described above. The direct, consumer-facing
steps—including category/command-path changes and generated-skill implications—
are in the [eager recurring-job migration guide](../docs/development/hangfire-recurring-migration.md).

`RegisterJobs<TJobBase>` is the primary class-registration API for new job
classes based on `HangfireJob` or `HangfireJobWithOptions<TOptions>`.
`AddHangfireJobTypes` remains supported as an advanced escape hatch for
existing/custom job shapes that require caller-selected types, methods, or
Hangfire argument construction. These mechanisms are distinct from stable
recurring operations and must not be merged.

## Milestones and issues

### HF-1 — runtime recurring operations (P0)

- Implement live list/trigger descriptors, DTO/result schemas, category and
  safety options, ordering, provider-safe errors, and the configurable
  execution model.
- Acceptance: catalog construction never opens storage; list reflects changes
  after build; trigger rejects unknown ids and returns the acknowledgement;
  both configured-storage and isolated in-memory execution are observable and
  cancellation/timeout bounded.
- Tests: in-memory add/remove after build, empty storage, storage exception,
  unknown id, trigger invocation, serialization, ordering, isolated execution,
  timeout, cancellation, and configured-storage enqueueing.

### HF-2 — class discovery and options binding (P0)

- Implement both `RegisterJobs` overloads, method selection, DI/Hangfire
  activation, exclusions, enrichment, names, collisions, and permissive/strict
  diagnostics.
- Acceptance: inherited/closed generic jobs are discovered once; abstract/open
  types are excluded; options appear in schema and queued `Job.Args`.
- Tests: base/inherited/closed generic jobs, overloads, parameterless/options
  jobs, malformed options, DI activation, exclusions, collisions, ordering, and
  reflection load failures.

### HF-3 — fail-closed safety and shared host options (P0)

- Make Confirm and Dangerous fail closed; define shared CLI/MCP confirmation
  metadata and compose global options/policies/output/exit handling.
- Acceptance: no prompt; no callback means denial; Core, CLI, and MCP agree.
- Tests: each safety level, flag combinations, non-interactive denial,
  cancellation, and adapter equivalence.

### HF-4 — migration, source generation, and diagnostics (P1)

- Add obsolete guidance, generated registration, trimming/AOT diagnostics, and
  structured discovery warnings.
- Acceptance: migration text is actionable; generated/reflection metadata is
  byte-identical; NativeAOT limitations are explicit.
- Tests: obsolete compile sample, parity, linker smoke test, diagnostic snapshots.

### HF-5 — samples and documentation (P1)

- Update the Hangfire sample to stable runtime operations and retain the
  credential-free SQL Server consumer guidance.
- Acceptance: in-memory sample runs offline; SQL Server requires configuration;
  README and skill examples use complete category paths.
- Tests: sample build/run, package-source mapping, and API-checked examples.

## Definition of done

P0 is complete only when recurring catalog construction is storage-independent,
invocation uses current storage, trigger results are stable and honest,
job classes/options are explicitly discoverable and schema-bound, safety is
fail-closed across adapters, and migration guidance plus focused tests ship
with the implementation.
