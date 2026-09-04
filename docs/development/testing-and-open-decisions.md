# Testing strategy and open design decisions

Cross-surface testing goals and the design questions still open (or already
resolved) for the initial release. See the
[development hub](../../DEVELOPMENT.md) for how this fits with the rest of
the project.

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

End-to-end tests should run generated CLI commands and an MCP client against hosts backed by the same sample service. The current suite lives in [`tests/DotNetAgentSurface.Core.Tests`](../../tests/DotNetAgentSurface.Core.Tests).

## Open design decisions

The initial prototypes and external research have now resolved the design direction below. Decisions marked **work item** are intentional follow-up tasks before the public API is considered stable.

### Target frameworks and language version — resolved

Target the libraries at `net10.0;netstandard2.0`. The repository's .NET 10 applications get the least-friction experience, while `netstandard2.0` keeps the core usable from older .NET implementations. The `net472` sample validates the downlevel path. Use the latest supported C# compiler (`LangVersion=latest`) during this preview and keep source TFM-uniform where practical; compatibility helpers are preferable to scattered conditional compilation. Do not add more TFMs until a consumer requires them.

### Discovery and registration — work item

Keep `[AgentOperation]` attributes as the convention for discoverable public methods, and add a first-class fluent builder for registrations that cannot be expressed naturally with attributes (delegates, aliases, or runtime composition). The intended shape is an `OperationCatalogBuilder` that can consume attributed services and explicit registrations, similar in spirit to EF Core's convention-plus-fluent configuration model. Add focused diagnostics and registration tests before treating the builder as public API.

This escape hatch is exactly what the three discovery satellites below build on: Hangfire recurring jobs, ASP.NET Core endpoints, and native MCP-SDK tools each discover operations at runtime through their own source-specific APIs and register them via `OperationCatalogBuilder.Add(...)` rather than `[AgentOperation]`, without adding new dependencies to `Core`. See [`discovery-satellites.md`](../../features/discovery-satellites.md) for the full viability assessment and design, and the "Discovery satellites" work item below.

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

Assessed as viable in [`discovery-satellites.md`](../../features/discovery-satellites.md); no source generator (the information each source needs is only fully known at runtime, not compile time), and ASP.NET Core discovery uses `IApiDescriptionGroupCollectionProvider` (`ApiExplorer`) rather than a hand-rolled `EndpointDataSource` walk. All three feed the existing catalog through `OperationCatalogBuilder.Add(...)`; none changes `Core`'s "scan only explicitly annotated methods" philosophy or adds a dependency to `Core`, `CommandLine`, or `Mcp`'s existing outward-facing adapter. See work items 19–24 below for the concrete backlog.

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

### Packaging and publishing readiness — resolved

The library is published to NuGet-compatible feeds while the public API is still pre-1.0. The repository's `/dotnet-packable` skill conventions were applied and the packability gate has since been removed:

14. **Completed** — added `Nerdbank.GitVersioning` for automated git-height-based semantic versioning. A root [`version.json`](../../version.json) drives the version (public release ref `main`); `Directory.Build.props` no longer sets a manual `<Version>` property.
15. **Completed** — added `DotNet.ReproducibleBuilds` for deterministic builds, SourceLink integration, and normalized source paths, with `ContinuousIntegrationBuild` enabled for GitHub Actions.
16. **Completed** — the repository-level `README.md` and package icon are packed at the NuGet package root; package metadata, XML documentation, `.nupkg`, and `.snupkg` contents are verified as part of `dotnet pack`.
17. **Completed** — the `IsPackable=false` preview gate no longer exists in `src/`; every library under `src/` (`Core`, `CommandLine`, `Mcp`, `Hangfire`, `AspNetCore`) is packable by default. Samples and tests remain `IsPackable=false` (they are not meant to be published).
18. **Completed** — [`.github/workflows/publish.yml`](../../.github/workflows/publish.yml) checks out with `fetch-depth: 0`, restores/builds/tests before packing, and publishes to GitHub Packages on every push to `main` (plus NuGet.org for stable releases or an explicit `publish_nuget` manual run), using `--skip-duplicate` for repeatable publishing. Packages have been published and consumed by at least one external project (OPG Platform, `DotNetAgentSurface 0.1.14-preview`). See the root [README.md](../../README.md#testing-prerelease-packages) for the GitHub Packages consumption steps and [README.md](../../README.md#local-package-workflow) for a credential-free local `dotnet pack`-based workflow.

Discovery satellites (see [`discovery-satellites.md`](../../features/discovery-satellites.md)):

19. **Completed** — added `IsIdempotent` registration support and retained bound delegate targets for closure and instance-delegate discovery contracts (`e66520f`).
20. **Completed** — added native MCP-SDK tool ingestion through `AddMcpServerTools(...)` (`0d7a834`). It maps name, `DescriptionAttribute`/title, idempotency, and destructive safety. `ReadOnly` and `OutputSchemaType` have no `OperationDescriptor` equivalent and remain MCP-projection concerns. Tests cover catalog, CLI, MCP, invocation, and skill output.
21. **Completed** — added the `DotNetAgentSurface.Hangfire` satellite and `AddHangfireRecurringJobs(...)` (`8f05d49`). It discovers recurring jobs from Hangfire storage, triggers through the supplied manager, defaults to confirmation, and supports optional metadata enrichment with `Hangfire.InMemory` coverage.
22. **Completed** — added the `DotNetAgentSurface.AspNetCore` ApiExplorer satellite (`be7a47a`). It discovers MVC and Minimal API endpoints, invokes anonymous route delegates in-process, and catalogs protected endpoints while denying their execution by default until a trusted caller-context contract exists.
23. **Deferred — authorization caller context.** Add `AspNetCoreEndpointAuthorizationPolicy` (`IOperationInvocationPolicy`) only after Core, CLI, and MCP define a trusted invocation-context/credential-forwarding contract. Capture `IAuthorizeData`/`[Authorize]`/`[AllowAnonymous]` metadata, evaluate supplied credentials through the host's real `IAuthorizationService`, and deny by default when no valid caller context exists. The current satellite safely catalogs protected endpoints but refuses execution; it must not accept arbitrary injected principals or tokens.
24. **Completed — cross-source validation.** Focused Core/MCP (8), ASP.NET Core (2), and Hangfire (3) test suites pass, confirming each source populates the shared catalog and its relevant projections. `DotNetAgentSurface.Samples.Hangfire` was added and manually validated end to end: it discovers two recurring jobs through `AddHangfireRecurringJobs`, demonstrates per-job metadata enrichment, and confirms invocation only enqueues the job (verified via `IMonitoringApi`) without starting a `BackgroundJobServer`. `DotNetAgentSurface.Samples.AspNetCore` was extended with `AddFromApiExplorer`, combining its attribute-discovered `TaskTrackerService` operations with two new Minimal API routes (`/demo/ping` anonymous, `/demo/secret` protected); manual validation confirmed `aspnet_get_demo_ping` executes successfully and `aspnet_get_demo_secret` is cataloged but its invocation is denied (HTTP 400) because Core does not yet forward a trusted caller context — this is the expected deny-by-default behavior from work item 23, not a gap in this item. Both samples are documented in `samples/README.md`.
25. **Completed — Hangfire vNext HF-1.** Superseded the eager `AddHangfireRecurringJobs(...)` catalog with `AddHangfireRecurringOperations(storage, jobManager, ...)`, which adds the stable `list-recurring-hangfire` and `trigger-recurring-hangfire` operations without querying storage at catalog-construction time. Invocation uses the current recurring-job storage state; list output is deterministically ordered and trigger rejects unknown job ids before touching Hangfire. The stable `TriggerRecurringHangfireResult` acknowledgement contains `JobId`, `Status`, `EnqueueId`, and `TriggeredAt`: configured-storage mode returns `enqueued`/`rejected` and deliberately only acknowledges enqueueing, while explicit isolated in-memory execution returns `succeeded` only after the job completes. Isolated execution is cancellation/timeout bounded and uses an empty global filter provider so default Hangfire automatic retries cannot delay an observable failure. Eleven focused `Hangfire.InMemory` tests cover the runtime contract, and `DotNetAgentSurface.Samples.Hangfire`/`samples/README.md` demonstrate configured-storage enqueueing, rejection, and isolated execution. Deferred vNext milestones: HF-2 class discovery/options binding, HF-3 fail-closed safety enforcement, HF-4 migration/source generation/diagnostics, and HF-5 broader docs/sample polish. Legacy non-`V2` managers still cannot report a stale job that disappears between lookup and `Trigger(...)`, because the legacy method has no return value.
26. **Completed — Hangfire vNext HF-2.** Added `RegisterJobs<TJobBase>(this OperationCatalogBuilder, ...)` and `RegisterJobs<TJobBase, TOptions>(this OperationCatalogBuilder, ...)` in `HangfireJobRegistrationCatalogBuilderExtensions`, catalog-time class discovery that resolves each job from DI and invokes it through `IBackgroundJobClient` (never Hangfire storage) at catalog-construction time. Discovery finds every concrete, non-abstract, non-open-generic class assignable to `TJobBase` exactly once, including inherited and closed-generic types; the execution method is selected from `HangfireJob`/`HangfireJobWithOptions<TOptions>`'s conventional shapes or a caller-supplied `MethodSelector`. Options-based jobs bind JSON input to `TOptions` before enqueueing; malformed input fails the operation (`Succeeded == false`) without enqueueing. Naming/base-type/method-selection collisions and invalid `MethodSelector` results raise `HangfireJobRegistrationDiagnostic`s — permissive by default (skip and continue), or `OperationCatalogException` when `StrictValidation = true`. `EnrichAsync` reuses existing metadata conventions and never downgrades an operation's dangerous/safety classification. 21 focused tests cover discovery, exclusion, ambiguity (both diagnostics modes), options binding (success and failure), inherited/closed-generic discovery, custom method selection, and deterministic ordering. `DotNetAgentSurface.Samples.Hangfire` and `samples/README.md` demonstrate both APIs end to end alongside the existing predicate-based `AddHangfireJobTypes` satellite. Deferred: HF-3 fail-closed Confirm/Dangerous enforcement, HF-4 migration/source-generation/diagnostics, and HF-5 broader docs/sample polish. Not yet exercised: real DI-container/`BackgroundJobServer` activation (tests use a recording `IBackgroundJobClient` fake plus a minimal service-provider stub, consistent with HF-1's `Hangfire.InMemory`-only scope); a future milestone could add an end-to-end integration test that resolves and runs a discovered job through a real `BackgroundJobServer`. The plan's note that this discovery shape should "later be usable by a source generator" is unaddressed by this slice (tracked under HF-4).

## Definition of an initial usable release

The first usable release should let a consumer annotate a simple service method, build a validated catalog, expose it through both an MCP stdio host and a CLI, and generate a complete skill directory. A test must demonstrate that all outputs originate from the same descriptor and invoke the same service through the same policy pipeline.
