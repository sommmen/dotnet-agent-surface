# .NET Agent Surface

.NET Agent Surface is a small library and framework for exposing an application's existing capabilities to people and AI agents through three synchronized surfaces:

- an [MCP](https://modelcontextprotocol.io/) server;
- a command-line interface;
- generated agent skill documentation.

Implement an operation once, annotate it explicitly, and generate every surface from the same catalog.

> This project is in its initial design phase. The API and package structure described here are planned and may change during development.

## Motivation

Applications often implement MCP tools, CLI commands, and agent instructions independently. That duplicates metadata, invocation logic, validation, security policy, examples, and documentation—and allows those surfaces to drift apart.

.NET Agent Surface will use one `OperationCatalog` as their source of truth:

```text
Existing application services
          |
 [AgentOperation] methods
          |
     OperationCatalog
      /      |       \
    MCP     CLI    SKILL.md
```

The catalog will discover only explicitly annotated operations and describe their:

- stable name and human-readable description;
- invocation target (`MethodInfo` or delegate);
- parameters, required values, and defaults;
- input JSON Schema and return type;
- category and safety level;
- examples and documentation.

Adapters will use that metadata to expose equivalent behavior over MCP and the CLI and to render deterministic skill files. The generated CLI will also target the [Agent eXperience Interface (AXI)](https://github.com/kunchenguid/axi/blob/main/.agents/skills/axi/SKILL.md) conventions so agents can discover and consume operations with fewer calls and fewer tokens.

## Proposed usage

```csharp
public class CustomerOperations
{
    private readonly ICustomerService customerService;

    public CustomerOperations(ICustomerService customerService)
    {
        this.customerService = customerService;
    }

    [AgentOperation(
        "find-customer",
        "Find a customer by email address",
        Category = "customers")]
    public Customer FindCustomer(string email)
    {
        return customerService.FindByEmail(email);
    }
}
```

The same operation could then be available as:

```text
myapp-cli customers find-customer --email customer@example.com
```

an MCP tool named `find-customer`, and an entry in generated skill documentation.

The exact public API and command layout will be established during implementation.

## Planned outputs

A consuming application should be able to produce separate host processes and generated documentation from one shared automation assembly:

```text
MyApp.Automation.dll       Shared catalog and invocation logic
MyApp.McpServer.exe        MCP stdio server
myapp-cli.exe              Human and agent CLI
skills/
  customer-management/
    SKILL.md
    references/
      commands.md
      schemas.json
```

Keeping the MCP server outside a WinForms or WPF process prevents protocol traffic from being mixed with GUI output. A Windows service can either host the shared services directly or expose them to separate hosts through named pipes or HTTP.

## Compatibility and proposed dependencies

The initial goal is to support modern .NET and legacy .NET Framework 4.6.2 or later. Candidate building blocks are:

- [MCP C# SDK](https://www.nuget.org/packages/ModelContextProtocol/) for MCP hosting and tool adaptation;
- [System.CommandLine](https://www.nuget.org/packages/System.CommandLine) for generated CLI commands;
- [NJsonSchema](https://www.nuget.org/packages/NJsonSchema/) for DTO and parameter schemas.

Dependency versions, target frameworks, and compatibility will be verified before the first package is published.

## Design principles

- **Explicit exposure:** only annotated methods enter the catalog.
- **One source of truth:** MCP, CLI, schemas, and skill files derive from the same metadata.
- **Shared policy enforcement:** authentication, authorization, validation, and confirmation belong in the common invocation layer.
- **Safe protocol hosting:** MCP stdout is reserved for protocol messages; diagnostics go to stderr.
- **Agent-friendly contracts:** operations prefer simple DTOs and JSON-compatible values.
- **Token-efficient CLI output:** generated CLIs follow AXI conventions, including compact [TOON](https://toonformat.dev/) output, minimal default projections, explicit truncation, and actionable next-step guidance while retaining a stable JSON mode for interoperability.
- **Visible risk:** destructive operations are marked and require an appropriate confirmation policy.
- **Deterministic generation:** identical catalog input produces identical documentation and schemas.

## Development

The planned architecture, milestones, and open design decisions are documented in [features/development.md](features/development.md).

## Status

No packages are available yet. The first milestone is a minimal catalog and invocation pipeline, followed by adapters and deterministic generators.

## License

[MIT](LICENSE).
