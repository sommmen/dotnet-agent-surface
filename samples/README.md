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
- **DotNetAgentSurface.Samples.LegacyDesktop** (`legacy-desktop-cli`) — a
  separate, self-contained sample that targets `net472` instead of `net10.0`
  to demonstrate `DotNetAgentSurface.Core` and `DotNetAgentSurface.CommandLine`
  running on real .NET Framework, consuming their `netstandard2.0` build. See
  the "Legacy .NET Framework sample" section below.
- **DotNetAgentSurface.Samples.AspNetCore** — a modern minimal API host that
  exposes the same `TaskTrackerService` catalog through HTTP. It uses ASP.NET
  Core dependency injection and delegates discovery and invocation to
  `OperationCatalog` and `OperationInvoker`.

Each host process starts with an empty, in-memory task list (there is no
persistence layer), so state does not carry over between separate CLI
invocations. Run multiple operations in the same process (e.g. through the
MCP host, which stays alive) to see state changes reflected.

## Running the ASP.NET Core sample

```powershell
dotnet run --project samples\DotNetAgentSurface.Samples.AspNetCore
```

The app listens on the URLs printed at startup. Its HTTP surface includes:

- `GET /operations` — lists the catalog's operation metadata.
- `GET /operations/{name}` — gets metadata for one operation, such as
  `/operations/add-task`.
- `POST /operations/{name}` — invokes an operation with a JSON object whose
  properties map to its parameters.

For example, once the app is running:

```powershell
Invoke-RestMethod http://localhost:5000/operations
Invoke-RestMethod http://localhost:5000/operations/add-task -Method Post -ContentType 'application/json' -Body '{"title":"Write docs"}'
Invoke-RestMethod http://localhost:5000/operations/list-tasks -Method Post -ContentType 'application/json' -Body '{}'
```

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

## Legacy .NET Framework sample

`DotNetAgentSurface.Samples.LegacyDesktop` targets `net472` and hosts a
different, minimal `GreeterService` (rather than reusing `TaskTrackerService`,
which targets `net10.0` and would not compile for `net472`). It exercises the
same `OperationCatalog` discovery and `OperationCommandLineAdapter` execution
path as the other CLI sample, but against the `netstandard2.0` build of
`DotNetAgentSurface.Core`/`DotNetAgentSurface.CommandLine` — the same binaries
a WinForms, WPF, or console .NET Framework 4.6.1+ application would reference.

```powershell
dotnet run --project samples\DotNetAgentSurface.Samples.LegacyDesktop -- --help
dotnet run --project samples\DotNetAgentSurface.Samples.LegacyDesktop -- greet --name "Ada" --honorific "Dr."
dotnet run --project samples\DotNetAgentSurface.Samples.LegacyDesktop -- count-letters --name "banana" --letter a
```

On Windows with the .NET Framework installed, `dotnet run` builds and launches
`net472` executables directly. The build also produces a standalone
`legacy-desktop-cli.exe` under `bin\Debug\net472\` that can be run without the
`dotnet` host, or launched from a WinForms/WPF `net472`/`net48` host
application the same way.
