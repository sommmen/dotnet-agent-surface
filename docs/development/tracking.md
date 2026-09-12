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
| Trusted invocation context & authorization | Coordinator | Discovery satellites, Shared policy pipeline | Completed | Core now carries a trusted host-supplied `OperationInvocationContext` (resolved `ClaimsPrincipal` plus optional credential) to `IOperationInvocationPolicy`; CLI/MCP forward it without accepting operation JSON as identity input. `AspNetCoreEndpointAuthorizationPolicy` evaluates `IAuthorizeData`/`[Authorize]`/`[AllowAnonymous]` metadata through the host's real authorization services, honors `IAllowAnonymous`, and fails closed without an authenticated principal. Focused tests cover protected, anonymous, and allowed endpoint execution (`ec03ec4`; work item 23 in [`testing-and-open-decisions.md`](testing-and-open-decisions.md#planned-work-items)). |
| Skill generator — reference-based `SKILL.md` | Coordinator | Skill generator, Explicit skill generator command | Completed | `SkillReferenceGenerator` now renders a compact `SKILL.md` (YAML frontmatter, "when to use" paragraph, discovery instructions, command index, 2-3 examples, link to `references/commands.md`) with full per-operation detail moved under `references/commands.md`, sharded into `references/commands/<category-slug>.md` (plus `_root.md` for uncategorized operations) once the catalog exceeds `SkillGenerationOptions.CategoryShardThreshold` (default 20); `Generate`/`IsCurrent`/`check` detect and remove orphaned category files. `SkillGeneratorCommand.ExecuteAsync` keeps working unchanged (v1 wrapping) when no `SkillGenerationOptions` is supplied. Added size-budget, threshold-boundary, orphaned-file, and frontmatter tests in `SkillReferenceGeneratorTests`; full solution test suite passed (113 tests). |
| Hangfire vNext — P0 migration and host contract | Coordinator | Discovery satellites | Completed | Stable `AddHangfireRecurringOperations(...)` list/trigger operations, the direct eager-recurring migration guide, an offline CLI composition sample with generated-skill snapshot checking, and a shared fail-closed `OperationConfirmation` contract for CLI and MCP are delivered. `RegisterJobs<TJobBase>` is the primary class-registration API; `AddHangfireJobTypes(...)` remains the advanced, caller-directed alternative. |
| Hangfire vNext — P1 reflection diagnostics and AOT boundary | Coordinator | Hangfire vNext P0 | Completed (SQL follow-up) | Discovery exposes immutable reports for registration, skips, warnings, and strict failures; validates assembly and metadata inputs. The legacy `EnrichAsync` callback was removed outright in [issue #28](https://github.com/sommmen/dotnet-agent-surface/issues/28) rather than kept as a startup-time throw (see `CHANGELOG.md`). Reflection registration is explicitly unsupported in trimmed and NativeAOT applications pending source generation; stable recurring operations are unaffected. SQL Server provider compatibility tests are deferred to credential-free opt-in [issue #22](https://github.com/sommmen/dotnet-agent-surface/issues/22). |
| Hangfire vNext — P2 package and operational polish | Coordinator | Hangfire vNext P1 | Completed (SQL follow-up) | Added Hangfire package-consumption and operational guidance, package source mapping/token handling, production-wiring guidance, and a publish-workflow package/version summary. `Hangfire.Core` 1.8.18's `netstandard2.0` dependency on vulnerable `Newtonsoft.Json` 11.0.1 is remediated by an explicit compatible `Newtonsoft.Json` 13.0.3 reference; it is not suppressed. SQL Server provider compatibility remains tracked in credential-free opt-in [issue #22](https://github.com/sommmen/dotnet-agent-surface/issues/22). |
| Hangfire vNext — SQL Server compatibility suite ([issue #22](https://github.com/sommmen/dotnet-agent-surface/issues/22)) | Coordinator | Hangfire vNext P1 | Completed | Added `tests\DotNetAgentSurface.Hangfire.SqlServer.Tests\`, a separate, opt-in test project covering `AddHangfireRecurringOperations(...)` against real `Hangfire.SqlServer` 1.8.18 storage: storage-lazy catalog construction, recurring listing, triggering/enqueue, unknown-job rejection, and Hangfire/storage error translation. Uses `Testcontainers.MsSql` to provision an ephemeral, credential-free SQL Server container and `Xunit.SkippableFact` to skip cleanly (not fail) unless `DOTNETAGENTSURFACE_HANGFIRE_SQLSERVER_TESTS=1` is set and Docker is available. Verified locally: all 5 tests pass against a live Testcontainers-provisioned SQL Server instance; the suite skips cleanly by default (5 skipped, 0 failed) in a full solution `dotnet test` run; the existing offline `DotNetAgentSurface.Hangfire.Tests` project (25 tests) is unaffected. |
| Hangfire vNext — HF-2 class-based job discovery | Coordinator | Hangfire vNext — HF-1 stable recurring operations | Completed | `RegisterJobs<TJobBase>(...)` and `RegisterJobs<TJobBase, TOptions>(...)` conventionally discover concrete job classes and enqueue one-off executions through `IBackgroundJobClient`. The P0 API decision table identifies `RegisterJobs` as the primary class-discovery API and `AddHangfireJobTypes(...)` as the advanced caller-directed alternative. |
| Hangfire vNext — brownfield job hierarchy support ([issue #28](https://github.com/sommmen/dotnet-agent-surface/issues/28)) | Coordinator | Hangfire vNext — HF-2 class-based job discovery | Completed | `RegisterJobs<TJobBase>`/`RegisterJobs<TJobBase, TOptions>`'s constraints were relaxed from the concrete `HangfireJob`/`HangfireJobWithOptions<TOptions>` base classes to the new `IHangfireJob`/`IHangfireJob<TOptions>` interfaces (which those base classes now implement), so pre-existing/brownfield job hierarchies — including CRTP-style base classes with constructor parameters, e.g. a Hangfire `PerformContext` — can opt in without rewriting their inheritance chain. Discovery remains reflection-only; Hangfire's own `JobActivator` constructs job instances at execution time, so constructor parameters were never a runtime constraint. See [`docs/development/hangfire-recurring-migration.md`](./hangfire-recurring-migration.md#adopting-a-pre-existing-brownfield-job-hierarchy) for a worked example and new brownfield-focused tests in `HangfireJobRegistrationCatalogBuilderExtensionsTests`. Re-prioritized from issue #28's original P1 to P0 by the maintainer based on real-world (OPG Platform) integration feedback; the issue's original P0 (version-signal/changelog work) was redirected to `[Obsolete]`-member cleanup plus `CHANGELOG.md` (see the P1/P2 rows below), and package-discoverability (originally P2) was deferred as out of scope until the package is published beyond GitHub Packages. |
| Hangfire vNext — attribute-based (duck-typed) job discovery ([PR #56](https://github.com/sommmen/dotnet-agent-surface/pull/56)) | Coordinator | Hangfire vNext — brownfield job hierarchy support | Completed | Added `[HangfireJob]`, `RegisterAttributeJobs(...)`, and `RegisterAttributeWorkflowTests(...)`: an inherited-attribute, duck-typed alternative to `RegisterJobs`/`RegisterAllOptionsJobs` for job hierarchies that already implement an unrelated marker interface, so they can opt into discovery without changing their interface list. Shares the same conventional `Execute`/`ExecuteAsync` shape matching and `HangfireJobDiscoveryDisposition` diagnostics as the other class-registration APIs. Documented in `README.md`, `CHANGELOG.md`, and [`docs/development/hangfire-recurring-migration.md`](./hangfire-recurring-migration.md#attribute-based-duck-typed-discovery). |
| Hangfire vNext — obsolete-member cleanup and changelog ([issue #28](https://github.com/sommmen/dotnet-agent-surface/issues/28)) | Coordinator | Hangfire vNext — P1 reflection diagnostics and AOT boundary | Completed | Removed the sole remaining `[Obsolete]` member repo-wide, `HangfireJobRegistrationOptions.EnrichAsync` (and its startup-time throw), rather than adding another forwarding shim. Added [`CHANGELOG.md`](../../CHANGELOG.md) with a breaking-changes entry per preview version from `0.1.14-preview` through the current version, so a consumer upgrading across many versions has one place to read what changed. Deliberately did **not** add a compatibility shim for the already-removed `AddHangfireRecurringJobs` API — issue #20 and the recurring-jobs migration guide already cover that migration. |
| SignalR satellite — stable send operations | Coordinator | Discovery satellites | Completed | Design documented in [`signalr-vnext.md`](../../features/signalr-vnext.md): a `DotNetAgentSurface.SignalR` package exposing stable `send-signalr-all`/`send-signalr-group`/`send-signalr-user`/`send-signalr-client` operations over `IHubContext<THub>`, `Confirm` safety by default, and no Hub-method reflection or connection/group enumeration. Package, `DotNetAgentSurface.SignalR.Tests` (11/11 passing), the hosted `DotNetAgentSurface.Samples.SignalR` sample, and the final documentation re-verification are all delivered. |
| XML doc comments as operation metadata | Coordinator | Core catalog, Skill generator — reference-based `SKILL.md` | Phase 1 and selected Phase 2 complete | Source operation and parameter descriptions from C# XML documentation comments so `[AgentOperation(...)]` no longer has to repeat text the `/// <summary>` already carries, mirroring how Swashbuckle and `Microsoft.AspNetCore.OpenApi` feed XML docs into an OpenAPI document. Consumers opt in with `<GenerateDocumentationFile>true</GenerateDocumentationFile>`. Full design in [`xml-doc-comments.md`](xml-doc-comments.md). |

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
- [x] Add `[HangfireJob]`/`RegisterAttributeJobs(...)`/`RegisterAttributeWorkflowTests(...)` as an attribute-based, duck-typed discovery alternative to `RegisterJobs`/`RegisterAllOptionsJobs` ([PR #56](https://github.com/sommmen/dotnet-agent-surface/pull/56)).

### 9. Trusted invocation context & authorization

- [x] Define a Core invocation-context contract that securely carries a host-authenticated `ClaimsPrincipal` and optional credential (not raw JSON input) through to `IOperationInvocationPolicy`.
- [x] Forward that context from the CLI and MCP adapters; MCP also uses the SDK transport's authenticated request principal.
- [x] Add `AspNetCoreEndpointAuthorizationPolicy` (`IOperationInvocationPolicy`) to evaluate `IAuthorizeData`/`[Authorize]`/`[AllowAnonymous]` metadata through the host's real `IAuthorizationService`, replacing the unconditional endpoint exception.
- [x] Deny by default when no authenticated caller context exists; arbitrary JSON principals or tokens are never accepted.
- [x] Add focused tests proving protected endpoints execute only with a valid forwarded context and remain denied otherwise.

### 10. Skill generator — reference-based `SKILL.md`

Full design in [`skill-generation.md`](skill-generation.md).

- [x] `SkillGenerationOptions` (name/description/executable/`CategoryShardThreshold`, default 20) exists on `SkillReferenceGenerator.Generate`/`IsCurrent`; `SkillGeneratorCommand.ExecuteAsync` accepts an optional trailing `SkillGenerationOptions? generationOptions` and threads it through to `ExecuteGenerate`/`ExecuteCheck`, falling back to the legacy parameterless overloads (derived from the output directory name) when `null`, so v1 command wrapping still works unchanged. Parsed `--name`/`--description`/`--executable` CLI flags remain out of scope for this change (hosts pass `SkillGenerationOptions` in-process).
- [x] `RenderSkill` produces `SKILL.md` with YAML frontmatter (`name`, `description`, `executable`), a short "when to use" statement, `--help` discovery instructions, a compact command index (per-category summary once sharded, otherwise one line per operation), 2-3 examples, and a link to `references/commands.md`; full per-operation detail lives under `references/commands.md` instead.
- [x] Added `Generate_keeps_SKILL_md_within_the_documented_size_budget_for_large_catalogs`, asserting generated `SKILL.md` stays under 150 lines / ~4 KB for a synthetic 60-operation, 6-category catalog.
- [x] Category-based sharding of `references/commands.md` into `references/commands/<category-slug>.md` (plus `_root.md` for uncategorized operations) once `catalog.Operations.Count > CategoryShardThreshold` or more than one distinct category exists, reusing `OperationCatalog.GetCategorySegments` for slugging. Added `Generate_shards_commands_only_once_the_operation_count_threshold_is_exceeded` covering exactly-at-threshold (20, unsharded) and one-above (21, sharded) boundaries.
- [x] `IsCurrent`/`check` detect orphaned files under `references/commands/` (categories renamed or removed since the last generation) in addition to missing/mismatched-content checks; `Generate` removes them (plus now-empty directories). Added `Generate_removes_orphaned_category_files_and_check_reports_them_as_stale`.
- [x] Added `Generate_writes_valid_YAML_frontmatter_with_non_empty_name_and_description` asserting `SKILL.md` starts with `---`-delimited frontmatter containing non-empty `name`/`description`/`executable` fields.
- [x] `SkillReferenceGeneratorTests` and `SkillGeneratorCommandTests` (generation-options pass-through, `check` currency with custom options) pass; full solution test suite (`DotNetAgentSurface.Core.Tests`, `DotNetAgentSurface.CommandLine.Tests`, `DotNetAgentSurface.AspNetCore.Tests`, `DotNetAgentSurface.Hangfire.Tests`) passed with 113/113 tests green.

### 11. SignalR satellite — stable send operations

Full design in [`signalr-vnext.md`](../../features/signalr-vnext.md).

- [x] Design and document the stable `send-signalr-all`/`send-signalr-group`/`send-signalr-user`/`send-signalr-client` operations, the `IHubContext<THub>`-based API shape, and the authorization/safety boundary (agent invocation caller vs. `HubCallerContext.User`) before writing implementation.
- [x] Add the `DotNetAgentSurface.SignalR` package (`net10.0`, `Microsoft.AspNetCore.App` framework reference) with a typed `AddSignalRSendOperations<THub>(...)` extension registering the four stable send operations against `OperationCatalogBuilder`, `Confirm` safety by default. Input validation (non-empty `methodName` and, per-target, `groupName`/`userId`/`connectionId`) runs before the `IHubClients` target selector is resolved, so an invalid request never touches SignalR routing.
- [x] Add focused tests in `DotNetAgentSurface.SignalR.Tests` covering catalog registration, operation metadata, target routing (all/group/user/client), method/argument forwarding, validation, result shape, cancellation, and option overrides. All 11 tests pass.
- [x] Add a hosted `DotNetAgentSurface.Samples.SignalR` sample mapping a Hub and composing the catalog from a typed `IHubContext<THub>`; document it in `samples/README.md`.
- [x] Re-verify `signalr-vnext.md` against the delivered operation names, option/type names, result properties, default safety/category, package target, and test/sample coverage once implementation lands. Verified: operation names, `SignalRSendResult` (Target/MethodName/ArgumentCount/AcceptedAt), `SignalROperationsOptions` (`Category` = `"SignalR"`, `SendSafetyLevel` = `Confirm`), and host composition example all match the shipped code. Full solution build and test run are green (`DotNetAgentSurface.SignalR.Tests`: 11/11; full solution: 0 errors, all suites passing except the pre-existing opt-in `DotNetAgentSurface.Hangfire.SqlServer.Tests`, which skip cleanly by design).


### 12. XML doc comments as operation metadata

Full design in [`xml-doc-comments.md`](xml-doc-comments.md).

Phase 1 — Core reader and optional description:

- [x] Add an `AgentOperationAttribute(string name)` constructor and make `Description` nullable, so an operation can be annotated with a name only. Existing two-argument call sites keep compiling and behaving identically; record the nullable-annotation change in [`CHANGELOG.md`](../../CHANGELOG.md).
- [x] Move `OperationCatalog.CreateDescriptor(...)`'s empty-description guard off the raw attribute value and onto the resolved description, so a name-only `[AgentOperation("name")]` reaches the fallback chain instead of throwing `OperationCatalogException` during discovery. An explicitly supplied blank string keeps failing; an empty resolved description becomes a diagnostic, and a build failure only under strict mode.
- [x] Add the `IOperationDocumentationSource` seam plus the `OperationDocumentation` record (`Summary`, `Remarks`, `Parameters`, `Returns`), with `Null`, `Composite`, and `Xml` implementations. `XmlOperationDocumentationSource` loads `<AssemblyName>.xml` lazily (explicit paths or probed from `AppContext.BaseDirectory`), caches per assembly, and never throws on missing/malformed files.
- [x] Implement documentation comment ID (`M:`) generation from `MethodInfo`, covering nested types (`+` → `.`), generic type arity (`` `n ``), generic method arity (``` ``n ```), positional generic parameters, arrays, pointers, and `ref`/`out`/`in` (`@`). Fail closed by returning `null` so an unresolvable signature is just a lookup miss.
- [x] Implement text normalization: strip `///` indentation, collapse whitespace, flatten `<see cref>`/`<paramref>`/`<c>`/`<code>`/`<para>`/`<list>` to plain text, decode entities once, normalize line endings, and escape Markdown table-hostile characters — so `SkillReferenceGenerator.IsCurrent` stays byte-for-byte stable across machines and operating systems.
- [x] Resolve descriptions in `OperationDescriptor` by precedence: explicit attribute/registration text → `<summary>` → `<summary>` + `<remarks>` when `IncludeRemarks` is set → satellite-synthesized text → empty. Explicit text always wins, so adopting the feature cannot change an existing catalog's output.
- [x] Wire the seam through `OperationCatalog.Discover(...)` and `OperationCatalogBuilder.UseDocumentation(...)`/`UseXmlDocumentation()` as opt-in parameters rather than a static global, so tests stay isolated.
- [x] Resolve the trivial `<inheritdoc/>` case (exactly one documented base or interface member); defer `cref`/`path` resolution.
- [x] Tests: table-driven ID generation verified against the test assembly's own compiler-produced `.xml`; normalization across multi-line/CRLF/inline-tag inputs; precedence; and a missing-`.xml` catalog that matches the no-documentation baseline.

Phase 2 — parameter descriptions and surfaces:

- [x] Add `OperationParameterDescriptor.Description`, populated from `<param name="...">`.
- [x] Render parameter descriptions in the `SKILL.md` reference parameter tables, CLI option help, and per-property `description` fields in `OperationSchemaGenerator` output; re-baseline the affected `schemas.json` assertions.
- [x] Add `OperationCatalog.DocumentationDiagnostics` surfacing operations that resolved to an empty description, plus an opt-in `RequireDescription` strict mode that fails the catalog build. Reporting these diagnostics as warnings from `skill generate`/`check` remains planned.
- [x] Tests: determinism with CRLF-normalized XML input, asserting `IsCurrent` stays true. The extended size-budget test over an XML-sourced catalog remains planned.

Phase 3 — satellites and the optional source generator:

- [ ] ASP.NET Core: controller-action and Minimal API handler `<summary>` replaces the synthesized `"Invokes ASP.NET Core {method} /{route}."` text.
- [ ] Hangfire: class-based job discovery resolves the job method's `<summary>`; storage-discovered recurring jobs keep their synthesized text.
- [ ] MCP-native: `DescriptionAttribute` keeps winning; `<summary>` fills the gap when it is absent.
- [ ] Evaluate a source-generated `IOperationDocumentationSource` for trimmed/Native AOT hosts, folded into the [explicit-metadata registration and source generation work item](testing-and-open-decisions.md#explicit-metadata-registration-and-source-generation--work-item). It must stay opt-in and complementary to the runtime reader, which is the only path that sees referenced assemblies and delegate-registered third-party types.

Documentation:

- [ ] Document the `<GenerateDocumentationFile>true</GenerateDocumentationFile>` requirement, `CS1591` guidance, and the single-file/trimming/container caveats in `README.md`; update [`core-catalog.md`](core-catalog.md) and [`skill-generation.md`](skill-generation.md); update the task-tracker sample to drop duplicated description strings and regenerate its committed skill snapshot.
