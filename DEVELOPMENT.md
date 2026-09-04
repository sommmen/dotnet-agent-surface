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
  DotNetAgentSurface.AspNetCore/   ApiExplorer discovery satellite
  DotNetAgentSurface.Hangfire/     Hangfire recurring-job discovery satellite
tests/
  DotNetAgentSurface.Core.Tests/         Core, MCP, and cross-adapter test suite
  DotNetAgentSurface.CommandLine.Tests/  CLI/AXI output contract tests
  DotNetAgentSurface.AspNetCore.Tests/   ApiExplorer satellite tests
  DotNetAgentSurface.Hangfire.Tests/     Hangfire satellite tests
samples/
  DotNetAgentSurface.Samples.TaskTracker/    Shared sample service
  DotNetAgentSurface.Samples.Cli/            tasktracker-cli host
  DotNetAgentSurface.Samples.Mcp/            tasktracker-mcp host
  DotNetAgentSurface.Samples.CliAndMcp/      combined tasktracker host
  DotNetAgentSurface.Samples.LegacyDesktop/  legacy-desktop-cli (net472) host
  DotNetAgentSurface.Samples.AspNetCore/     ApiExplorer-discovered minimal API host
  DotNetAgentSurface.Samples.Hangfire/       Hangfire recurring-job sample
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

CI runs the same build and test steps; see
[`.github/workflows`](.github/workflows) for the current workflow.

## Design documentation

The design is split by module/feature so each slice stays focused:

- [Core catalog and abstractions](docs/development/core-catalog.md) — goals, non-goals, architecture, `AgentOperationAttribute`, `OperationDescriptor`, `OperationCatalog`, binding/invocation, and supported types.
- [CLI adapter](docs/development/cli-adapter.md) — `DotNetAgentSurface.CommandLine`, plus AXI and token-efficiency conventions.
- [MCP adapter](docs/development/mcp-adapter.md) — `DotNetAgentSurface.Mcp` and stdio hosting.
- [Skill and reference generation](docs/development/skill-generation.md) — the `SKILL.md`/`commands.md`/`schemas.json` generator, its current flat-file state, and the reference-sharding plan that keeps `SKILL.md` small as catalogs grow.
- [Safety and security](docs/development/safety-and-security.md) — the shared policy pipeline and confirmation model.
- [Target frameworks and dependencies](docs/development/frameworks-and-dependencies.md) — the `net10.0;netstandard2.0` compatibility baseline and dependency choices.
- [Testing strategy and open design decisions](docs/development/testing-and-open-decisions.md) — cross-surface testing goals, resolved/open design decisions (including the fluent `OperationCatalogBuilder` and discovery satellites), and the definition of an initial usable release.
- [Discovery satellites](features/discovery-satellites.md) — the Hangfire, ASP.NET Core, and native MCP-SDK discovery satellite designs referenced from the open design decisions above.
- [Hangfire vNext](features/hangfire-vnext.md) — the P0 plan for live recurring-job operations, class-based job discovery, safety, host composition, and migration.

## Ongoing work

- [Development tracking](docs/development/tracking.md) — delivery milestones, current status, and the per-milestone task breakdown.
- [Changelog](CHANGELOG.md) — breaking changes and notable additions per preview version, so a consumer upgrading across many versions has one place to read what changed.

## License

[MIT](LICENSE).
