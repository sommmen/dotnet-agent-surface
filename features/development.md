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

The following should be resolved with prototypes before the public API is stabilized:

- exact target framework matrix and language-version constraints;
- attribute-only discovery versus first-class delegate registration;
- command hierarchy and collision rules for categories;
- TOON library or renderer choice and the exact JSON/TOON output contract;
- output projection metadata, default truncation limits, and `--fields` behavior;
- safety-level vocabulary and non-interactive confirmation semantics;
- dependency-injection abstraction without forcing a specific container;
- schema representation in the core model;
- support for overloaded methods and operation aliases;
- build-time generation versus an explicit generator command;
- package names and namespace conventions.

## Definition of an initial usable release

The first usable release should let a consumer annotate a simple service method, build a validated catalog, expose it through both an MCP stdio host and a CLI, and generate a complete skill directory. A test must demonstrate that all outputs originate from the same descriptor and invoke the same service through the same policy pipeline.
