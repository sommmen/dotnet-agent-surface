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
- **DotNetAgentSurface.Samples.CliAndMcp** (`tasktracker`) — a single
  executable that supports both surfaces: it discovers the same catalog once
  and dispatches to the CLI adapter or the MCP stdio server depending on the
  first argument (`mcp` vs. everything else). See "Running the combined
  CLI+MCP sample" below.
- **DotNetAgentSurface.Samples.LegacyDesktop** (`legacy-desktop-cli`) — a
  separate, self-contained sample that targets `net472` instead of `net10.0`
  to demonstrate `DotNetAgentSurface.Core` and `DotNetAgentSurface.CommandLine`
  running on real .NET Framework, consuming their `netstandard2.0` build. See
  the "Legacy .NET Framework sample" section below.
- **DotNetAgentSurface.Samples.AspNetCore** — a modern minimal API host that
  exposes the same `TaskTrackerService` catalog through HTTP, combined with
  its own Minimal API routes discovered through the ApiExplorer satellite
  (`DotNetAgentSurface.AspNetCore`). It uses ASP.NET Core dependency
  injection and delegates discovery and invocation to `OperationCatalog` and
  `OperationInvoker`.
- **DotNetAgentSurface.Samples.Hangfire** (`hangfire-sample`) — a
  self-contained console sample (using `Hangfire.InMemory`, no external
  backend required) that registers two Hangfire recurring jobs and catalogs
  two stable operations (`list-recurring-hangfire`,
  `trigger-recurring-hangfire`) through the `DotNetAgentSurface.Hangfire`
  discovery satellite, plus predicate-based ad-hoc job discovery
  (`AddHangfireJobTypes`) and attributed base-class job discovery
  (`RegisterJobs<TJobBase>()` / `RegisterJobs<TJobBase, TOptions>()`), then
  invokes them as agent operations. Because it deliberately demonstrates all
  four Hangfire discovery satellites side by side, its `Program.cs` is
  several hundred lines — a real consumer wiring up that many satellites
  plus its own custom operations should expect a similarly sized
  composition root. `DotNetAgentSurface.Samples.Hangfire.Cli` (below) is the
  representative size for a CLI that only needs one satellite: its
  `Program.cs` is ~50 lines end to end, most of which is composition-root
  boilerplate shared by every sample CLI in this repository, not
  Hangfire-specific.

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

### ApiExplorer-discovered demo endpoints

The sample also maps two of its own Minimal API routes — `GET /demo/ping`
(anonymous) and `GET /demo/secret` (`RequireAuthorization()`) — purely to
demonstrate the ApiExplorer discovery satellite (`AddFromApiExplorer`). Both
show up in the catalog alongside the `TaskTrackerService` operations:

```powershell
Invoke-RestMethod http://localhost:5000/operations/aspnet_get_demo_ping -Method Post -ContentType 'application/json' -Body '{}'
# => { "message": "pong" }

Invoke-RestMethod http://localhost:5000/operations/aspnet_get_demo_secret -Method Post -ContentType 'application/json' -Body '{}'
# => HTTP 400: the operation is cataloged but invocation is denied by default
#    because the Core invocation pipeline does not yet forward an
#    authenticated caller context (see work item 23 in development.md).
```

`aspnet_get_demo_secret` is discoverable — its metadata (including its
authorization requirement) is visible via `GET /operations/aspnet_get_demo_secret`
— but it can never be executed through the catalog until that authorization
contract exists. This is intentional deny-by-default behavior, not a bug.

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

## Running the combined CLI+MCP sample

`DotNetAgentSurface.Samples.Cli` and `DotNetAgentSurface.Samples.Mcp` are
separate executables, which can make it look like a host has to pick one
surface or the other. `DotNetAgentSurface.Samples.CliAndMcp` shows that isn't
the case: it discovers `TaskTrackerService`'s operation catalog exactly once
and wires up *both* `OperationCommandLineAdapter` and `McpOperationServer`
against it in the same `Program.cs`, choosing which one to run based on the
first command-line argument.

```powershell
# CLI mode - same behavior/output as DotNetAgentSurface.Samples.Cli
dotnet run --project samples\DotNetAgentSurface.Samples.CliAndMcp -- --help
dotnet run --project samples\DotNetAgentSurface.Samples.CliAndMcp -- tasks add-task --title "Write docs"
dotnet run --project samples\DotNetAgentSurface.Samples.CliAndMcp -- tasks list-tasks

# MCP mode - same stdio JSON-RPC server as DotNetAgentSurface.Samples.Mcp
dotnet run --project samples\DotNetAgentSurface.Samples.CliAndMcp -- mcp
```

The two modes are not active at the same time within one process invocation:
MCP's stdio transport reserves stdout exclusively for JSON-RPC protocol
traffic, so a process actively writing normal CLI output to stdout could not
also run the MCP server on the same stream without corrupting it. Selecting
the mode from the first argument avoids that conflict while still proving the
two surfaces share one catalog/invoker and one executable - a host application
can add an `mcp` verb alongside its normal commands instead of shipping and
maintaining a second binary.

## Running the offline Hangfire CLI composition sample

```powershell
# List the current in-memory recurring jobs.
dotnet run --project samples\DotNetAgentSurface.Samples.Hangfire.Cli -- Hangfire list-recurring-hangfire

# Denied without a prompt; exits 1 and schedules nothing.
dotnet run --project samples\DotNetAgentSurface.Samples.Hangfire.Cli -- Hangfire trigger-recurring-hangfire --jobId nightly-cleanup

# Approved by the shared fail-closed confirmation policy.
dotnet run --project samples\DotNetAgentSurface.Samples.Hangfire.Cli -- Hangfire trigger-recurring-hangfire --jobId nightly-cleanup --confirm

# Generate and verify a checked-in-or-CI skill snapshot without connecting to external storage.
dotnet run --project samples\DotNetAgentSurface.Samples.Hangfire.Cli -- generate --output skill
dotnet run --project samples\DotNetAgentSurface.Samples.Hangfire.Cli -- check --output skill
```

`DotNetAgentSurface.Samples.Hangfire.Cli` is a complete composition example:
it creates `Hangfire.InMemory` storage, registers stable recurring operations,
attaches `DangerousOperationConfirmationPolicy` to the same `OperationInvoker`
used by the CLI, and dispatches `generate`/`check` before generated operation
commands. Every invocation starts with fresh in-memory jobs, so triggering
returns Hangfire's configured-storage acknowledgement; it intentionally does
not claim that a background worker completed the job. It requires no credentials
or external services. For SQL Server, move only the connection string and
storage wiring to configuration—never put a credential in source.

See the [recurring-job migration guide](../docs/development/hangfire-recurring-migration.md)
and [non-interactive confirmation contract](../docs/development/operation-confirmation.md)
for the consumer contract demonstrated here.

## Running the Hangfire sample

```powershell
dotnet run --project samples\DotNetAgentSurface.Samples.Hangfire
```

The process is self-contained: it creates an in-memory Hangfire storage
(`Hangfire.InMemory`, no Redis/SQL Server backend needed), registers two
recurring jobs (`nightly-cleanup`, `hourly-report`) directly with
`IRecurringJobManager`, then catalogs two **stable** agent operations with
`AddHangfireRecurringOperations`: `list-recurring-hangfire` and
`trigger-recurring-hangfire`. Unlike a per-job catalog, these two operations
never change as recurring jobs are added, removed, or renamed — both query
Hangfire's recurring-job storage at invocation time rather than at catalog
construction time. Running it prints:

- the two stable catalog entries (not one per recurring job);
- the current recurring jobs as returned by `list-recurring-hangfire`;
- confirmation that invoking `trigger-recurring-hangfire` in its default mode
  **enqueues** the job rather than running it — `IRecurringJobManager.Trigger`
  (or `TriggerJob` when the manager supports it) only schedules work for a
  `BackgroundJobServer` to pick up later, and this sample deliberately does
  not start one for that path, so the job body does not execute inline;
- the enqueued jobs as reported back by Hangfire's `IMonitoringApi`, proving
  the operation really reached Hangfire's storage;
- a rejected trigger for an unknown job id, without ever calling into
  Hangfire; and
- an isolated-execution trigger (`HangfireExecutionModel.ExecuteUsingIsolatedInMemoryServer`),
  an explicit opt-in for short-lived CLI/local scenarios: it spins up a
  separate in-memory Hangfire storage/server, runs the job to completion (or
  failure/timeout), and reports the outcome — all without touching the
  application's configured storage.

This mirrors the satellite's real contract: the agent operation means "ask
Hangfire to run this job now", not "run this job's code directly" — except
where the isolated execution model is explicitly opted into, in which case it
means "run this job to completion on a disposable server".

The same run also prints two class-based discovery demos that catalog
job classes directly, rather than jobs already registered with
`IRecurringJobManager`:

- predicate-based discovery via `AddHangfireJobTypes`, which scans an
  assembly for classes matching a caller-supplied predicate; and
- attributed base-class discovery via `RegisterJobs<TJobBase>()` and
  `RegisterJobs<TJobBase, TOptions>()`, which catalog every concrete class
  derived from `HangfireJob` or `HangfireJobWithOptions<TOptions>` in an
  assembly as a stable, kebab-case-named operation — including
  options-binding jobs whose JSON input is bound to `TOptions` before the
  job is enqueued via `IBackgroundJobClient`.

### Using SQL Server storage

The sample uses `Hangfire.InMemory` so it runs without infrastructure. A SQL
Server consumer should keep the same `DotNetAgentSurface.Hangfire` registration
and replace only the storage package/configuration:

```powershell
dotnet add package Hangfire.SqlServer
dotnet add package DotNetAgentSurface.Hangfire
```

```csharp
var connectionString = configuration.GetConnectionString("Hangfire")
    ?? throw new InvalidOperationException("Configure the Hangfire connection string.");
var storage = new SqlServerStorage(connectionString);
var jobManager = new RecurringJobManager(storage);
var catalog = new OperationCatalogBuilder()
    .AddHangfireRecurringOperations(storage, jobManager)
    .Build();
```

Read the connection string from normal application configuration, environment
variables, or a secret store. Do not commit it to source control.

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
