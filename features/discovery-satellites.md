# Discovery satellites: Hangfire jobs, ASP.NET Core endpoints, and native MCP tools

## Status: viable — proposed plan

This document assesses the viability of three additional, automatic operation-discovery sources feeding
the same `OperationCatalog` that `[AgentOperation]` populates today, and proposes a plan for delivering
them. All three are judged **viable**. None requires changing `DotNetAgentSurface.Core`'s discovery
philosophy ("scan only explicitly annotated methods") — each is a separate *satellite* package that
performs its own discovery and then calls the existing `OperationCatalogBuilder.Add(...)` escape hatch
that already exists precisely for "a method [that] cannot carry an `AgentOperationAttribute` (third-party
types)" (see [`OperationCatalogBuilder.cs`](../src/DotNetAgentSurface.Core/OperationCatalogBuilder.cs)).
This also directly resolves the "Discovery and registration" work item in
[`development.md`](./development.md), which already anticipated "registrations that cannot be
expressed naturally with attributes."

The three sources:

1. **Hangfire recurring jobs**, discovered without adding a Hangfire dependency to `Core` or `CommandLine`
   or `Mcp` — a new `DotNetAgentSurface.Hangfire` satellite package owns that dependency.
2. **ASP.NET Core endpoints**, discovered via a meta package that is allowed to depend on ASP.NET Core
   (`DotNetAgentSurface.AspNetCore`), with an explicit design for the authentication concern the user
   flagged.
3. **Native MCP-SDK-attributed tools** (`[McpServerToolType]` / `[McpServerTool]`), ingested into the same
   catalog so operations declared for the official MCP C# SDK show up in the CLI, skill docs, and policy
   pipeline too, not just the MCP surface.

## Assumption flagged for confirmation

The user's second message included an unfinished sentence: *"For h[angfire]..."* before the message was
resent without it. I could not get clarification, so this plan makes one explicit assumption in its place
and calls it out here rather than guessing silently:

> **Assumption:** the unstated Hangfire concern was about *safe invocation*, not discovery — specifically,
> that Hangfire job classes/methods often depend on Hangfire-supplied context (see `PerformContext`,
> `IJobCancellationToken`, DI-activated constructor parameters) that a naive `MethodInfo.Invoke` from the
> catalog's `OperationInvoker` cannot supply. Evidence for this concern is real: patterns found in the two
> private reference repos show job classes with constructors that require `PerformContext context` and
> `ILogger<TSelf> logger`, injected by Hangfire's own `JobActivator` when a job runs through Hangfire's
> pipeline — such a job would fail if invoked directly.
>
> The design below resolves this by **never invoking Hangfire job methods directly**. Discovered jobs are
> invoked through `IRecurringJobManager.TriggerJob(id)`, Hangfire's own public API for firing a job
> on-demand. This re-enters Hangfire's normal activation/execution pipeline (queueing, `JobActivator`,
> `PerformContext`, retries, logging) exactly as a scheduled firing would. If the real concern was
> something else (for example, duplicate/overlapping execution, long-running jobs blocking a
> request/response invocation model, or something ops-related), flag it and this section will be revised.

## 1. Hangfire recurring job discovery

### Verdict: viable, but only through Hangfire's own runtime storage — not through blind reflection

Two private reference repos were inspected for real-world Hangfire usage patterns (not named here per the
user's request). In both, jobs are registered **imperatively** in startup code — a custom
`IJobScheduler`/`JobScheduler` abstraction calls `RecurringJob`/`IRecurringJobManager` APIs directly (for
example, `scheduler.ScheduleRecurringJob<CleanOldData>(Cron.Daily())`). Hangfire itself defines **no**
marker attribute for job methods, and neither reference repo layers one on top. This rules out a naive
"scan all assemblies for a `[HangfireJob]`-style attribute" approach as the primary mechanism: it would
require every job author to adopt a brand-new marker before anything is discoverable, and real jobs today
have none.

What *is* viable, and requires zero changes to how job authors already write jobs, is discovery through
Hangfire's own runtime storage:

- `Hangfire.Storage.StorageConnectionExtensions.GetRecurringJobs(IStorageConnection)` returns
  `List<RecurringJobDto>` — Hangfire's storage layer (SQL Server, PostgreSQL, in-memory, etc.) already
  maintains a complete, queryable catalog of every recurring job, however it was registered.
- Each `RecurringJobDto.Job` is a `Hangfire.Common.Job` with public `Type` and `MethodInfo Method`
  properties — full reflection access to the target type and method, its cron expression, queue, and
  last/next execution, with no custom attribute required.
- Invocation goes through `IRecurringJobManager.TriggerJob(string recurringJobId)`, not
  `Method.Invoke(...)`. This is the piece that resolves the DI/`PerformContext` concern above.

This means Hangfire job discovery is necessarily a **post-startup, runtime** operation: it has to run
after `app.Build()`/`app.Run()` has registered jobs with Hangfire's storage, not at compile time or at
service-registration time. The satellite package's shape:

```csharp
// DotNetAgentSurface.Hangfire
public static class HangfireOperationCatalogBuilderExtensions
{
    public static OperationCatalogBuilder AddHangfireRecurringJobs(
        this OperationCatalogBuilder builder,
        JobStorage storage,
        IRecurringJobManager jobManager,
        Action<HangfireDiscoveryOptions>? configure = null);
}
```

Each discovered `RecurringJobDto` becomes one operation, registered via the existing delegate-based
`Add(name, description, delegate, configure)` overload, where the delegate wraps
`jobManager.TriggerJob(dto.Id)` rather than the raw job method — the catalog operation's `MethodInfo` can
still point at a thin static trigger method so schema generation has something to reflect over (recurring
jobs take no invocation-time parameters beyond an optional reason/cancellation token, matching Hangfire's
own `TriggerJob(string)` signature).

Design notes:

- **Naming**: default operation name derives from the recurring job's `Id` (already unique in Hangfire);
  allow an override.
- **Safety level**: default new Hangfire-sourced operations to `AgentSafetyLevel.Confirm` rather than
  `Safe` — an agent invoking a production recurring job on demand is a meaningfully different risk profile
  than a read-only query, and the catalog has no way to know what a given job actually does. Let the host
  application override per-job.
- **Description**: Hangfire's storage doesn't carry a human-readable description. An optional companion
  marker attribute (for example, `[HangfireOperation(Description = "...")]`, applied to the job class or
  method) can be offered purely for metadata enrichment (description, category, safety level, aliases) —
  discovered *and* used if present, but discovery itself never depends on it. This keeps the "opt-in
  metadata, opt-out-of-nothing discovery" property intact: undecorated jobs are still discovered, just with
  a generated fallback description (for example, `"Triggers the '{Id}' recurring job ({Type}.{Method})."`).
- **One-off (fire-and-forget) jobs and background jobs enqueued ad hoc** (`BackgroundJob.Enqueue(...)`)
  are explicitly **out of scope** for the initial version: they have no persistent identity to enumerate
  before they run, unlike recurring jobs. Only `IRecurringJobManager`-registered jobs are discoverable this
  way. This should be stated as an explicit non-goal for the first iteration.
- **Refresh timing**: because discovery reads `JobStorage` at catalog-build time, a job added to Hangfire
  *after* the catalog was built (e.g. an admin dynamically schedules a new recurring job at runtime) will
  not appear until the catalog is rebuilt. `OperationCatalog` today is an immutable, build-once snapshot
  (matching its EF-Core-inspired "compose, then `Build()`" model) — call out that rebuilding/refreshing a
  catalog after startup is out of scope for the initial delivery and can be revisited if needed, consistent
  with the "Discovery and registration" work item already flagged as unresolved for the fluent builder in
  general.

### Package and dependencies

`DotNetAgentSurface.Hangfire` depends on `DotNetAgentSurface.Core` and `Hangfire.Core` (the storage/manager
abstractions live there; no specific storage provider like `Hangfire.SqlServer` is required). Hangfire.Core
targets modern .NET and .NET Standard 2.0 itself, so `net10.0;netstandard2.0` multi-targeting is possible
if desired for parity with `Core`/`CommandLine`/`Mcp`, though a `net10.0`-only build is also reasonable
given Hangfire consumers in this org's reference repos are exclusively on modern .NET. Recommendation:
match `Core`'s `net10.0;netstandard2.0` unless it creates real friction, for consistency.

## 2. ASP.NET Core endpoint discovery

### Verdict: viable via `ApiExplorer`, with an explicit auth-handling policy design

A meta package, `DotNetAgentSurface.AspNetCore`, can depend directly on ASP.NET Core (unlike `Core`, which
must stay dependency-light per the existing non-goals). It discovers endpoints — both MVC controller
actions and Minimal API route handlers — and registers each as a catalog operation via the same
delegate-based `Add(...)` builder method, with the delegate invoking the endpoint's request delegate (or,
more practically, wrapping an `HttpClient`/`IHttpContextFactory`-based in-process call, or delegating to
the already-resolved `Endpoint.RequestDelegate` against a synthesized `HttpContext`).

**Use `ApiExplorer` — yes.** `IApiDescriptionGroupCollectionProvider` (`ApiExplorer`) is the standard,
already-hardened way ASP.NET Core itself uses to enumerate the full set of routable endpoints for both MVC
and Minimal APIs, including parameter binding sources, response types, and (critically for the auth
concern below) endpoint metadata. It already handles both hosting models uniformly, is what Swashbuckle/
OpenAPI generation and `Microsoft.AspNetCore.OpenApi` build on, and avoids reinventing a fragile custom
`EndpointDataSource` walk. There is no compelling reason to hand-roll routing-table inspection when the
framework's own reflection surface already exists and is exercised by every ASP.NET Core app that
generates OpenAPI documents today.

### The authentication concern

The user explicitly flagged this as an open problem, and it is real: an auto-discovered endpoint such as
`DELETE /api/customers/{id}` may have been written assuming a browser session, a bearer token forwarded
from an authenticated caller, or an `[Authorize(Policy = "...")]` requirement the *catalog caller* (an
agent invoking through MCP or the CLI) has no way to naturally satisfy. Discovering the endpoint and
letting it run without accounting for this would silently bypass the application's real authorization
model — which is explicitly called out as a non-goal ("Replacing application-level authentication or
authorization systems") and a Safety-and-security requirement ("expose hooks for authentication and
authorization before invocation") in [`development.md`](./development.md).

The mechanism already exists in Core to solve this correctly: `IOperationInvocationPolicy`. The design:

1. **Discovery time**: for each discovered endpoint, capture its authorization metadata from
   `ApiDescription`/`Endpoint.Metadata` (`IAuthorizeData`, `[Authorize]`, `[AllowAnonymous]`) into the
   operation's registration — either stored in `OperationRegistrationOptions.Category` /
   a small metadata bag, or (cleaner) surfaced through a small `AspNetCoreEndpointMetadata` companion object
   attached to the descriptor via a dictionary keyed by operation name, since `OperationDescriptor` itself
   is sealed and framework-agnostic on purpose and should **not** grow ASP.NET-Core-specific properties.
2. **Invocation time**: `DotNetAgentSurface.AspNetCore` ships an `IOperationInvocationPolicy` implementation
   (for example, `AspNetCoreEndpointAuthorizationPolicy`) that, for endpoints requiring authorization,
   requires the catalog caller to supply equivalent credentials through the invocation context (for
   example, a bearer token or a pre-authenticated `ClaimsPrincipal` passed alongside `inputs`) and runs
   the endpoint's actual `IAuthorizationService`/policy evaluation against it before invoking — reusing the
   application's real authorization pipeline rather than re-implementing it. If no such context is
   supplied, the policy denies by default. Anonymous endpoints (`[AllowAnonymous]` or no auth metadata) are
   allowed through unchanged.
3. **Explicit opt-out for the unsafe case**: hosts that genuinely want to expose an authorization-gated
   endpoint to agents without agent-supplied credentials (for example, a trusted internal automation
   context) must opt in explicitly per-endpoint or per-category — never as a default — mirroring the
   existing `DangerousOperationConfirmationPolicy` pattern where the default behavior denies unless a host
   explicitly wires up an approval mechanism.

This keeps the framework's stated boundary intact: it provides the *hook*, not a new identity model. The
host application is still responsible for deciding how an agent's invocation gets treated as an
authenticated principal (if at all).

### Invocation model

Two workable options, both compatible with `ApiExplorer`-based discovery:

- **In-process invocation** using `Endpoint.RequestDelegate` against a synthesized `HttpContext` built from
  the catalog invocation's `inputs` (mapping JSON inputs to route values/query/body per the endpoint's
  bound parameters). Avoids a network hop and keeps everything in the same process/DI scope, but requires
  careful synthesis of `HttpContext` (headers, `IServiceProvider` scope, etc.) — this is exactly the kind
  of "in-process ASP.NET Core endpoint execution" pattern `Microsoft.AspNetCore.TestHost`
  (`TestServer`/`WebApplicationFactory`) already solves for integration testing; reusing that machinery (or
  a similar minimal harness) rather than hand-rolling `HttpContext` construction is the pragmatic choice.
- **Loopback HTTP call** via an injected `HttpClient` pointed at the app's own address. Simpler to reason
  about (it's exactly what a real caller would do, including real middleware/auth pipeline execution) but
  adds latency and requires the app to be reachable at a known base address, which isn't always true (for
  example, behind a reverse proxy with path rewriting).

Recommendation: start with **`TestHost`-style in-process invocation** (via `Endpoint.RequestDelegate` and
a synthesized context, using ASP.NET Core's own test-host approach as the reference implementation) as the
default, since it's dependency-light and works in every hosting scenario without requiring a reachable
base URL — but structure the invocation path behind a small internal abstraction so a loopback-HTTP mode
could be added later without changing the discovery/policy layers.

### Package and dependencies

`DotNetAgentSurface.AspNetCore` depends on `DotNetAgentSurface.Core` and ASP.NET Core's shared framework
(`Microsoft.AspNetCore.App`, referenced via `<FrameworkReference>`, not a NuGet package — standard for
ASP.NET Core class libraries). ASP.NET Core has no netstandard2.0 story worth targeting for this scenario
(Minimal APIs and modern `ApiExplorer` behavior are net6.0+ only in practice, and this repo's own ASP.NET
Core sample already targets `net10.0`), so `net10.0`-only is the right TFM for this package — no
multi-targeting needed or expected here.

## 3. Native MCP-SDK tool ingestion

### Verdict: viable, and it's the most mechanically simple of the three

Today, `DotNetAgentSurface.Mcp` only projects the catalog *outward* (catalog → MCP tools via
`McpOperationAdapter`/`McpOperationServer`). The user's addition asks for the reverse: methods already
marked with the official MCP C# SDK's own attributes should also flow *into* the catalog, so the same
operation participates in the CLI, skill generation, and policy pipeline, not just the MCP surface it was
originally written for.

The official SDK (`ModelContextProtocol.Server`, confirmed directly from the `modelcontextprotocol/csharp-sdk`
source) already defines exactly the attribute pair needed:

- `[McpServerToolType]` (class-level) marks a type containing tool methods.
- `[McpServerTool]` (method-level) marks an individual tool method, carrying `Name`, `Title`,
  `Destructive`, `Idempotent`, `OpenWorld`, `ReadOnly`, `UseStructuredContent`, `OutputSchemaType`, and
  `IconSource`.
- The SDK's own `WithToolsFromAssembly(Assembly?, JsonSerializerOptions?)` extension performs exactly this
  reflection scan today to build its own tool list — this repo's discovery code can mirror that same
  pattern rather than inventing a new one, which also means it will naturally stay compatible as the SDK
  evolves its own scan logic.

A small addition to `DotNetAgentSurface.Mcp` (or a new `DotNetAgentSurface.Mcp.Discovery` companion, if
keeping the outbound adapter and inbound discovery cleanly separated is preferred — recommendation below)
does the mirrored scan and registers each `[McpServerTool]` method via
`OperationCatalogBuilder.Add(name, description, delegate, configure)`, mapping SDK metadata to
`OperationRegistrationOptions` where a natural equivalent exists:

| MCP SDK attribute property | Catalog equivalent |
|---|---|
| `Name` | `name` argument to `Add(...)` |
| `Title` / method XML doc / no description property on SDK attribute | `description` argument (SDK tool attributes don't carry a description string themselves — today the MCP SDK sources tool descriptions from `[Description]` (`System.ComponentModel`) on the method, which this scan should also read) |
| `Destructive = true` | `SafetyLevel = AgentSafetyLevel.Dangerous` |
| `ReadOnly = true` | `SafetyLevel = AgentSafetyLevel.Safe` (absent other signals) |
| `Idempotent` | No direct equivalent today — `AgentOperationAttribute` has `IsIdempotent`, but `OperationRegistrationOptions` (the options type `Add(...)` accepts) does **not** currently expose it, only `Category`/`SafetyLevel`/`Examples`/`Aliases`. This is a small, concrete Core gap this feature surfaces: adding an `IsIdempotent` property to `OperationRegistrationOptions` is a minimal, additive change worth doing alongside this work so MCP idempotency hints aren't silently dropped |
| `OpenWorld`, `UseStructuredContent`, `OutputSchemaType`, `IconSource` | No direct catalog equivalent today; carry forward only if/when the schema generator grows matching concepts, otherwise ignore without failing discovery |

Because this ingestion is pure reflection over already-compiled attributes (no runtime state dependency
the way Hangfire's storage or ASP.NET Core's built request pipeline are), it is the one of the three that
*could* run at build time — see the source-generator discussion below for why it's still recommended as a
runtime scan rather than a generator, for consistency with the other two and to avoid a third distinct
discovery mechanism in the codebase.

### Should this live in `DotNetAgentSurface.Mcp` or a new package?

Recommendation: **extend `DotNetAgentSurface.Mcp`** rather than create a new package. The dependency
(`ModelContextProtocol`) is already a reference of `Mcp`, the attribute types live in the same SDK package,
and conceptually "ingest native MCP tools into the catalog" and "project catalog operations as MCP tools"
are two directions of the same MCP↔catalog relationship, not two unrelated concerns. Ship it as an
additive `OperationCatalogBuilder.AddMcpServerTools(Assembly)`-style extension method alongside the
existing adapter types, gated so it has no effect unless explicitly called (same opt-in posture as
everything else).

## Cross-cutting decisions

### Source generator — **no**

All three sources were evaluated against a Roslyn source generator (compile-time codegen) as an
alternative to runtime reflection, since the user asked for this to be considered explicitly. Verdict: no,
for all three, for the same underlying reason — **the information needed to discover each source is not
fully available at compile time**:

- **Hangfire**: recurring jobs are only fully known after the application has started and registered them
  with Hangfire's storage (imperative `Program.cs` calls, possibly conditional on configuration/environment).
  A source generator running at compile time over source text cannot see runtime `RecurringJob.AddOrUpdate`
  call sites reliably (they can be behind loops, conditionals, config-driven cron expressions, or in
  different assemblies), and even if it could, the resulting list wouldn't match what's actually registered
  at runtime in a given environment. The only reliable source of truth is Hangfire's own storage after
  startup.
- **ASP.NET Core**: `ApiExplorer`'s own endpoint list is itself only fully resolved after `app.Build()` —
  route templates, constraints, filters, and metadata can be affected by conventions, `IEndpointConventionBuilder`
  configuration, and DI-composed `IApplicationModelConvention`s that only run during startup. A generator
  operating purely on source text would have to reimplement significant parts of ASP.NET Core's own
  endpoint-model composition to get an equivalent answer, which is both a maintenance burden and a
  correctness risk (drift from the real framework behavior) for no real benefit over reusing `ApiExplorer`.
- **MCP tools**: this one *is* fully knowable at compile time (attributes on already-defined methods,
  similar to how `[AgentOperation]` is already reflection-scanned rather than generator-scanned). Using a
  generator here alone would introduce a third, inconsistent discovery mechanism for one source only, with
  no equivalent option available for the other two. Reflection is simpler, matches the pattern
  `OperationCatalog.DiscoverOperations` already uses for `[AgentOperation]`, and matches the SDK's own
  `WithToolsFromAssembly` approach it is mirroring.

The existing `PolySharp` reference in this repo is a build-time *language polyfill* generator with no
runtime dependency and no relationship to catalog population — it's not meaningful precedent either way
for this decision, and is not analogous.

Net effect: keep discovery uniformly reflection/runtime-API-based across all three new sources, consistent
with how `[AgentOperation]` discovery already works.

### `ApiExplorer` — **yes**

Covered above in the ASP.NET Core section; restated here since the user asked for an explicit answer.
`IApiDescriptionGroupCollectionProvider` is the correct, already-battle-tested mechanism (it's what
OpenAPI/Swagger generation is built on) rather than a custom `EndpointDataSource` walk.

## Proposed package structure

```text
src/
  DotNetAgentSurface.Core/            (unchanged — no new dependencies)
  DotNetAgentSurface.CommandLine/     (unchanged)
  DotNetAgentSurface.Mcp/             (extended: + AddMcpServerTools(Assembly) ingestion)
  DotNetAgentSurface.Hangfire/        (new — depends on Core + Hangfire.Core)
  DotNetAgentSurface.AspNetCore/      (new — depends on Core + Microsoft.AspNetCore.App)
samples/
  DotNetAgentSurface.Samples.Hangfire/     (new — small recurring-job sample, mirrors existing samples/ pattern)
  DotNetAgentSurface.Samples.AspNetCore/   (extended, or a new sample — demonstrate endpoint discovery + the auth policy)
```

This keeps `Core` dependency-free (no Hangfire, no ASP.NET Core reference) exactly as the existing
"Target frameworks and dependencies" section requires, while each satellite package takes on exactly one
external dependency family.

## Proposed implementation milestones

Following the numbered-milestone convention already used in
[`development.md`](./development.md)'s "Implementation tracking" table:

| Milestone | Dependency | Scope |
|---|---|---|
| MCP tool ingestion | MCP adapter (existing) | `AddMcpServerTools(Assembly)` reflecting `[McpServerToolType]`/`[McpServerTool]` into `OperationCatalogBuilder`; mapping table above; tests proving a `[McpServerTool]`-only method appears correctly in CLI/skill output, not just MCP |
| Hangfire recurring job discovery | Shared invocation, shared policy pipeline (existing) | `DotNetAgentSurface.Hangfire` package; `AddHangfireRecurringJobs(...)`; `TriggerJob`-based invocation; default `Confirm` safety level; optional enrichment attribute; tests against `Hangfire.InMemory` storage |
| ASP.NET Core endpoint discovery | Shared invocation, shared policy pipeline (existing) | `DotNetAgentSurface.AspNetCore` package; `ApiExplorer`-based discovery; in-process invocation via synthesized `HttpContext`/`TestHost`-style execution; tests covering both MVC and Minimal API endpoints |
| Endpoint authorization policy | ASP.NET Core endpoint discovery | `AspNetCoreEndpointAuthorizationPolicy` implementing `IOperationInvocationPolicy`; default-deny for `[Authorize]`-protected endpoints without supplied credentials; explicit opt-in path documented; tests proving an authorized endpoint is denied without credentials and allowed with a valid supplied principal |
| Sample hosts | All three above | Small samples demonstrating each discovery source feeding one catalog, following the existing `samples/` pattern |

## Open items to resolve before implementation starts

- Confirm or correct the assumption stated above about the intended meaning of the truncated "For
  h[angfire]..." sentence.
- Decide whether the ASP.NET Core enrichment/description gap (Hangfire has no human-readable description,
  same as raw MCP method names without `[Description]`) should also offer an optional attribute for
  ASP.NET Core actions, for description/category override, mirroring the Hangfire optional-attribute
  design — likely yes, for consistency, but not decided here.
- Decide the exact shape of "supplied credentials" for the endpoint authorization policy (bearer token
  passed through invocation context vs. a resolved `ClaimsPrincipal` vs. both) — this affects the CLI/MCP
  adapters too, since they'd need a way to accept and forward credentials, which doesn't exist in either
  adapter today.
- Decide whether catalog rebuild/refresh (for Hangfire jobs registered after startup, or new endpoints
  added via dynamic routing) is in scope now or deferred — recommendation above is to defer, consistent
  with `OperationCatalog`'s current immutable-snapshot design.
