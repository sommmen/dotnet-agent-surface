# Development

This is the canonical entry point for developing .NET Agent Surface. It
summarizes the project layout and workflow, and links to the focused design
documents and the ongoing-work tracker.

> This document captures the proposed architecture and implementation
> direction for .NET Agent Surface. It is a design baseline plus a live
> status tracker, not a description of an already-finished API.

## Repository layout

```text
src/
  DotNetAgentSurface.Core/         Shared catalog, invocation, schema generation
  DotNetAgentSurface.CommandLine/  CLI adapter
  DotNetAgentSurface.Mcp/          MCP adapter
  DotNetAgentSurface.Hosting/      Dual-surface (MCP + CLI) host application
  DotNetAgentSurface.AspNetCore/   ApiExplorer discovery satellite
  DotNetAgentSurface.Hangfire/     Hangfire recurring-job discovery satellite
  DotNetAgentSurface.SignalR/      SignalR server-to-client messaging satellite
tests/
  DotNetAgentSurface.Core.Tests/         Core, MCP, and cross-adapter test suite
  DotNetAgentSurface.CommandLine.Tests/  CLI/AXI output contract and argument tests
  DotNetAgentSurface.Hosting.Tests/      Host tests and end-to-end runs of the CliAndMcp sample
  DotNetAgentSurface.AspNetCore.Tests/   ApiExplorer satellite tests
  DotNetAgentSurface.Hangfire.Tests/     Hangfire satellite tests
  DotNetAgentSurface.Hangfire.SqlServer.Tests/  Opt-in SQL Server compatibility runs for the Hangfire satellite (Testcontainers)
  DotNetAgentSurface.SignalR.Tests/      SignalR satellite tests
samples/
  DotNetAgentSurface.Samples.TaskTracker/    Shared sample service
  DotNetAgentSurface.Samples.Cli/            tasktracker-cli host
  DotNetAgentSurface.Samples.Mcp/            tasktracker-mcp host
  DotNetAgentSurface.Samples.CliAndMcp/      combined tasktracker host (AgentSurfaceApplication)
  DotNetAgentSurface.Samples.LegacyDesktop/  legacy-desktop-cli (net472) host
  DotNetAgentSurface.Samples.AspNetCore/     ApiExplorer-discovered minimal API host
  DotNetAgentSurface.Samples.Hangfire/       Hangfire recurring-job sample
  DotNetAgentSurface.Samples.Hangfire.Cli/   hangfire-cli-sample (offline Hangfire CLI composition)
  DotNetAgentSurface.Samples.SignalR/        SignalR server-to-client messaging sample
docs/
  development/  Design slices and the ongoing-work tracker (linked below)
features/
  discovery-satellites.md  Viability assessment for the Hangfire/ASP.NET Core/MCP discovery satellites
```

See [`samples/README.md`](samples/README.md) for how to run each sample host.

## Building and testing

```powershell
dotnet build
dotnet test
```

CI runs the same build and test steps, a second job that repeats them at the lowest supported
dependency versions (`dotnet test DotNetAgentSurface.slnx -p:UseLowestSupportedDependencies=true`; the
floors live in `Directory.Packages.props`), and `dotnet pack`, which validates the public API against
the latest published preview (`PackageValidationBaselineVersion` in `src/Directory.Build.props`; record
intentional breaks with `-p:ApiCompatGenerateSuppressionFile=true`). See
[`.github/workflows`](.github/workflows) for the current workflows.

## Design documentation

The design is split by module/feature so each slice stays focused:

- [Core catalog and abstractions](docs/development/core-catalog.md) — goals, non-goals, architecture, `AgentOperationAttribute`, `OperationDescriptor`, `OperationCatalog`, binding/invocation, and supported types.
- [CLI adapter](docs/development/cli-adapter.md) — `DotNetAgentSurface.CommandLine`, plus AXI and token-efficiency conventions.
- [MCP adapter](docs/development/mcp-adapter.md) — `DotNetAgentSurface.Mcp` and stdio hosting.
- [Skill and reference generation](docs/development/skill-generation.md) — the `SKILL.md`/`commands.md`/`schemas.json` generator, its current flat-file state, and the reference-sharding plan that keeps `SKILL.md` small as catalogs grow.
- [XML documentation comments as operation metadata](docs/development/xml-doc-comments.md) — the plan for sourcing operation and parameter descriptions from `/// <summary>` comments so annotations don't repeat text the code already carries, modelled on how OpenAPI consumes XML docs.
- [Safety and security](docs/development/safety-and-security.md) — the shared policy pipeline and confirmation model.
- [Target frameworks and dependencies](docs/development/frameworks-and-dependencies.md) — the `net10.0;netstandard2.0` compatibility baseline and dependency choices.
- [Testing strategy and open design decisions](docs/development/testing-and-open-decisions.md) — cross-surface testing goals, resolved/open design decisions (including the fluent `OperationCatalogBuilder` and discovery satellites), and the definition of an initial usable release.
- [Discovery satellites](features/discovery-satellites.md) — the Hangfire, ASP.NET Core, and native MCP-SDK discovery satellite designs referenced from the open design decisions above.
- [Hangfire vNext](features/hangfire-vnext.md) — the P0 plan for live recurring-job operations, class-based job discovery, safety, host composition, and migration.
- [SignalR integration](features/signalr-vnext.md) — the stable server-to-client send operations, their authorization/safety boundary, and host composition.

## Ongoing work

- [Development tracking](docs/development/tracking.md) — delivery milestones, current status, and the per-milestone task breakdown.
- [Changelog](CHANGELOG.md) — breaking changes and notable additions per preview version, so a consumer upgrading across many versions has one place to read what changed.

## License

[MIT](LICENSE).
