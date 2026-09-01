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

MCP hosting should be provided as a separate executable or an easy-to-compose host library rather than embedded in a WinForms or WPF process.

See also: the [`tasktracker-mcp` sample](../../samples/README.md), which hosts `McpOperationServer` over stdio.
