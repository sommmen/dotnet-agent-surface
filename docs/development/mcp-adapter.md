# MCP adapter

Design notes for `DotNetAgentSurface.Mcp`, which turns catalog descriptors
into MCP tools hosted over stdio. See the
[development hub](../../DEVELOPMENT.md) for how this fits with the rest of
the project, and [core catalog and abstractions](core-catalog.md) for the
shared model it consumes.

## MCP adapter

The MCP adapter will use the official MCP C# SDK and create tools from catalog descriptors. The stdio host must reserve stdout exclusively for protocol traffic and route logs and diagnostics to stderr.

The adapter should preserve:

- tool names and descriptions;
- input schemas;
- required and default parameter behavior;
- structured errors;
- cancellation where supported.

Hosts can provide an `OperationInvocationContext` to the adapter. For transports
that authenticate requests, `McpOperationServer` forwards the MCP SDK's
transport-populated `JsonRpcMessageContext.User`; request `_meta` and tool
arguments are not treated as credentials.

MCP hosting should be provided as a separate executable or an easy-to-compose host library rather than embedded in a WinForms or WPF process.

### Custom server instructions

Consumers can pass free-form `serverInstructions` to `McpOperationServer`'s
constructor using the two-argument overload. This flows straight through to
`McpServerOptions.ServerInstructions`, which the MCP C# SDK returns to clients
during `initialize`. Per the MCP spec this is advisory: a compliant client *may*
fold it into the model's system prompt, but is not required to (some hosts
already read a server's instructions automatically, see
[ModelContextProtocol.Client.McpClient.ServerInstructions](https://modelcontextprotocol.github.io/csharp-sdk/api/ModelContextProtocol.Client.McpClient.html)).
A single-argument constructor overload is available for cases where
`serverInstructions` is not needed.

Use it to describe *how* to use the tools on this server (ordering, when to
prefer one operation over another), not to restate text already present in
individual tool descriptions:

```csharp
var server = new McpOperationServer(
    new McpOperationAdapter(catalog, invoker),
    serverInstructions: """
        Prefer `find-customer` over free-text search when a customer ID is known.
        Call `create-invoice` only after `find-customer` has confirmed the customer exists.
        """);

await server.RunStdioAsync();
```

`serverInstructions` is optional and defaults to `null`, so existing callers
are unaffected.

See also: the [`tasktracker-mcp` sample](../../samples/README.md), which hosts `McpOperationServer` over stdio.
