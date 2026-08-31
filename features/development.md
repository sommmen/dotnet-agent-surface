# Development plan

This document captures the proposed architecture and implementation direction for .NET Agent Surface. It is a design baseline, not a description of an already released API.

## Goals

The framework should let an implementer describe an application operation once and obtain:

1. an MCP tool;
2. a CLI command;
3. a generated agent skill and command reference.

All three surfaces must share discovery, binding, schema, invocation, policy, and documentation metadata so they cannot silently drift.

## Non-goals for the initial release

- Exposing unannotated public methods automatically.
- Hosting an MCP stdio loop inside a desktop GUI process.
- Replacing application-level authentication or authorization systems.
- Designing a general remote procedure call transport.
- Generating arbitrary natural-language skill content with an LLM at build time.

## Proposed architecture

```text
Application services
       |
Annotated methods or registered delegates
       |
Operation discovery and validation
       |
OperationCatalog
       |
Shared binder, policy pipeline, and invoker
       |
       +-- MCP adapter
       +-- CLI adapter
       +-- Skill/reference generator
```

### Core abstractions

#### `AgentOperationAttribute`

Marks methods that are intentionally available to agents and command-line users. Initial metadata should include:

- name;
- description;
- category;
- safety level;
- optional examples.

Names should be stable identifiers suitable for both MCP tools and CLI commands. Discovery must reject duplicate names and invalid signatures with actionable diagnostics.

#### `OperationDescriptor`

An immutable description of one operation. It should contain:

- operation name, description, and category;
- `MethodInfo` or invocation delegate;
- declaring/service type when applicable;
- parameter descriptors, including nullability, defaults, and required state;
- input JSON Schema;
- declared and effective return types;
- safety metadata and examples.

Framework-specific MCP and CLI types should not leak into this core model.

#### `OperationCatalog`

Discovers or receives operations, validates them, and exposes a stable ordered collection of descriptors. Ordering must be deterministic so generated artifacts are reproducible.

Reflection scanning should only include explicitly annotated methods. Registration by delegate may be added for applications that cannot annotate their service classes.

#### Binding and invocation

A common invocation layer should:

1. accept named, JSON-compatible inputs;
2. bind and validate values against operation parameters;
3. resolve the operation target through an abstraction compatible with dependency injection;
4. execute synchronous or asynchronous methods;
5. normalize results and errors;
6. enforce authorization, safety, and confirmation policies.

Adapters should translate transport-specific input and output only. They must not implement separate business validation or authorization rules.

## Adapters

### MCP

The MCP adapter will use the official MCP C# SDK and create tools from catalog descriptors. The stdio host must reserve stdout exclusively for protocol traffic and route logs and diagnostics to stderr.

The adapter should preserve:

- tool names and descriptions;
- input schemas;
- required and default parameter behavior;
- structured errors;
- cancellation where supported.

MCP hosting should be provided as a separate executable or an easy-to-compose host library rather than embedded in a WinForms or WPF process.

### CLI

The CLI adapter will use System.CommandLine to generate commands and options from the same descriptors. It should provide:

- predictable category and command naming;
- required options and defaults equivalent to MCP binding;
- discoverable `--help` output;
- machine-readable TOON output by default plus an explicit JSON mode;
- meaningful exit codes;
- non-interactive confirmation flags for destructive operations;
- cancellation propagation where practical.

Human-friendly formatting can be layered on later, but automation must have a stable output mode from the start.

#### AXI and token efficiency

Generated CLIs should support the [Agent eXperience Interface (AXI)](https://github.com/kunchenguid/axi/blob/main/.agents/skills/axi/SKILL.md) conventions. This is an output-boundary concern: operation inputs, schemas, and normalized results remain JSON-compatible internally, while the CLI can render compact [TOON](https://toonformat.dev/) by default for agent-oriented use and retain an explicit JSON mode for interoperability and scripting.

The CLI adapter and operation metadata should make it possible to:

- emit minimal list projections by default and expose `--fields` for additional fields;
- truncate large values with their total size and an actionable `--full` escape hatch;
- include cheap aggregate counts or derived status when these prevent a likely follow-up call;
- represent successful empty results explicitly rather than emitting ambiguous blank output;
- return structured, actionable errors and meaningful exit codes without leaking dependency details;
- treat already-satisfied mutations as successful no-ops where the operation declares idempotent semantics;
- reject unknown commands, arguments, and options before invocation;
- complete every operation non-interactively through arguments and options;
- keep stdout structured and route progress and diagnostics to stderr;
- provide a compact no-argument discovery view with common commands and next steps.

AXI presentation policy must not be inferred solely from arbitrary DTO shapes. `OperationDescriptor` will need optional output metadata for summary fields, expandable or truncatable fields, aggregate information, and idempotency. Safe defaults should still work when implementers provide none.

Ambient session hooks described by AXI may be offered later as an explicit, idempotent installation feature. They are complementary to generated skills: hooks provide compact live context at a per-session token cost, while skills load on demand and carry no cost in unrelated sessions. Hook installation must never occur as a side effect of normal commands.

### Skill and reference generation

Skill generation should be deterministic and template-driven. A generated skill directory is expected to contain:

```text
skills/
  <skill-name>/
    SKILL.md
    references/
      commands.md
      schemas.json
```

`SKILL.md` should include:

- YAML frontmatter;
- a concise statement of when to use the skill;
- the CLI executable name;
- command-discovery instructions;
- a small set of representative examples;
- a relative link to the generated command reference.

`commands.md` should document every generated command, parameter, default, safety classification, and example. `schemas.json` should contain the machine-readable schemas used by the catalog. Files should have stable ordering and line endings, and generation should avoid rewriting unchanged output.

## Safety and security

Safety is part of the shared invocation contract rather than adapter-specific behavior.

Initial design requirements:

- scan only explicitly annotated methods;
- reject ambiguous or unsupported signatures during catalog creation;
- classify read-only, mutating, and destructive operations;
- make destructive operations opt in to an explicit confirmation policy;
- expose hooks for authentication and authorization before invocation;
- avoid logging secrets or raw sensitive parameter values by default;
- return controlled errors rather than reflection or stack-trace details;
- keep protocol output separate from diagnostics.

The framework should provide extension points for policy but must not pretend to supply an application's identity model.

## Type and method support

The first implementation should prefer a deliberately small, testable surface:

- primitives, enums, nullable values, arrays, and simple DTOs;
- JSON-compatible parameters and results;
- instance methods resolved from a service provider;
- synchronous methods;
- `Task`, `Task<T>`, `ValueTask`, and `ValueTask<T>`;
- cancellation tokens treated as infrastructure parameters rather than user input.

Unsupported patterns should fail during catalog construction, not during the first live invocation. Candidate later features include streams, progress reporting, polymorphic DTOs, and richer file inputs.

## Target frameworks and dependencies

The intended compatibility baseline is .NET Framework 4.6.2 or later plus modern .NET. In practice this is implemented as `net10.0;netstandard2.0` multi-targeting for `DotNetAgentSurface.Core`, `DotNetAgentSurface.CommandLine`, and `DotNetAgentSurface.Mcp`: `netstandard2.0` is consumable from .NET Framework 4.6.1+ (a superset of the 4.6.2+ goal) and avoids juggling several raw `net46x` TFMs individually. `ModelContextProtocol` 2.2.0 was confirmed to already target `netstandard2.0` alongside `net8.0`/`net9.0`/`net10.0`, so it was not a blocker.

Dependencies actually used:

- `ModelContextProtocol` for the MCP adapter;
- `System.Text.Json` for JSON Schema generation and (de)serialization (referenced explicitly on `netstandard2.0`, where it isn't part of the shared framework; picked up transitively via `ModelContextProtocol` on `Mcp`);
- `PolySharp` (source-generator, build-time only) to polyfill C# language features (`init`, `record`, `CallerArgumentExpression`) on `netstandard2.0`.

The CLI adapter is hand-rolled directly on top of `System.CommandLine`-style parsing conventions rather than taking a package dependency; JSON Schema generation is done directly against `System.Text.Json.Nodes` rather than via `NJsonSchema`.

Two modern-only BCL APIs had no netstandard2.0 equivalent and were replaced with small hand-written, TFM-uniform helpers (no `#if`, so the exact same source compiles and behaves identically everywhere):

- `ArgumentNullException.ThrowIfNull` / `ArgumentException.ThrowIfNullOrWhiteSpace` → internal `Guard.ThrowIfNull` / `Guard.ThrowIfNullOrWhiteSpace` (using `CallerArgumentExpression` for the parameter name), in `DotNetAgentSurface.Core/Guard.cs`. The single call site living in the separate `Mcp` assembly was inlined instead of exposing `Guard` via `InternalsVisibleTo`.
- `NullabilityInfoContext` (unavailable on `netstandard2.0`) → internal `NullabilityReader.IsNullable(ParameterInfo | PropertyInfo)`, in `DotNetAgentSurface.Core/NullabilityReader.cs`, which reads the compiler-emitted `NullableAttribute`/`NullableContextAttribute` metadata directly via `CustomAttributeData`. This metadata is present in IL regardless of target framework, so the algorithm produces identical results on every TFM. Validated against real `NullabilityInfoContext` output across 12 hand-picked cases (value types, reference types, generics, oblivious code) before adoption, and indirectly covered in CI by the existing schema-generator tests (nullable/required property and parameter detection).

A couple of other modern-only members were swapped for their down-level equivalents in the same spirit: `ValueTask.FromResult(x)` → `new ValueTask<T>(x)`, and the `char`-overload of `string.Join('\n', ...)` → the `string`-overload `string.Join("\n", ...)`.

The core package should keep adapter dependencies isolated so consumers pay only for the surfaces they use. A likely package/project split is:

```text
DotNetAgentSurface.Core
DotNetAgentSurface.Mcp
DotNetAgentSurface.CommandLine
DotNetAgentSurface.Skills
```

Names remain provisional.

## Delivery milestones

## Implementation tracking

Implementation began on 2026-08-30 from `main` at `4f52f9a7fd1e252eae081d3efc5f969cad4f7c8f`.

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
| Packaging and docs | Coordinator | Sample hosts, Framework compatibility | Completed | Added `samples/DotNetAgentSurface.Samples.LegacyDesktop` (`legacy-desktop-cli`, `net472`), a self-contained `GreeterService` exercised through `OperationCatalog`/`OperationCommandLineAdapter` against the `netstandard2.0` build of Core/CommandLine; built and run successfully against the real .NET Framework 4.7.2 runtime installed on this machine (`greet`, `count-letters`, and the required-parameter error path all verified). Added `src/Directory.Build.props` with shared NuGet metadata (`Version=0.1.0-preview.1`, MIT `PackageLicenseExpression`, `PackageReadmeFile`, source-linked repository info, symbol packages, `GenerateDocumentationFile`) applied to `Core`/`CommandLine`/`Mcp`; added per-project `PackageId`/`Description`; added a root [LICENSE](../LICENSE) (MIT) and updated `README.md`'s license section. Verified `dotnet pack` end-to-end (correct nuspec, embedded README, per-TFM dependency groups, XML docs). Packages remain `IsPackable=false` by default pending an actual decision to publish to a feed. Full solution builds clean with 0 warnings/errors and all 23 tests pass. |
| Fluent registration and aliases | Coordinator | Core catalog | Completed | Fluent prototype builder/delegate registration, canonical names, aliases, and deterministic overload-collision tests completed; validated with the Core test suite. |
| Category routing and diagnostics | Coordinator | Fluent registration and aliases, CLI adapter | Completed | Nested category command chains, full-path collision diagnostics, stable help ordering, and README documentation completed; 54 Core tests passed. |
| AXI output contract | Coordinator | CLI adapter | Completed | Renderer abstraction, Toon-backed TOON, explicit JSON mode, projection, truncation, `--fields`, and structured empty/error output completed; 61 focused tests passed. |
| Explicit skill generator command | Coordinator | Skill generator | Completed | `generate`/`check` command wrapping `SkillReferenceGenerator` completed; MSBuild integration remains opt-in future work. |
| AXI best-effort compliance | Coordinator | CLI adapter, AXI output contract | Completed | AXI exit codes, per-command help/flag validation, fast host help/version paths, explicit idempotent no-op semantics, no prompts, and strict stdout/stderr separation completed; 58 Core and 7 command-line tests passed. |
| Packaging and publishing readiness | Coordinator | Packaging and docs | Completed | Nerdbank.GitVersioning, reproducible builds, packaged README/icon, packability gate removal, and a full-history NuGet publishing workflow completed and validated. |

> Orchestration note: this environment does not expose VS Code session-creation controls, so the coordinator is implementing and tracking the single-repository dependency chain directly in the current repository worktree. The intended final integration branch is `feature/initial-agent-surface`; no worker branches have been created.

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
- [x] Publish versioned packages with compatibility documentation — `Core`, `CommandLine`, and `Mcp` now carry full NuGet metadata (`PackageId`, `Description`, MIT `PackageLicenseExpression`, `PackageReadmeFile`, source-linked `RepositoryUrl`, symbol packages) via a shared `src/Directory.Build.props`; versioning starts at `0.1.0-preview.1` to signal pre-release/exploratory status. `dotnet pack` verified end-to-end (nuspec, README, XML docs, and per-TFM dependency groups all correct in the produced `.nupkg`/`.snupkg`). Packages are intentionally left `IsPackable=false` by default (flip to `true`, or drop an explicit `-p:IsPackable=true`, when actual publishing to a feed is decided) since no package has been published yet and no feed/CI publish step exists. The repository root [LICENSE](../LICENSE) now contains the MIT text and `README.md`'s license section links to it.

## Testing strategy

Testing should emphasize equivalence across surfaces:

- one catalog operation yields matching MCP, CLI, and reference metadata;
- the same logical input binds to the same invocation arguments;
- defaults, nullability, validation, and errors behave consistently;
- authorization and safety policies execute regardless of adapter;
- generated files are deterministic and snapshot-testable;
- AXI-oriented TOON and explicit JSON output represent equivalent normalized results;
- default projections, truncation notices, empty states, and errors remain compact and actionable;
- MCP stdout contains no logs or incidental text;
- target-framework builds catch accidental use of unavailable APIs.

End-to-end tests should run generated CLI commands and an MCP client against hosts backed by the same sample service.

## Open design decisions

The initial prototypes and external research have now resolved the design direction below. Decisions marked **work item** are intentional follow-up tasks before the public API is considered stable.

### Target frameworks and language version — resolved

Target the libraries at `net10.0;netstandard2.0`. The repository's .NET 10 applications get the least-friction experience, while `netstandard2.0` keeps the core usable from older .NET implementations. The `net472` sample validates the downlevel path. Use the latest supported C# compiler (`LangVersion=latest`) during this preview and keep source TFM-uniform where practical; compatibility helpers are preferable to scattered conditional compilation. Do not add more TFMs until a consumer requires them.

### Discovery and registration — work item

Keep `[AgentOperation]` attributes as the convention for discoverable public methods, and add a first-class fluent builder for registrations that cannot be expressed naturally with attributes (delegates, aliases, or runtime composition). The intended shape is an `OperationCatalogBuilder` that can consume attributed services and explicit registrations, similar in spirit to EF Core's convention-plus-fluent configuration model. Add focused diagnostics and registration tests before treating the builder as public API.

This escape hatch is exactly what the three discovery satellites below build on: Hangfire recurring jobs, ASP.NET Core endpoints, and native MCP-SDK tools each discover operations at runtime through their own source-specific APIs and register them via `OperationCatalogBuilder.Add(...)` rather than `[AgentOperation]`, without adding new dependencies to `Core`. See [`discovery-satellites.md`](./discovery-satellites.md) for the full viability assessment and design, and the "Discovery satellites" work item below.

### Command hierarchy and collisions — work item

Categories map to deterministic command groups: an operation in category `tasks` is invoked as `tasks <operation>`, with uncategorized operations remaining at the root. Normalize comparisons case-insensitively, sort categories and operations ordinally, and reject ambiguous effective paths rather than choosing by reflection order. Discovery should emit a diagnostic or warning for duplicate or shadowed metadata where the host has a warning channel; the CLI must return a structured error. Document the command chain and collision behavior in `README.md` (the current flat adapter is only the prototype).

### TOON and output contract — work item

Use [Cysharp/ToonEncoder](https://github.com/Cysharp/ToonEncoder) initially (`ToonEncoder` 2.x is available on NuGet), but hide it behind a small output-renderer boundary such as `IAgentOutputRenderer`. Keep normalized results and schemas represented internally as JSON-compatible values; render TOON only at the CLI stdout boundary. JSON remains an explicit machine-readable escape hatch (for example, `--output json`), while TOON is the AXI-oriented default. Pin and test the exact encoder options and newline/escaping contract so a future official implementation can replace the dependency without changing operation metadata.

### Projection, truncation, and `--fields` — work item

Follow AXI best effort: default list/detail projections should be compact, include enough identifying state to act, and include total counts for collections where available. Large text should be previewed rather than silently omitted, with its total size and a `--full` escape hatch when truncated. Add output metadata describing available fields, a configurable default truncation limit (initially 1,000 characters), and `--fields a,b,c` to request additional properties. Unknown fields must fail clearly; empty results must be explicit; errors must use the same structured output contract and exit codes.

### Safety vocabulary and non-interactive confirmation — resolved and documented

`Safe` means no confirmation is normally needed, `Confirm` means the host may require explicit approval, and `Dangerous` means a destructive or high-impact operation that the default policy protects. `DangerousOperationConfirmationPolicy` is the reference policy: it calls an injected confirmation delegate and denies when no approval is available. CLI and MCP hosts must never block waiting for a prompt; non-interactive execution supplies confirmation through host configuration or fails with an actionable denial. This vocabulary describes policy intent, not a security boundary or authorization system; authorization remains a separate policy concern.

### Dependency injection — resolved

Support `Microsoft.Extensions.DependencyInjection` conventions through `IServiceProvider` and constructor injection. Do not introduce a container-specific abstraction or require a particular container package in Core. Consumers may adapt another container to `IServiceProvider`; the invocation pipeline depends only on the BCL interface.

### Schema representation — resolved and documented

The core model represents an operation's inputs as parameter metadata (`OperationParameterDescriptor`), and `OperationSchemaGenerator` projects that metadata into a JSON Schema document. The schema describes an object whose properties are named operation inputs; requiredness comes from optional/default and nullable metadata; DTOs, arrays, enums, and nested records are expanded recursively with cycle protection. The schema is the interchange contract for CLI/MCP inputs, while the descriptor remains the source of truth. Keep schema generation deterministic and do not make JSON Schema nodes part of the invocation model itself.

### Overloads and aliases — work item

Add explicit operation aliases and a deterministic overload strategy. Prefer distinct stable operation names in the public catalog; if overloads are supported, require an alias or signature-based disambiguation rather than relying on CLR method ordering. Test duplicate aliases, case-insensitive collisions, generated help, MCP tool names, and invocation binding.

### Generation model — resolved with future extension

Make the main entry point an explicit generator command that invokes the existing `SkillReferenceGenerator` and supports check or stale detection. Build-time or MSBuild integration is future work, not a prerequisite for the initial release; it may be added later as an opt-in integration that does not force generation on every build.

### Packages and namespaces — resolved

Use normal .NET conventions: `DotNetAgentSurface.*` package IDs and namespaces, one package per independently consumable surface (`Core`, `CommandLine`, `Mcp`, and later `Skills` if it becomes a separate runtime/package concern). Keep adapter dependencies isolated and preserve the existing source-linking, XML documentation, and symbol-package metadata.

### Discovery satellites (Hangfire, ASP.NET Core, native MCP tools) — work item

Assessed as viable in [`discovery-satellites.md`](./discovery-satellites.md); no source generator (the information each source needs is only fully known at runtime, not compile time), and ASP.NET Core discovery uses `IApiDescriptionGroupCollectionProvider` (`ApiExplorer`) rather than a hand-rolled `EndpointDataSource` walk. All three feed the existing catalog through `OperationCatalogBuilder.Add(...)`; none changes `Core`'s "scan only explicitly annotated methods" philosophy or adds a dependency to `Core`, `CommandLine`, or `Mcp`'s existing outward-facing adapter. See work items 19–24 below for the concrete backlog.

### Planned work items

1. Add fluent/delegate registration alongside attributes.
2. Add category-based command routing, deterministic collision diagnostics, and README command-chain documentation.
3. Add the renderer abstraction and Cysharp/ToonEncoder-backed TOON output with explicit JSON mode.
4. Add AXI-oriented projections, configurable truncation, `--fields`, `--full`, total counts, and structured empty/error output.
5. Add overload and alias support with deterministic naming rules.
6. Add an explicit generator CLI command; evaluate optional MSBuild integration later.

### AXI best-effort compliance

The CLI surface should follow [AXI](https://github.com/kunchenguid/axi/blob/main/.agents/skills/axi/SKILL.md) conventions as a best-effort target. AXI defines ergonomic standards for agent-facing CLIs. The library itself is framework-agnostic; AXI compliance lives in the CLI adapter layer (`DotNetAgentSurface.CommandLine`). The following work items capture the remaining AXI gaps:

7. Implement AXI exit codes: `0` for success (including idempotent no-ops), `1` for errors, `2` for usage errors. Structured errors go to stdout with actionable suggestions; never leak dependency internals.
8. Implement AXI `--help` on every subcommand and per-subcommand flag validation. Unknown flags must be rejected with the command's valid flag list inlined in the error message.
9. Make the no-argument route content-first: show the most relevant live operation output rather than a full usage manual, identify the executable and its purpose, and include only a few contextual next-step commands when they are useful.
10. Add a lightweight version fast path: `-v`, `-V`, and `--version` print only the version and exit `0` without constructing the catalog, service provider, or command graph.
11. Implement idempotent operation semantics: applying an already-applied state must succeed with exit code `0` and a brief acknowledgement rather than fail. Expose adapter/operation metadata so hosts can opt into this behavior without guessing from result text.
12. Ensure no interactive prompts exist in the CLI surface. Every operation must be completable with flags alone; missing required values fail immediately with a clear error.
13. Route stderr for debug/progress logging only; never mix log messages into stdout. Evaluate optional session-hook and generated agent-skill integration separately because those require explicit host/user installation intent.

### Packaging and publishing readiness

The library should be ready to publish to NuGet before the public API is stabilized. The current `Directory.Build.props` already sets package metadata but is missing automated versioning, reproducible build guarantees, and proper artifact bundling. Apply the repository's `/dotnet-packable` skill conventions:

14. Add `Nerdbank.GitVersioning` for automated git-height-based semantic versioning. Create a root `version.json` (version `0.1`, public release ref `main`) and remove the manual `<Version>` property from `Directory.Build.props`.
15. Add `DotNet.ReproducibleBuilds` for deterministic builds, SourceLink integration, and normalized source paths. Enable `ContinuousIntegrationBuild` for GitHub Actions and Azure DevOps.
16. Pack the repository-level `README.md` and an optional package icon at the NuGet package root, and verify package metadata, XML documentation, `.nupkg`, and `.snupkg` contents.
17. Remove the current `IsPackable=false` preview gate from `Directory.Build.props` only once the packaging validation and publishing pipeline are ready.
18. Add a GitHub Actions deployment workflow that checks out with `fetch-depth: 0`, restores/builds/tests before packing, publishes to GitHub Packages first and NuGet.org second, and uses `--skip-duplicate` for repeatable publishing.

Discovery satellites (see [`discovery-satellites.md`](./discovery-satellites.md)):

19. **Completed** — added `IsIdempotent` registration support and retained bound delegate targets for closure and instance-delegate discovery contracts (`e66520f`).
20. **Completed** — added native MCP-SDK tool ingestion through `AddMcpServerTools(...)` (`0d7a834`). It maps name, `DescriptionAttribute`/title, idempotency, and destructive safety. `ReadOnly` and `OutputSchemaType` have no `OperationDescriptor` equivalent and remain MCP-projection concerns. Tests cover catalog, CLI, MCP, invocation, and skill output.
21. **Completed** — added the `DotNetAgentSurface.Hangfire` satellite and `AddHangfireRecurringJobs(...)` (`8f05d49`). It discovers recurring jobs from Hangfire storage, triggers through the supplied manager, defaults to confirmation, and supports optional metadata enrichment with `Hangfire.InMemory` coverage.
22. **Completed** — added the `DotNetAgentSurface.AspNetCore` ApiExplorer satellite (`be7a47a`). It discovers MVC and Minimal API endpoints, invokes anonymous route delegates in-process, and catalogs protected endpoints while denying their execution by default until a trusted caller-context contract exists.
23. **Deferred — authorization caller context.** Add `AspNetCoreEndpointAuthorizationPolicy` (`IOperationInvocationPolicy`) only after Core, CLI, and MCP define a trusted invocation-context/credential-forwarding contract. Capture `IAuthorizeData`/`[Authorize]`/`[AllowAnonymous]` metadata, evaluate supplied credentials through the host's real `IAuthorizationService`, and deny by default when no valid caller context exists. The current satellite safely catalogs protected endpoints but refuses execution; it must not accept arbitrary injected principals or tokens.
24. **Partially completed — cross-source validation.** Focused Core/MCP (8), ASP.NET Core (2), and Hangfire (3) test suites pass, confirming each source populates the shared catalog and its relevant projections. Remaining: add a small Hangfire sample and extend/add an ASP.NET Core sample once work item 23 provides an authorization policy, then use them for end-to-end manual validation.

## Definition of an initial usable release

The first usable release should let a consumer annotate a simple service method, build a validated catalog, expose it through both an MCP stdio host and a CLI, and generate a complete skill directory. A test must demonstrate that all outputs originate from the same descriptor and invoke the same service through the same policy pipeline.
