# Development tracking

The ongoing-work tracker for .NET Agent Surface: delivery milestones, their
current status, and the per-milestone task breakdown. See the
[development hub](../../DEVELOPMENT.md) for the rest of the design
documentation this tracker implements against.

Implementation began on 2026-08-30 from `main` at `4f52f9a7fd1e252eae081d3efc5f969cad4f7c8f`.

## Milestone status

| Milestone | Owner | Dependency | Status | Validation / handoff |
|---|---|---|---|---|
| Core catalog | Coordinator | None | Completed | Catalog discovery and diagnostics tests passed (`4` tests) |
| Shared invocation | Coordinator | Core catalog | Completed | Binding, sync/async invocation, and shared policy pipeline tests passed |
| Shared policy pipeline | Coordinator | Shared invocation | Completed | `IOperationInvocationPolicy` runs before binding and invocation; dangerous operations can require explicit confirmation |
| Schema generation | Coordinator | Core catalog | Completed | Stable schemas include nullable reference-type metadata, `IEnumerable<T>` array support, and nested DTO/record object schemas (property-level `required`/`nullable`, cycle-safe recursion, `additionalProperties: false`); focused tests passed |
| CLI adapter | Coordinator | Shared invocation, schema generation | Completed | CLI help, binding, and malformed-input tests passed |
| MCP adapter | Coordinator | Shared invocation, schema generation | Completed | Official `ModelContextProtocol` 2.2.0 adapter and stdio host implemented; tool discovery and invocation tests passed. Package compatibility verified for `net10.0`; stdio transport reserves stdout for protocol traffic. |
| Skill generator | Coordinator | Core catalog, schema generation | Completed | Deterministic output and stale-file check tests passed |
| Adapter policy equivalence | Coordinator | Shared policy pipeline, CLI adapter, MCP adapter | Completed | Same denying `IOperationInvocationPolicy` proven to block invocation identically (denial message propagated, underlying operation never executed) across direct Core invocation, the CLI adapter, and the MCP adapter (`23` tests total) |
| MCP adapter error/annotation coverage | Coordinator | MCP adapter | Completed | Added focused tests for cancellation, missing required (non-nullable) arguments, reflected-operation exceptions, and `ReadOnlyHint`/`DestructiveHint` tool annotations |
| Sample hosts | Coordinator | CLI adapter, MCP adapter | Completed | Added `samples/` with a shared `TaskTrackerService` (list/add/complete/remove-task, one `Dangerous` op) plus thin `tasktracker-cli` and `tasktracker-mcp` hosts that discover the same catalog. Smoke-tested: CLI `--help`/`add-task`/`list-tasks`/error path; MCP stdio `initialize` and `tools/list` round trips (clean stdout, correct schemas and safety annotations). Full suite still 23/23 passing. |
| Framework compatibility | Coordinator | Core catalog | Completed | `Core`, `CommandLine`, and `Mcp` now multi-target `net10.0;netstandard2.0` (covers .NET Framework 4.6.1+, superset of the 4.6.2+ goal). Added `PolySharp` for language-feature polyfills, plus hand-written TFM-uniform `Guard` (replaces `ArgumentNullException.ThrowIfNull`/`ArgumentException.ThrowIfNullOrWhiteSpace`) and `NullabilityReader` (replaces `NullabilityInfoContext`, reading `NullableAttribute`/`NullableContextAttribute` metadata directly). Zero `#if` conditionals needed anywhere. Full solution builds clean (0 warnings/errors) across both TFMs; all 23 tests still pass; both sample hosts re-smoke-tested (CLI `--help`/`add-task`/`list-tasks`, MCP build) and still work correctly, including nullable-vs-required parameter distinction. |
| Packaging and docs | Coordinator | Sample hosts, Framework compatibility | Completed | Added `samples/DotNetAgentSurface.Samples.LegacyDesktop` (`legacy-desktop-cli`, `net472`), a self-contained `GreeterService` exercised through `OperationCatalog`/`OperationCommandLineAdapter` against the `netstandard2.0` build of Core/CommandLine; built and run successfully against the real .NET Framework 4.7.2 runtime installed on this machine (`greet`, `count-letters`, and the required-parameter error path all verified). Added `src/Directory.Build.props` with shared NuGet metadata (MIT `PackageLicenseExpression`, `PackageReadmeFile`, source-linked repository info, symbol packages, `GenerateDocumentationFile`) applied to `Core`/`CommandLine`/`Mcp`/`Hangfire`/`AspNetCore`; added per-project `PackageId`/`Description`; added a root [LICENSE](../../LICENSE) (MIT) and updated `README.md`'s license section. Verified `dotnet pack` end-to-end (correct nuspec, embedded README, per-TFM dependency groups, XML docs). Superseded by the "Packaging and publishing readiness" milestone below, which added Nerdbank.GitVersioning, removed the packability gate, and shipped the publish workflow; packages are now built and published, not merely packable. Full solution builds clean with 0 warnings/errors and all 23 tests pass. |
| Fluent registration and aliases | Coordinator | Core catalog | Completed | Fluent prototype builder/delegate registration, canonical names, aliases, and deterministic overload-collision tests completed; validated with the Core test suite. |
| Category routing and diagnostics | Coordinator | Fluent registration and aliases, CLI adapter | Completed | Nested category command chains, full-path collision diagnostics, stable help ordering, and README documentation completed; 54 Core tests passed. |
| AXI output contract | Coordinator | CLI adapter | Completed | Renderer abstraction, Toon-backed TOON, explicit JSON mode, projection, truncation, `--fields`, and structured empty/error output completed; 61 focused tests passed. |
| Explicit skill generator command | Coordinator | Skill generator | Completed | `generate`/`check` command wrapping `SkillReferenceGenerator` completed; MSBuild integration remains opt-in future work. |
| AXI best-effort compliance | Coordinator | CLI adapter, AXI output contract | Completed | AXI exit codes, per-command help/flag validation, fast host help/version paths, explicit idempotent no-op semantics, no prompts, and strict stdout/stderr separation completed; 58 Core and 7 command-line tests passed. |
| Packaging and publishing readiness | Coordinator | Packaging and docs | Completed | Nerdbank.GitVersioning, reproducible builds, packaged README/icon, packability gate removal, and a full-history NuGet publishing workflow completed and validated. |
| Discovery satellites (MCP-native, Hangfire, ASP.NET Core) | Coordinator | Shared invocation, CLI adapter, MCP adapter | Completed | Added `IsIdempotent` registration support (`e66520f`); native MCP-SDK tool ingestion via `AddMcpServerTools(...)` (`0d7a834`); the `DotNetAgentSurface.Hangfire` satellite and `AddHangfireRecurringJobs(...)` (`8f05d49`); and the `DotNetAgentSurface.AspNetCore` ApiExplorer satellite (`be7a47a`). Cross-source validation confirmed Core/MCP (8), ASP.NET Core (2), and Hangfire (3) focused test suites pass and each source populates the shared catalog; `DotNetAgentSurface.Samples.Hangfire` and the extended `DotNetAgentSurface.Samples.AspNetCore` were manually validated end to end. Protected ASP.NET Core endpoints are cataloged but denied execution by default pending a trusted caller-context contract (see next milestone). |
| Trusted invocation context & authorization | Coordinator | Discovery satellites, Shared policy pipeline | Not started | Define a Core invocation-context contract that securely carries authenticated caller identity/credentials to `IOperationInvocationPolicy`, so the host's real ASP.NET Core authentication/authorization services can evaluate it. Add `AspNetCoreEndpointAuthorizationPolicy` to enforce `IAuthorizeData`/`[Authorize]`/`[AllowAnonymous]` metadata against that context, replacing today's deny-by-default `UnauthorizedAccessException` for protected endpoints (work item 23 in [`testing-and-open-decisions.md`](testing-and-open-decisions.md#planned-work-items)). |
| Skill generator — reference-based `SKILL.md` | Coordinator | Skill generator, Explicit skill generator command | Not started | The shipped v1 generator inlines every operation's description directly into `SKILL.md`, so it grows with the catalog instead of staying small. Plan recorded in [`skill-generation.md`](skill-generation.md): add `SkillGenerationOptions` (name/description/executable), move `SKILL.md` to a small frontmatter + compact index, relocate full command detail under `references/`, and shard `references/commands/<category>.md` once a catalog exceeds a fixed operation-count threshold, with orphaned-file detection added to `check` mode. |
| Hangfire vNext — HF-1 stable recurring operations | Coordinator | Discovery satellites | Completed | Implemented [`features/hangfire-vnext.md`](../../features/hangfire-vnext.md) milestone HF-1: replaced the eager per-job `AddHangfireRecurringJobs(...)`/`HangfireDiscoveryOptions` catalog with two stable operations, `list-recurring-hangfire` and `trigger-recurring-hangfire`, registered via `AddHangfireRecurringOperations(storage, jobManager, ...)`. Both query Hangfire's recurring-job storage at invocation time (never at catalog construction), so adding/removing/renaming recurring jobs never requires rebuilding the catalog. `trigger-recurring-hangfire` returns a stable acknowledgement contract (`JobId`, `Status`, `EnqueueId`, `TriggeredAt`) and rejects unknown job ids without ever calling into Hangfire. Triggering defaults to enqueueing on the application's configured storage (`Status` = `enqueued`/`rejected`, an acknowledgement only); an opt-in `HangfireExecutionModel.ExecuteUsingIsolatedInMemoryServer` mode runs the job to completion on a short-lived, disposable in-memory Hangfire server (`Status` = `succeeded`, or a `Failure`/cancellation result on error/timeout), with global job filters (including automatic retries) intentionally excluded so a single failure surfaces immediately instead of after Hangfire's default retry backoff. 11 focused tests added/passing covering empty storage, unknown id rejection, both trigger modes, isolated success/failure/timeout, and catalog-construction independence from storage state. `DotNetAgentSurface.Samples.Hangfire` and `samples/README.md` updated to demonstrate both trigger modes end to end. HF-3 (fail-closed Confirm/Dangerous enforcement), HF-4 (migration/source-generation/diagnostics), and HF-5 (samples/documentation polish beyond what shipped here) remain **not started** — the pre-existing `AddHangfireJobTypes` class-based satellite (added before this vNext plan) is unrelated to HF-2's proposed generic-base-class API and was left unchanged. The non-`V2` `IRecurringJobManager.Trigger` fallback path also has a known limitation: because it returns `void`, a job that vanishes between lookup and trigger cannot be distinguished from a successful trigger on storage backends that only implement the legacy interface. |
| Hangfire vNext — HF-2 class-based job discovery | Coordinator | Hangfire vNext — HF-1 stable recurring operations | Completed | Implemented [`features/hangfire-vnext.md`](../../features/hangfire-vnext.md) milestone HF-2: added `RegisterJobs<TJobBase>(this OperationCatalogBuilder, ...)` and `RegisterJobs<TJobBase, TOptions>(this OperationCatalogBuilder, ...)` in `HangfireJobRegistrationCatalogBuilderExtensions`, discovering concrete, non-abstract, non-open-generic classes assignable to `TJobBase` (default: the calling assembly, overridable via explicit assemblies) exactly once regardless of inheritance depth or closed-generic instantiation. Each discovered type is registered as a stable, kebab-case-named operation (default from the type name, overridable via `NameFactory`) that resolves the job instance from DI and invokes its Hangfire-compatible execution method (`HangfireJob`'s parameterless/`CancellationToken` method, or `HangfireJobWithOptions<TOptions>`'s options-plus-`CancellationToken` method, discovered via `IsValidExecutionMethod`/`SelectMethod`, overridable via `MethodSelector`) through `IBackgroundJobClient.Create`/`Enqueue`, so operations never depend on Hangfire storage during catalog construction. Options-based jobs bind their JSON input to `TOptions` before enqueueing; malformed input fails the operation invocation (`Succeeded == false`) without enqueueing. Naming/base-type/method-selection collisions and invalid custom `MethodSelector` results are reported as `HangfireJobRegistrationDiagnostic`s: permissive mode (default) skips the offending type and continues discovery, while `StrictValidation = true` throws `OperationCatalogException` on the first diagnostic. `EnrichAsync` and `SafetyLevel`/category defaults reuse the existing operation-catalog metadata conventions and never downgrade an operation's dangerous/safety classification. 21 focused tests added/passing covering basic discovery/enqueue, options binding (success and malformed-JSON failure), exclusion (abstract/open-generic/custom-excluded), ambiguity (permissive and strict), metadata enrichment/safety non-downgrade, inherited and closed-generic discovery, custom method selector (valid overload and invalid-selector permissive/strict), and deterministic discovery ordering. `DotNetAgentSurface.Samples.Hangfire` extended with a third demo section (`SendDigestJob : HangfireJob`, `PurgeCacheJobBase : HangfireJobWithOptions<PurgeCacheOptions>`) demonstrating both APIs end to end; sample build and run verified manually. HF-3 (fail-closed Confirm/Dangerous enforcement), HF-4 (migration/source-generation/diagnostics), and HF-5 (samples/documentation polish beyond what shipped here) remain **not started**. |

> Orchestration note: this environment does not expose VS Code session-creation controls, so the coordinator is implementing and tracking the single-repository dependency chain directly in the current repository worktree. The intended final integration branch is `feature/initial-agent-surface`; no worker branches have been created.

## Milestone task breakdown

### 1. Core catalog

- Define operation attributes and descriptor models.
- Discover annotated methods from explicit assemblies or types.
- Validate names, signatures, parameters, and duplicates.
- Establish deterministic ordering.
- Add focused tests for discovery and diagnostics.

### 2. Shared invocation

- Bind named inputs and defaults.
- Resolve service instances.
- Support sync and async return shapes.
- Normalize results, cancellation, and failures.
- Add policy hooks for authorization and safety confirmation.

### 3. Schema generation

- Generate input schemas for supported parameters and DTOs.
- Define nullability and required-value behavior consistently.
- Test stable schema output on every target framework.

### 4. CLI adapter

- Generate commands and help from the catalog.
- Invoke through the shared pipeline.
- Provide JSON output, stable errors, and exit codes.
- Test help, binding, defaults, failures, and confirmation.

### 5. MCP adapter

- Generate MCP tools from catalog descriptors.
- Host over stdio with clean stdout.
- Verify schema, invocation, cancellation, errors, and logging behavior.

### 6. Skill generator

- Render `SKILL.md`, `commands.md`, and `schemas.json`.
- Derive compact discovery guidance from the same metadata as the CLI's no-argument view.
- Guarantee deterministic output and provide a check mode that detects stale committed artifacts.
- Provide a build or packaging integration point without forcing generation into every build.

### 7. Samples and packaging

- [x] Add a small service shared by MCP and CLI sample hosts — see `samples/DotNetAgentSurface.Samples.TaskTracker` plus the `tasktracker-cli` and `tasktracker-mcp` hosts.
- [x] Add a desktop or legacy .NET Framework integration example — see `samples/DotNetAgentSurface.Samples.LegacyDesktop` (`legacy-desktop-cli`, targets `net472`, consumes the `netstandard2.0` build of Core/CommandLine, built and run successfully against the real .NET Framework 4.7.2 runtime).
- [x] Publish versioned packages with compatibility documentation — `Core`, `CommandLine`, `Mcp`, `Hangfire`, and `AspNetCore` carry full NuGet metadata (`PackageId`, `Description`, MIT `PackageLicenseExpression`, `PackageReadmeFile`, source-linked `RepositoryUrl`, symbol packages) via a shared `src/Directory.Build.props`. Versioning is git-height-based via Nerdbank.GitVersioning (see [`version.json`](../../version.json)), producing prerelease identifiers such as `0.1.14-preview.g<commit>`. `dotnet pack` verified end-to-end (nuspec, README, XML docs, and per-TFM dependency groups all correct in the produced `.nupkg`/`.snupkg`). The packability gate has been removed (see "Packaging and publishing readiness" below) and [`.github/workflows/publish.yml`](../../.github/workflows/publish.yml) publishes on every push to `main`; DotNetAgentSurface packages have been published to GitHub Packages and consumed by at least one external project (OPG Platform, `0.1.14-preview`). The repository root [LICENSE](../../LICENSE) now contains the MIT text and `README.md`'s license section links to it.

### 8. Discovery satellites (MCP-native, Hangfire, ASP.NET Core)

- [x] Add `IsIdempotent` registration support and retain bound delegate targets for closure/instance-delegate discovery contracts (`e66520f`).
- [x] Add native MCP-SDK tool ingestion through `AddMcpServerTools(...)`, mapping name, `DescriptionAttribute`/title, idempotency, and destructive safety (`0d7a834`).
- [x] Add the `DotNetAgentSurface.Hangfire` satellite and `AddHangfireRecurringJobs(...)`, discovering recurring jobs from Hangfire storage and triggering them through the supplied manager with confirmation by default (`8f05d49`).
- [x] Add the `DotNetAgentSurface.AspNetCore` ApiExplorer satellite (`AddFromApiExplorer`), discovering MVC and Minimal API endpoints and invoking anonymous route delegates in-process, while cataloging protected endpoints and denying their execution by default (`be7a47a`).
- [x] Cross-source validation — confirm Core/MCP, ASP.NET Core, and Hangfire focused test suites pass and each source populates the shared catalog; manually validate `DotNetAgentSurface.Samples.Hangfire` and the extended `DotNetAgentSurface.Samples.AspNetCore` end to end.

### 9. Trusted invocation context & authorization

- [ ] Define a Core invocation-context contract that securely carries authenticated caller identity/credentials (not raw JSON input) through to `IOperationInvocationPolicy`.
- [ ] Forward that context from the CLI and MCP adapters so callers can supply credentials the host's real authentication middleware can evaluate.
- [ ] Add `AspNetCoreEndpointAuthorizationPolicy` (`IOperationInvocationPolicy`) to evaluate `IAuthorizeData`/`[Authorize]`/`[AllowAnonymous]` metadata against the invocation context through the host's real `IAuthorizationService`, replacing today's unconditional deny-by-default `UnauthorizedAccessException` for protected endpoints.
- [ ] Deny by default when no valid caller context exists; do not accept arbitrary injected principals or tokens.
- [ ] Add focused tests proving protected endpoints execute only with a valid forwarded context and remain denied otherwise.

### 10. Skill generator — reference-based `SKILL.md`

Full design in [`skill-generation.md`](skill-generation.md).

- [x] (partial) `SkillGenerationOptions` already existed on `SkillReferenceGenerator.Generate`/`IsCurrent`; `SkillGeneratorCommand.ExecuteAsync` now accepts an optional trailing `SkillGenerationOptions? generationOptions` and threads it through to `ExecuteGenerate`/`ExecuteCheck`, so hosts can pass a custom skill name/description/executable name/shard threshold without re-deriving them. Still open: parsed `--name`/`--description`/`--executable` CLI flags so a bare `generate`/`check` invocation (no host-side wiring) can set them from the command line.
- [ ] Rewrite `RenderSkill` so `SKILL.md` contains YAML frontmatter, a short "when to use" statement, discovery instructions, a compact index (never full per-operation detail), and a link to `references/commands.md`; move output under `<output>/references/`.
- [ ] Add a size-budget test asserting generated `SKILL.md` stays under the documented line/byte budget regardless of catalog size.
- [ ] Add category-based sharding of `references/commands.md` into `references/commands/<category-slug>.md` once the operation-count threshold is exceeded, reusing the same category-segment convention as `OperationCommandLineAdapter`.
- [ ] Extend `IsCurrent`/`check` to detect orphaned files under `references/commands/` (categories renamed or removed since the last generation) in addition to the existing missing/mismatched-content checks; make `Generate` remove them.
- [ ] Update `SkillReferenceGeneratorTests` for the new structure, frontmatter, sharding threshold boundary, and orphaned-file detection.
