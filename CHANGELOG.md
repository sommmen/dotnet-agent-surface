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

> **Package availability:** these packages are currently published only to
> GitHub Packages, not to NuGet.org. There is no public badge/version-discovery
> surface yet ([issue #28](https://github.com/sommmen/dotnet-agent-surface/issues/28)
> tracks this as deferred until the package is published more broadly).

## Unreleased

### Breaking changes

- `OperationInvoker.InvokeAsync` now throws `ConfirmationPolicyMissingException`
  when an operation's `SafetyLevel` is `AgentSafetyLevel.Confirm` or
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

### Added

- `RegisterJobs<TJobBase>`/`RegisterJobs<TJobBase, TOptions>`'s generic
  constraints were relaxed (not tightened) from the concrete
  `HangfireJob`/`HangfireJobWithOptions<TOptions>` base classes to the new
  `IHangfireJob`/`IHangfireJob<TOptions>` interfaces. This is purely additive:
  `HangfireJob`/`HangfireJobWithOptions<TOptions>` now implement the
  interfaces, so every existing greenfield caller keeps compiling unchanged.

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
  Throws `InvalidOperationException` if the job type has no matching method or
  more than one candidate, matching `RegisterJobs`'s existing error semantics.
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

### Changed

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

This is the version OPG Platform originally integrated against
([issue #28](https://github.com/sommmen/dotnet-agent-surface/issues/28)).

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
