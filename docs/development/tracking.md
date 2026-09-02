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
| Packaging and docs | Coordinator | Sample hosts, Framework compatibility | Completed | Added `samples/DotNetAgentSurface.Samples.LegacyDesktop` (`legacy-desktop-cli`, `net472`), a self-contained `GreeterService` exercised through `OperationCatalog`/`OperationCommandLineAdapter` against the `netstandard2.0` build of Core/CommandLine; built and run successfully against the real .NET Framework 4.7.2 runtime installed on this machine (`greet`, `count-letters`, and the required-parameter error path all verified). Added `src/Directory.Build.props` with shared NuGet metadata (`Version=0.1.0-preview.1`, MIT `PackageLicenseExpression`, `PackageReadmeFile`, source-linked repository info, symbol packages, `GenerateDocumentationFile`) applied to `Core`/`CommandLine`/`Mcp`; added per-project `PackageId`/`Description`; added a root [LICENSE](../../LICENSE) (MIT) and updated `README.md`'s license section. Verified `dotnet pack` end-to-end (correct nuspec, embedded README, per-TFM dependency groups, XML docs). Packages remain `IsPackable=false` by default pending an actual decision to publish to a feed. Full solution builds clean with 0 warnings/errors and all 23 tests pass. |
| Fluent registration and aliases | Coordinator | Core catalog | Completed | Fluent prototype builder/delegate registration, canonical names, aliases, and deterministic overload-collision tests completed; validated with the Core test suite. |
| Category routing and diagnostics | Coordinator | Fluent registration and aliases, CLI adapter | Completed | Nested category command chains, full-path collision diagnostics, stable help ordering, and README documentation completed; 54 Core tests passed. |
| AXI output contract | Coordinator | CLI adapter | Completed | Renderer abstraction, Toon-backed TOON, explicit JSON mode, projection, truncation, `--fields`, and structured empty/error output completed; 61 focused tests passed. |
| Explicit skill generator command | Coordinator | Skill generator | Completed | `generate`/`check` command wrapping `SkillReferenceGenerator` completed; MSBuild integration remains opt-in future work. |
| AXI best-effort compliance | Coordinator | CLI adapter, AXI output contract | Completed | AXI exit codes, per-command help/flag validation, fast host help/version paths, explicit idempotent no-op semantics, no prompts, and strict stdout/stderr separation completed; 58 Core and 7 command-line tests passed. |
| Packaging and publishing readiness | Coordinator | Packaging and docs | Completed | Nerdbank.GitVersioning, reproducible builds, packaged README/icon, packability gate removal, and a full-history NuGet publishing workflow completed and validated. |
| Discovery satellites (MCP-native, Hangfire, ASP.NET Core) | Coordinator | Shared invocation, CLI adapter, MCP adapter | Completed | Added `IsIdempotent` registration support (`e66520f`); native MCP-SDK tool ingestion via `AddMcpServerTools(...)` (`0d7a834`); the `DotNetAgentSurface.Hangfire` satellite and `AddHangfireRecurringJobs(...)` (`8f05d49`); and the `DotNetAgentSurface.AspNetCore` ApiExplorer satellite (`be7a47a`). Cross-source validation confirmed Core/MCP (8), ASP.NET Core (2), and Hangfire (3) focused test suites pass and each source populates the shared catalog; `DotNetAgentSurface.Samples.Hangfire` and the extended `DotNetAgentSurface.Samples.AspNetCore` were manually validated end to end. Protected ASP.NET Core endpoints are cataloged but denied execution by default pending a trusted caller-context contract (see next milestone). |
| Trusted invocation context & authorization | Coordinator | Discovery satellites, Shared policy pipeline | Not started | Define a Core invocation-context contract that securely carries authenticated caller identity/credentials to `IOperationInvocationPolicy`, so the host's real ASP.NET Core authentication/authorization services can evaluate it. Add `AspNetCoreEndpointAuthorizationPolicy` to enforce `IAuthorizeData`/`[Authorize]`/`[AllowAnonymous]` metadata against that context, replacing today's deny-by-default `UnauthorizedAccessException` for protected endpoints (work item 23 in [`testing-and-open-decisions.md`](testing-and-open-decisions.md#planned-work-items)). |
| Skill generator — reference-based `SKILL.md` | Coordinator | Skill generator, Explicit skill generator command | Not started | The shipped v1 generator inlines every operation's description directly into `SKILL.md`, so it grows with the catalog instead of staying small. Plan recorded in [`skill-generation.md`](skill-generation.md): add `SkillGenerationOptions` (name/description/executable), move `SKILL.md` to a small frontmatter + compact index, relocate full command detail under `references/`, and shard `references/commands/<category>.md` once a catalog exceeds a fixed operation-count threshold, with orphaned-file detection added to `check` mode. |

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
- [x] Publish versioned packages with compatibility documentation — `Core`, `CommandLine`, and `Mcp` now carry full NuGet metadata (`PackageId`, `Description`, MIT `PackageLicenseExpression`, `PackageReadmeFile`, source-linked `RepositoryUrl`, symbol packages) via a shared `src/Directory.Build.props`; versioning starts at `0.1.0-preview.1` to signal pre-release/exploratory status. `dotnet pack` verified end-to-end (nuspec, README, XML docs, and per-TFM dependency groups all correct in the produced `.nupkg`/`.snupkg`). Packages are intentionally left `IsPackable=false` by default (flip to `true`, or drop an explicit `-p:IsPackable=true`, when actual publishing to a feed is decided) since no package has been published yet and no feed/CI publish step exists. The repository root [LICENSE](../../LICENSE) now contains the MIT text and `README.md`'s license section links to it.

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

- [ ] Add `SkillGenerationOptions` (skill name, description, executable name, category shard threshold) to `SkillReferenceGenerator.Generate`/`IsCurrent`, and matching options to `SkillGeneratorCommand`.
- [ ] Rewrite `RenderSkill` so `SKILL.md` contains YAML frontmatter, a short "when to use" statement, discovery instructions, a compact index (never full per-operation detail), and a link to `references/commands.md`; move output under `<output>/references/`.
- [ ] Add a size-budget test asserting generated `SKILL.md` stays under the documented line/byte budget regardless of catalog size.
- [ ] Add category-based sharding of `references/commands.md` into `references/commands/<category-slug>.md` once the operation-count threshold is exceeded, reusing the same category-segment convention as `OperationCommandLineAdapter`.
- [ ] Extend `IsCurrent`/`check` to detect orphaned files under `references/commands/` (categories renamed or removed since the last generation) in addition to the existing missing/mismatched-content checks; make `Generate` remove them.
- [ ] Update `SkillReferenceGeneratorTests` for the new structure, frontmatter, sharding threshold boundary, and orphaned-file detection.
