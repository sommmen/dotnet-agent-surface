# Samples

This folder contains a minimal end-to-end example that demonstrates how a single
service class can be exposed as both a CLI tool and an MCP server using
DotNetAgentSurface, without duplicating any operation definitions.

## Projects

- **DotNetAgentSurface.Samples.TaskTracker** — a small in-memory task tracker
  service annotated with `[AgentOperation]`. This is the single source of
  truth for the sample's behavior; it has no knowledge of the CLI or MCP
  adapters.
- **DotNetAgentSurface.Samples.Cli** (`tasktracker-cli`) — discovers the
  `TaskTrackerService` operation catalog and executes a single command per
  process invocation via `OperationCommandLineAdapter`.
- **DotNetAgentSurface.Samples.Mcp** (`tasktracker-mcp`) — discovers the same
  catalog and exposes it as an MCP server over stdio via `McpOperationServer`.

Each host process starts with an empty, in-memory task list (there is no
persistence layer), so state does not carry over between separate CLI
invocations. Run multiple operations in the same process (e.g. through the
MCP host, which stays alive) to see state changes reflected.

## Running the CLI sample

```powershell
dotnet run --project samples\DotNetAgentSurface.Samples.Cli -- --help
dotnet run --project samples\DotNetAgentSurface.Samples.Cli -- add-task --title "Write docs"
dotnet run --project samples\DotNetAgentSurface.Samples.Cli -- list-tasks
dotnet run --project samples\DotNetAgentSurface.Samples.Cli -- complete-task --id 1
dotnet run --project samples\DotNetAgentSurface.Samples.Cli -- remove-task --id 1
```

`remove-task` is marked `AgentSafetyLevel.Dangerous` while the other operations
are marked `Safe`; the generated MCP tool annotations (`destructiveHint`,
`readOnlyHint`) reflect this metadata. The sample hosts do not register an
`IOperationInvocationPolicy`, so `remove-task` currently executes without a
confirmation gate — see `AdapterPolicyEquivalenceTests` in
`DotNetAgentSurface.Core.Tests` for how a `DangerousOperationConfirmationPolicy`
can be layered into the `OperationInvoker` to enforce identical
confirmation/denial behavior across the CLI and MCP adapters.

## Running the MCP sample

```powershell
dotnet run --project samples\DotNetAgentSurface.Samples.Mcp
```

The process communicates over stdio using JSON-RPC (MCP protocol). It does not
write anything other than protocol messages to stdout, so it can be wired
directly into any MCP-compatible client. For example, after sending an
`initialize` request and an `initialized` notification, a `tools/list` request
returns the four operations above with their generated JSON schemas and safety
annotations.
