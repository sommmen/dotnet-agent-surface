# Hangfire vNext integration

## Status: delivered through P0; P1 diagnostics and AOT boundary delivered

The vNext architecture is implemented. Stable recurring operations remain
storage-lazy, while class-based one-off job registration remains an explicit
reflection-based opt-in. The remaining operational and package-polish work is
tracked separately from the shipped migration path.

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

## Delivery status

### HF-1 — runtime recurring operations (delivered)

`AddHangfireRecurringOperations(...)` supplies stable list and trigger
operations. Catalog construction does not access Hangfire storage; listing and
triggering use the configured storage only when invoked. The focused in-memory
tests cover storage changes after catalog construction, ordering, unknown-id
rejection, configured-storage acknowledgement, isolated execution, timeout, and
cancellation.

### HF-2 — class discovery and options binding (delivered)

`RegisterJobs<TJobBase>` and `RegisterJobs<TJobBase, TOptions>` provide the
primary opinionated path for concrete `HangfireJob` types, including method
selection, options binding, exclusions, deterministic metadata enrichment, and
strict/permissive handling. `AddHangfireJobTypes(...)` remains the deliberately
advanced alternative where consumers must choose types, methods, or Hangfire
arguments themselves.

### HF-3 — fail-closed safety and shared host options (delivered)

`Confirm` and `Dangerous` operations fail closed unless the host supplies the
shared `OperationConfirmation` metadata. CLI and MCP adapters use that same
contract; the migration guide documents non-interactive approval, denial,
cancellation, and exit semantics.

### P1 — reflection diagnostics and AOT boundary (delivered scope)

Reflection-based registration (`RegisterJobs` and `AddHangfireJobTypes`) is
unsupported in trimmed and NativeAOT applications until source-generated
registration exists. This is an explicit support boundary rather than a claim of
AOT compatibility; stable recurring operations are unaffected because they do
not discover job classes through reflection.

Discovery now emits immutable `HangfireJobDiscoveryReport` entries for
registration, skips, warnings, and strict failures, while retaining the older
mutable diagnostics collection for compatibility. Registration validates null
assemblies and generated metadata. Catalog registration is synchronous: use the
deterministic, non-I/O `Enrich` callback. `EnrichAsync` is retained only for
source compatibility and fails at startup with actionable migration guidance,
rather than synchronously blocking asynchronous work.

A supported SQL Server storage compatibility suite requires opt-in,
credential-free infrastructure that this repository does not yet provide. That
work is intentionally deferred to [issue #22](https://github.com/sommmen/dotnet-agent-surface/issues/22); it must validate recurring listing, triggering, and error translation
against a supported SQL Server provider without making normal test runs require
credentials or Docker.

### P2 — package and operational polish (remaining)

The remaining work is a dedicated README/package-consumption section, publish
workflow preview summaries, and investigation/documentation of the transitive
`Newtonsoft.Json` advisory. These tasks do not change the delivered recurring
operation or class-registration architecture.

## Definition of done

The delivered P0 path provides storage-independent recurring catalog
construction, current-storage invocation, explicit class discovery, fail-closed
confirmation across adapters, and a documented offline consumer migration. P1
adds first-class reflection diagnostics and the explicit trimming/NativeAOT
support boundary; SQL Server compatibility coverage remains the tracked
follow-up described above.