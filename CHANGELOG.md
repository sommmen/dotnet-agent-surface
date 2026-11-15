# Changelog

This changelog exists so a consumer upgrading across many preview versions in
one jump (as happened in [issue #28](https://github.com/sommmen/dotnet-agent-surface/issues/28),
which upgraded 11 versions from `0.1.14-preview` to `0.1.25-preview`) has one
place to read what changed and, in particular, what broke — instead of having
to read every PR body individually.

Versions are the `NuGetPackageVersion` computed by
[Nerdbank.GitVersioning](https://github.com/dotnet/Nerdbank.GitVersioning)
from [`version.json`](version.json) and git height; they are stamped on every
push to `main` (see [`.github/workflows/publish.yml`](.github/workflows/publish.yml)).
Only versions with a breaking change, a notable addition, or a deprecation are
listed. Each entry links the pull request(s) that shipped it.

> **Package availability:** every push to `main` publishes a preview to both
> NuGet.org (trusted publishing) and GitHub Packages, so
> `dotnet add package DotNetAgentSurface.Mcp --prerelease` works with no extra
> feed. There is no public badge/version-discovery surface yet
> ([issue #28](https://github.com/sommmen/dotnet-agent-surface/issues/28)).

## Versioning and breaking changes before 1.0

Every version is a `0.x` preview. Until 1.0 the public API may change between
any two previews, and nothing is deprecated first: a change can break the
build or the behaviour of a consumer. Consumers should pin an exact version
(not a floating `0.*` or `*-preview*` range) and read this changelog before
bumping. Breaking changes are always listed under a **Breaking changes**
heading with the migration step. From 1.0 onward the project follows semantic
versioning and removals go through a deprecation cycle.

## Unreleased

### Fixed

- The `--fields`/`--full` rejection for text results no longer suggests `--output <toon|json>`, which cannot make field selection apply to text output either.

### Fixed and added (CLI e2e findings from dotnet-efcore-mcp, #106)

- **Output format reaches the host.** `IOperationContext.OutputFormat` (new interface member, recorded in `CompatibilitySuppressions.xml`: only a custom `IOperationContext` implementation must add it, and the library supplies the only implementation hosts receive; `null` without the flag) and `OperationInvocationContext.OutputFormat` carry the CLI's `--output`/`--format` value (`json` or `toon`). `AgentSurfaceApplication.BindOutputFormat(configKey, map)` maps it to a host configuration key before the host is built, via the new `OperationCommandLineAdapter.ReadOutputFormat`. `--fields` / `--full` with an operation that returns a string now exit 2 with `--fields is not supported for this operation's text output` instead of being silently ignored.
- **Required parameters are enforced in the CLI.** A parameter without a default value (`OperationParameterDescriptor.IsRequired`, the same set as the MCP schema's `required` list) must be supplied; otherwise exit 2 with `missing required --name`. Behaviour change: a nullable parameter without a default (`string? name`) was previously passed as `null` and is now required, as it already was over MCP; give it `= null` to keep it optional.
- **Help shows friendly types:** `string`, `integer`, `number`, `boolean`, `string[] (repeatable)`, `object (JSON: prop, ...)` and `Alpha|Beta` for enums instead of CLR names.
- **Unknown operation** exits 2 listing operation names only plus up to three did-you-mean suggestions.
- **`SensitiveKeys`.** `ConfigureSession(o => o.SensitiveKeys.Add("cursorKey"))` makes `cli session show` print `***` for that key.
- **`cli session` and `cli skill` without a subcommand** exit 2 with the usage on stderr.

### Added (CI and release hardening, #89)

- **Lowest-supported-dependency CI job** (`lowest-dependencies` in `ci.yml`). Restoring, building and testing with `-p:UseLowestSupportedDependencies=true` swaps the `PackageVersion` entries in `Directory.Packages.props` for the `Min*Version` floors declared there (ModelContextProtocol 2.3.1, Toon.DotNet 4.1.1, Microsoft.Extensions.* 10.0.0, System.Text.Json 10.0.1). Raise a floor only when the code needs something newer. Run it locally with the same switch.
- **Package validation.** `EnablePackageValidation` is on for every packable project, compared against `PackageValidationBaselineVersion` (the latest nuget.org preview, set in `src/Directory.Build.props`) whenever `dotnet pack` runs; CI packs after testing. Policy: an unintended API break fails the build. An *intentional* `0.x` break is recorded with `dotnet pack -p:ApiCompatGenerateSuppressionFile=true`, which writes a reviewed `CompatibilitySuppressions.xml` next to the project, and is listed under **Breaking changes** here; validation is never disabled. Bump the baseline (and delete the suppressions it made obsolete) after a release that should become the new reference.
- `publish.yml` now runs the tests before packing, so a red build never reaches nuget.org, adds `concurrency` (publishes are serialised, never cancelled) and `timeout-minutes`, and pins `NuGet/login` to the `v1.2.0` commit SHA.

### Fixed (session concurrency, #101)

- `FileAgentSession.Save` no longer loses updates from concurrent CLI processes. It takes a short exclusive lock on `<id>.json.lock`, re-reads the file, applies only the keys the invocation changed and replaces the file atomically. `Set` on the *same* key is still last-writer-wins.
- `IAgentSession.GetOrAdd<T>(key, factory)` and `IAgentSession.Update<T>(key, update)` are new. They are default interface members on `net10.0` (existing implementers keep compiling; the default is a non-atomic get-then-set) and extension methods on `netstandard2.0`. `InMemoryAgentSession` runs them atomically in process; `FileAgentSession` runs them under the cross-process lock against freshly read state and persists immediately (not on `Save`).

### Fixed (global options after built-in subcommands, #102)

- `cli session show|clear` and `cli skill generate|check` now accept global flags (`--session`, `--output`/`--format`, `--fields`, `--full`, `--confirm`, `--yes` and host `AddGlobalOption` flags) after the subcommand as well as before it. `skill` keeps `--output` as its alias of `--out-dir`. Unknown trailing flags still exit 2 with usage text, and `session --help` / `skill --help` list the honoured global flags. New helpers: `OperationCommandLineAdapter.RemoveGlobalFlags` and `RenderGlobalFlagHelp`.

### Added

- `DotNetAgentSurface.Hosting` package with `AgentSurfaceApplication`: one executable and
  one DI composition serving an MCP stdio server and a plain CLI (`<exe> cli <operation>`),
  with host verbs, `AddGlobalOption(name, injectAs, configKey, description)`, an opt-in
  `ICliHostedService` marker, async host disposal, stderr logging and Ctrl+C cancellation.
- `AgentOperationAttribute.Surfaces` / `OperationRegistrationOptions.Surfaces` /
  `OperationDescriptor.Surfaces` (`AgentSurfaces.All|Mcp|Cli`), `OperationCatalog.ForSurface`
  and `McpToolImportOptions.Surfaces`. The CLI adapter and skill generator hide operations
  that are not exposed on the CLI.
- CLI ergonomics: `--name=value`, bare boolean switches, `--no-<name>`, kebab-case parameter
  and operation names, `@file` and `-` values, repeatable array flags, `--format` alias,
  parameter descriptions, defaults, required markers and examples in help.
- `skill generate|check --out-dir`, `SkillGenerationOptions.GlobalOptions` and `CommandPrefix`.
- Session state (#91): `IAgentSession` and `IAgentSessionInitializer` in Core, an in-memory
  `InMemoryAgentSession` for MCP, and a file-backed `FileAgentSession` for the CLI that
  persists across invocations (`--session <id>`, `<APP>_SESSION`, then a hash of the working
  directory), written atomically after a successful run and TTL-pruned.
  `AgentSurfaceApplication.ConfigureSession`, built-in `cli session show|clear`. Never store
  secrets in a session: names and paths only.
- `OperationCommandLineAdapter.FindCommandStart` skips leading global flags, so
  `cli --connection x skill generate` and `cli --connection x --help` work; the adapter now
  maps stdin and renderer exceptions to exit code 1.

### Breaking changes

- **MCP adapter parity (#90).** `AgentOperationAttribute`/`OperationRegistrationOptions` annotation
  hints are now independent and nothing is derived from `SafetyLevel`: `McpOperationAdapter` no
  longer sets `ReadOnlyHint = (SafetyLevel == Safe)` or `DestructiveHint = (SafetyLevel == Dangerous)`;
  an unset hint is not emitted (and `Annotations` is `null` when none is set). Set `ReadOnly`,
  `Destructive`, `Idempotent`, `OpenWorld` and `Title` explicitly where you want them advertised.
- A `string` returned by an operation is now sent to MCP clients verbatim instead of as a quoted
  JSON string, other values use `JsonSerializerDefaults.Web` (camelCase) instead of default
  (PascalCase) options, and `null` yields empty content.
- Exceptions that are not `OperationException`/`McpException`/binding errors no longer expose
  `Exception.Message` to MCP clients: they become `An unexpected error occurred while running
  '<op>'. Correlation id: <id>.` and the exception is logged with that id. The CLI keeps the original
  message. Replace the behaviour with `IOperationExceptionMapper` (`OperationInvokerOptions.ExceptionMapper`).
- Input schemas are generated by `AIJsonUtilities` (as in the SDK): nullable values use
  `"type": ["string","null"]` instead of `"nullable": true`, property names follow the serializer
  options (camelCase by default) instead of the CLR name, and nested objects no longer set
  `additionalProperties: false`. `OperationInvocationContext.RequestId` is always populated by the
  invoker (a generated id when the host supplies none) and the context passed to policies is a copy.
- `Microsoft.Extensions.AI.Abstractions` and `Microsoft.Extensions.Logging.Abstractions` are new
  dependencies of `DotNetAgentSurface.Core`; `System.Text.Json` is 10.0.13.
- The CLI adapter writes `string` results verbatim instead of JSON-quoted, and no longer
  truncates them or applies `--fields` to them; `--output`, `--fields` and `--full` affect
  object results only.
- The CLI parses a value as JSON only for parameters that are not `string`, `char`, `Guid`,
  enum, date or time types; `--name 123` now binds to a `string` parameter as text.
- Multi-line operation errors keep their newlines. Set
  `OperationCommandLineOptions.FlattenOperationErrors` for the old single-line output.
- Adapter flags (`--output`, `--fields`, `--full`, `--confirm`, `--yes`) are only global
  before the operation name or when the operation has no parameter of that name; they are no
  longer stripped from anywhere in the arguments.
- Generated skills show kebab-case flag names (`--context-name`); camelCase still works.
- `SkillGeneratorCommand` accepts `--out-dir` (`--output` still works).
- `OperationCommandLineAdapter.CleanOperationError` behaviour moved behind
  `FlattenOperationErrors`; the adapter constructor gained an optional `options` parameter.
- `AddMcpServerTools` no longer maps the MCP SDK's `McpServerToolAttribute.Destructive`
  (default `true`) to `AgentSafetyLevel.Dangerous`; imported tools are `Safe` and the
  SDK hints are surfaced as MCP annotations (`Title`, `ReadOnlyHint`, `DestructiveHint`,
  `IdempotentHint`, `OpenWorldHint`) instead. Hosts that relied on the old mapping must
  opt in with `options.SafetyMapping = McpToolSafetyMapping.DestructiveIsDangerous`.
  Annotations of imported tools now mirror the SDK attribute values rather than being
  derived from the safety level.
- `OperationInvoker.InvokeAsync` no longer throws `ConfirmationPolicyMissingException`
  or any other pre-invocation exception; it returns a failed `OperationInvocationResult`
  with the actionable message, so the CLI adapter exits with code 1 and the MCP adapter
  returns `IsError = true`. Previously the exception escaped to the host.
- `DotNetAgentSurface.CommandLine` is now a plain library: the stub `Program.cs` host and
  `OutputType=Exe` were removed. Use `samples/DotNetAgentSurface.Samples.Cli` for a
  runnable host.
- `ModelContextProtocol` is now 2.3.1 and `Toon.DotNet` is now 4.1.1; consumers pinning
  older versions must align them.
- `OperationInvoker.InvokeAsync` now fails with a `ConfirmationPolicyMissingException`
  message (as a failed result, see above) when an operation's `SafetyLevel` is `AgentSafetyLevel.Confirm` or
  `AgentSafetyLevel.Dangerous` but no supplied policy implements the new
  `IConfirmationEnforcingPolicy` marker interface (`DangerousOperationConfirmationPolicy`
  is the built-in implementation). Previously, `SafetyLevel` was metadata only:
  an `OperationInvoker` constructed without a confirmation policy would
  silently execute confirm/dangerous operations unconfirmed. Every consumer
  that invokes an operation with `SafetyLevel >= Confirm` must now supply a
  policy implementing `IConfirmationEnforcingPolicy` (for example
  `DangerousOperationConfirmationPolicy`) in `OperationInvoker`'s `policies`.
  See [`docs/development/operation-confirmation.md`](docs/development/operation-confirmation.md#safetylevel-is-metadata-only)
  for details.
- `AddHangfireJobTypes(...)` and `RegisterJobs<TJobBase>`/`RegisterJobs<TJobBase, TOptions>`
  now register operations whose delegate returns the enqueued Hangfire job ID
  (a `string`) instead of discarding it. Invoking these operations through
  `OperationInvoker` now yields the job ID as `OperationInvocationResult.Value`
  instead of `null`. This is a behavioral change for any caller inspecting
  `result.Value` after invoking a job-type or `RegisterJobs<>` operation.

### Added (MCP adapter parity, #90)

- `IMcpServerBuilder.WithOperations(Action<OperationCatalogBuilder>, Action<McpOperationOptions>?)`
  and `WithOperationCatalog(OperationCatalog, ...)` register each operation as an SDK
  `McpServerTool` (`McpOperationTool`) without replacing the list/call handlers, so they compose
  with stdio/custom transports, `WithTools<T>()`, filters, prompts, resources and server
  instructions. Targets resolve from the request's service provider. `McpOperationServer` now
  uses `McpServerOptions.ToolCollection`.
- `IMcpResultRenderer` / `DefaultMcpResultRenderer` (string, `CallToolResult`, `ContentBlock`(s)
  pass-through; Web JSON otherwise; opt-in structured content and output schema).
- Core: `IOperationExceptionMapper`/`DefaultOperationExceptionMapper`, `IOperationInvocationFilter`/
  `OperationInvocationFilter`, `IOperationContext`, `IOperationProgress` (default no-op),
  `OperationInvokerOptions` (filters, mapper, logger, `OperationConfirmationMode.OperationEnforced`,
  target factory), `OperationHints`, `OperationCatalogBuilder.AddMethod`.
- Ambient parameter injection (`McpServer`, `RequestContext<CallToolRequestParams>`,
  `IProgress<ProgressNotificationValue>`, `IServiceProvider`, `ClaimsPrincipal`,
  `IOperationContext`, `IOperationProgress`), excluded from schemas.
- Elicitation-based confirmation (`McpOperationOptions.UseElicitationConfirmation`).
- `McpToolParity.Compare` to diff `tools/list` between the SDK and the adapter, and
  `McpToolImportOptions.ResolveTargetsFromServices`.
- Documented in [`docs/development/mcp-adapter.md`](docs/development/mcp-adapter.md).

### Added

- `McpToolImportOptions` with an `Include` predicate and `SafetyMapping`
  (`McpToolSafetyMapping.None` default | `DestructiveIsDangerous`), used through the new
  `AddMcpServerTools(assembly, targetFactory, configure)` overload.
- `[Description]` on operation parameters is read by the catalog and wins over the XML
  `<param>` text, which remains the fallback. `AddMcpServerTools` benefits automatically.
- Shared adapter contracts in `DotNetAgentSurface.Core`: `AgentSurface` (`Unknown`, `Mcp`,
  `Cli`), the `OperationInvocationContext.Surface` and `RequestId` init properties (set to
  `Mcp`/`Cli` by the adapters), and `OperationException`, whose message is surfaced
  verbatim (including newlines) by both adapters. `OperationInvocationResult.ErrorIsVerbatim`
  marks such failures.
- `publish.yml` now also pushes every `main` build to NuGet.org via trusted publishing.
  One-time setup of the trusted-publishing policy and the `NUGET_USER` secret on nuget.org
  is required before the first push succeeds.

- `AddAgentSurfaceFromApiExplorer` in `DotNetAgentSurface.AspNetCore` now has an
  overload accepting `Action<OperationCatalogBuilder, IServiceProvider>`, so a
  `configure` callback that adds non-HTTP operations (for example a Hangfire
  job registration or a SignalR `AddSignalRSendOperations` call that needs
  `IHubContext<THub>`) can resolve services from the application's DI
  container instead of working around the gap by building the catalog
  outside of `AddAgentSurfaceFromApiExplorer`/`RunAgentSurfaceCliAsync`.
  `RunAgentSurfaceCliAsync` passes `app.Services` to this overload. Purely
  additive: the existing `Action<OperationCatalogBuilder>` overload and every
  caller of it are unaffected.

- Root `OperationCommandLineAdapter` help now explains category drill-down with
  `<category> [<sub-category> ...] --help`, so category-only generated CLIs
  make their positional discovery and invocation model explicit. The CLI
  adapter guide also documents how hosts that opt into the separate
  `SkillGeneratorCommand` surface should advertise `generate` and `check` in
  their own root help.

- `SyntheticPerformContextFactory` and `SyntheticPerformContext` in
  `DotNetAgentSurface.Hangfire`, synthesizing a real (but otherwise
  unconnected) Hangfire `PerformContext` from just `Hangfire.Core`/
  `Hangfire.InMemory` — no running Hangfire server required.
  `RegisterWorkflowTests`/`RegisterAttributeWorkflowTests` now use this
  automatically, so a job base class whose constructor requires a
  `PerformContext`, `IJobCancellationToken`, or `CancellationToken` (a common
  pattern for jobs using Hangfire.Console-style progress logging or job
  metadata) can be registered and run through workflow tests without a
  bespoke `IServiceProvider` from the caller. `SyntheticPerformContextFactory.Create(...)`
  is also public for callers that need a synthetic `PerformContext` outside of
  workflow-test registration. Purely additive: jobs that don't request these
  types are unaffected. See
  [`docs/development/testing-and-open-decisions.md`](docs/development/testing-and-open-decisions.md#workflow-test-construction-of-performcontext-requiring-jobs--resolved).

- `McpOperationServer`'s constructor now accepts an optional
  `serverInstructions` parameter, forwarded to `McpServerOptions.ServerInstructions`
  so consumers can inject custom guidance on how MCP clients should use the
  hosted tools. Purely additive: it defaults to `null`, matching prior
  behavior, for every existing caller. See
  [`docs/development/mcp-adapter.md`](docs/development/mcp-adapter.md#custom-server-instructions).

- `RegisterJobs<TJobBase>`/`RegisterJobs<TJobBase, TOptions>`'s generic
  constraints were relaxed (not tightened) from the concrete
  `HangfireJob`/`HangfireJobWithOptions<TOptions>` base classes to the new
  `IHangfireJob`/`IHangfireJob<TOptions>` interfaces. This is purely additive:
  `HangfireJob`/`HangfireJobWithOptions<TOptions>` now implement the
  interfaces, so every existing greenfield caller keeps compiling unchanged.

- `[HangfireJob]`, `RegisterAttributeJobs(...)`, and
  `RegisterAttributeWorkflowTests(...)` in `DotNetAgentSurface.Hangfire` for
  opt-in, duck-typed discovery of class-based jobs. Existing job hierarchies
  can add the inherited attribute instead of implementing
  `IHangfireJob`/`IHangfireJob<TOptions>`; public conventional
  `Execute`/`ExecuteAsync` methods are selected and ambiguous shapes are
  reported through the existing registration diagnostics. See
  [`docs/development/hangfire-recurring-migration.md`](docs/development/hangfire-recurring-migration.md#choosing-the-right-hangfire-integration).

- `IConfirmationEnforcingPolicy` marker interface in `DotNetAgentSurface.Core`,
  implemented by `DangerousOperationConfirmationPolicy`, identifying a policy
  that enforces `AgentSafetyLevel.Confirm`/`AgentSafetyLevel.Dangerous`
  confirmation. Paired with the new `ConfirmationPolicyMissingException`
  breaking change above.
- `AddHangfireJobStatusOperations(...)` in `DotNetAgentSurface.Hangfire`, adding
  two stable operations: `continue-hangfire-job` (enqueues a follow-up job that
  runs once a given parent job ID reaches a matching state, wrapping Hangfire's
  `AwaitingState`/`ContinueJobWith` behavior) and `get-hangfire-job-status`
  (looks up a background job's current state by ID via `JobStorage`, returning
  `null` for an unknown ID, and an optional dashboard URL when a base URL is
  configured).
- `HangfireJobStatusOperations.ForJob<TJob>()` and
  `ForJob<TJob, TOptions>(TOptions?)` in `DotNetAgentSurface.Hangfire`, building
  the continuation `Job` for `AddHangfireJobStatusOperations` via the same
  "find the public Execute/ExecuteAsync method by convention" discovery
  `RegisterJobs<TJobBase>` uses internally, instead of requiring callers to
  hand-roll `GetMethod(...)` plus a null-coalescing throw at every call site.
  Throws `InvalidOperationException` unconditionally if the job type has no matching method or
  more than one candidate; unlike `RegisterJobs` (which only fails hard under `StrictValidation`),
  `ForJob` has no discovery-report/strict-mode concept and immediately surfaces ambiguity as an error.
- `HangfireJobStatusOperationsOptions.ContinuationOperationName` and
  `StatusOperationName`, letting a consumer register more than one
  continuation target from `AddHangfireJobStatusOperations` in the same
  catalog (for example one per distinct job type) by giving each call's
  `continue-hangfire-job` operation a distinct name. `StatusOperationName` can
  be set to `null` on every call after the first, since a single
  `get-hangfire-job-status` operation already looks up any job ID regardless
  of which call created it. Previously, calling `AddHangfireJobStatusOperations`
  more than once always attempted to register the same two hardcoded operation
  names and threw `OperationCatalogException` from `OperationCatalogBuilder.Build()`.
- `RegisterAllOptionsJobs(...)` in `DotNetAgentSurface.Hangfire`, a one-call,
  assembly-scanning alternative to repeated `RegisterJobs<TJobBase, TOptions>`
  calls. It discovers every concrete job that implements one closed
  `IHangfireJob<TOptions>` interface (including through
  `HangfireJobWithOptions<TOptions>`) and creates the correctly typed options
  input operation for it. A job that implements more than one closed options
  interface is skipped and reported as ambiguous; register those exceptional
  jobs explicitly with `RegisterJobs<TJobBase, TOptions>`.
- `IHangfireJob`/`IHangfireJob<TOptions>` interfaces in
  `DotNetAgentSurface.Hangfire`, so a pre-existing ("brownfield") Hangfire job
  hierarchy — including a CRTP-style base class with constructor parameters
  (for example, one that takes a Hangfire `PerformContext`) — can opt into
  `RegisterJobs<TJobBase>`/`RegisterJobs<TJobBase, TOptions>` discovery without
  rewriting its inheritance chain. See
  [`docs/development/hangfire-recurring-migration.md`](docs/development/hangfire-recurring-migration.md#adopting-a-pre-existing-brownfield-job-hierarchy)
  for a worked example. ([issue #28](https://github.com/sommmen/dotnet-agent-surface/issues/28))
- [`CHANGELOG.md`](CHANGELOG.md) itself, and a PR template checklist item
  reminding contributors to update it for breaking changes.
  ([issue #28](https://github.com/sommmen/dotnet-agent-surface/issues/28))
- `AgentOperationAttribute(string name)` single-argument constructor and
  nullable `Description`, so an operation can be annotated with a name only
  and source its description from XML documentation comments instead of
  duplicating it in the attribute. `Description` was `string`; it is now
  `string?`. Existing two-argument call sites keep compiling and behaving
  identically. Paired with `IOperationDocumentationSource`,
  `XmlOperationDocumentationSource`, `OperationCatalogBuilder.UseDocumentation(...)`/
  `UseXmlDocumentation(...)`, `OperationCatalog.Discover(IOperationDocumentationSource?, ...)`
  overloads, and `OperationCatalog.DocumentationDiagnostics` (surfacing
  operations that still resolved to an empty description). See
  [`docs/development/xml-doc-comments.md`](docs/development/xml-doc-comments.md).

### Changed

- `DotNetAgentSurface.CommandLine`'s TOON output renderer
  (`ToonAgentOutputRenderer`) switched its encoding dependency from
  [Cysharp/ToonEncoder](https://github.com/Cysharp/ToonEncoder) (net10.0-only,
  requiring a hand-rolled `netstandard2.0` fallback writer) to
  [Toon.DotNet](https://github.com/CharlesHunt/ToonDotNet) 1.7.3, which
  supports both `net10.0` and `netstandard2.0` natively. This removed the
  duplicated `netstandard2.0` TOON-writing code path; the `IAgentOutputRenderer`
  boundary and rendered output are unchanged for callers on either target
  framework. `System.Text.Json` is bumped to `10.0.1` for the `netstandard2.0`
  build to match Toon.DotNet's own dependency. See
  [`docs/development/testing-and-open-decisions.md`](docs/development/testing-and-open-decisions.md#toon-and-output-contract--resolved).

- `Hangfire.Core`/`Hangfire.SqlServer` bumped from `1.8.18` to the latest
  stable `1.8.25`, and `Hangfire.InMemory` bumped from `0.9.0` to the latest
  stable `1.0.0`, across `DotNetAgentSurface.Hangfire` and its test/sample
  projects. The `Newtonsoft.Json` 13.0.3 pin remains: `Hangfire.Core` 1.8.25's
  `netstandard2.0` dependency group still selects vulnerable `Newtonsoft.Json`
  11.0.1 transitively. `Hangfire.InMemory` 1.0.0 removed the deprecated
  `DisableJobSerialization` option and changed the default `IdType`; neither is
  referenced by this repository, so no source changes were needed.

### Removed

- `HangfireJobRegistrationOptions.EnrichAsync` — the last remaining
  `[Obsolete]`-marked member in the codebase. It threw at catalog-construction
  time in every version since `0.1.22-preview` (PR #23) and had no remaining
  callers; it has now been deleted outright instead of kept as a permanent
  forwarding shim. Use the synchronous `HangfireJobRegistrationOptions.Enrich`
  callback instead. ([issue #28](https://github.com/sommmen/dotnet-agent-surface/issues/28))

## 0.1.26-preview

PR: [#27](https://github.com/sommmen/dotnet-agent-surface/pull/27) — `test(hangfire): add opt-in SQL Server recurring-operation compatibility coverage`

### Breaking changes

- None. Adds a separate, opt-in `DotNetAgentSurface.Hangfire.SqlServer.Tests`
  project covering `AddHangfireRecurringOperations(...)` against real
  `Hangfire.SqlServer` storage via Testcontainers; gated behind an environment
  variable and `SkippableFact` so it skips (not fails) offline and in default
  CI runs. No production API changes.

## 0.1.25-preview

PR: [#26](https://github.com/sommmen/dotnet-agent-surface/pull/26) — `feat(trusted-auth): add trusted invocation context authorization`

### Breaking changes

- None for the Hangfire/CLI/MCP catalog surface. ASP.NET Core hosts that rely
  on the host's `IAuthorizationService` for endpoint authorization now have
  authenticated-caller context threaded through core invocation policies;
  anonymous endpoints keep working, but untrusted/missing callers are denied
  by default where authorization was previously not enforced end to end.

## 0.1.24-preview

PR: [#25](https://github.com/sommmen/dotnet-agent-surface/pull/25) — `test(skill-generation): add missing v2 coverage and mark tracking complete`

### Breaking changes

- None. Test-only change adding missing coverage for the v2 `SKILL.md`
  reference generator.

## 0.1.23-preview

PR: [#24](https://github.com/sommmen/dotnet-agent-surface/pull/24) — `docs(hangfire): improve package consumption guidance`

### Breaking changes

- None. Documentation-only: production Hangfire wiring guidance, package
  source mapping, and an explicit compatible `Newtonsoft.Json` 13.0.3
  reference to remediate `Hangfire.Core` 1.8.18's `netstandard2.0`
  transitive advisory (not a suppression).

## 0.1.22-preview

PR: [#23](https://github.com/sommmen/dotnet-agent-surface/pull/23) — `feat(hangfire): add discovery readiness diagnostics`

### Breaking changes

- **`HangfireJobRegistrationOptions.EnrichAsync` marked `[Obsolete]` and made
  to throw at catalog-construction time** if set, instead of being invoked.
  Callers using the removed asynchronous enrichment callback needed to
  migrate to the synchronous `Enrich` callback (added earlier, alongside
  `RegisterJobs`, in PR #18) before upgrading past this version. As of this
  changelog, `EnrichAsync` has been deleted entirely (see "Unreleased"
  above) rather than kept as a permanent throwing shim.

### Added

- Immutable reflection discovery reports (`HangfireJobDiscoveryReport`) for
  registration, skips, warnings, and strict failures; assembly/metadata input
  validation; documented the reflection-registration boundary with trimmed
  and NativeAOT applications.

## 0.1.21-preview

PR: [#21](https://github.com/sommmen/dotnet-agent-surface/pull/21) — `feat(hangfire): document vNext consumer migration`

### Breaking changes

- None new in this PR, but it is the migration guide for the breaking change
  shipped across `0.1.15-preview`–`0.1.17-preview` (below): the eager
  `AddHangfireRecurringJobs(...)` API removal. See
  [`docs/development/hangfire-recurring-migration.md`](docs/development/hangfire-recurring-migration.md).

## 0.1.20-preview

PR: [#19](https://github.com/sommmen/dotnet-agent-surface/pull/19) — `feat(safety): fail closed on Confirm and Dangerous operations (HF-3)`

### Breaking changes

- **Confirm and Dangerous-classified operations now fail closed by default.**
  Invoking a `Confirm`/`Dangerous` operation through the CLI or MCP adapters
  without an explicit confirmation flag (`--confirm` / `--yes`, or the
  equivalent MCP confirmation metadata) now fails the invocation instead of
  executing. Callers that invoke such operations programmatically or from
  automation must pass the confirmation explicitly.

## 0.1.17-preview

PR: [#18](https://github.com/sommmen/dotnet-agent-surface/pull/18) — `feat(hangfire): add class-based job discovery (HF-2)`

### Added

- `HangfireJob`/`HangfireJobWithOptions<TOptions>` base classes and
  `RegisterJobs<TJobBase>`/`RegisterJobs<TJobBase, TOptions>` fluent,
  reflection-based class discovery and registration — the API this
  changelog's "Unreleased" entry above extends with brownfield support.
  `HangfireJobRegistrationOptions.EnrichAsync` was also added here as an
  asynchronous per-type metadata-enrichment hook (later marked `[Obsolete]`
  in PR #23 and removed outright — see "Unreleased" above).

## 0.1.16-preview

PR: [#17](https://github.com/sommmen/dotnet-agent-surface/pull/17) — `fix(hangfire): correct recurring operation defaults`

### Breaking changes

- None. Bugfix follow-up to PR #16: corrected the exception parameter name
  used when `HangfireRecurringOperationsOptions.IsolatedExecutionTimeout` is
  invalid, and fell back to `JobActivator.Current` when no isolated
  `IsolatedJobActivator` is configured (previously `null` was passed through
  unconditionally).

## 0.1.15-preview

PR: [#16](https://github.com/sommmen/dotnet-agent-surface/pull/16) — `feat(hangfire): add stable recurring operations`

### Breaking changes

- **Removed the eager `AddHangfireRecurringJobs(...)` API, `HangfireDiscoveryOptions`,
  and its per-row `Enrich` callback.** There is no compatibility shim — it was
  intentionally not added; the ship has sailed. Replaced by the storage-lazy
  `AddHangfireRecurringOperations(...)` API, which always adds exactly two
  stable operations (`list-recurring-hangfire`, `trigger-recurring-hangfire`)
  instead of one operation per recurring-job row read eagerly from
  `JobStorage` at catalog-construction time. Per-job names, aliases,
  descriptions, and categories have no replacement because recurring jobs are
  now runtime data, not generated operations. See the full migration guide:
  [`docs/development/hangfire-recurring-migration.md`](docs/development/hangfire-recurring-migration.md).

## 0.1.14-preview

PR: [#15](https://github.com/sommmen/dotnet-agent-surface/pull/15) — `Fix NuGet pack failure in AspNetCore package metadata`

This version was originally integrated against [issue #28](https://github.com/sommmen/dotnet-agent-surface/issues/28).

### Breaking changes

- None. Packaging-only fix (added the missing `icon.png`/`README.md` package
  content items to `DotNetAgentSurface.AspNetCore.csproj` so `dotnet pack`
  stopped failing with `NU5046`).

### Earlier history

Versions prior to `0.1.14-preview` (`0.1.1-preview` through `0.1.13-preview`,
PRs [#3](https://github.com/sommmen/dotnet-agent-surface/pull/3)–[#14](https://github.com/sommmen/dotnet-agent-surface/pull/14))
predate this changelog and predate the first Hangfire satellite APIs; see
[`docs/development/tracking.md`](docs/development/tracking.md) for the full
milestone history if needed.
