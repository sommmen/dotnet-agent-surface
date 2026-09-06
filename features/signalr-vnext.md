# SignalR integration

## Status: delivered

This document defines the initial SignalR satellite. The package exposes a
small, stable set of server-to-client send operations backed by a registered
`IHubContext<THub>`. It deliberately does not reflect Hub methods into agent
operations.

## Problem and goals

A SignalR Hub is a connection-scoped RPC endpoint. Its methods run within the
SignalR pipeline, where a caller connection, its user principal, and Hub
lifetime services are available. An agent-surface invocation is different: it
has no SignalR connection and, when authenticated, carries the agent caller's
trusted invocation principal rather than the principal of a target Hub client.

Reflecting Hub methods would blur those two caller identities, require a
connection and argument-discovery model that SignalR does not provide, and
suggest that a Hub method can be safely invoked outside its pipeline. The
useful server-side integration point is instead `IHubContext<THub>`, which
supports deliberate server-to-client messages without a Hub instance.

Goals:

- expose deterministic, kebab-case operations for sending a named client
  method to all clients, one group, one user, or one connection;
- register operations from a supplied `IHubContext<THub>` without inspecting
  connected clients, Hub methods, or application routes at catalog-build time;
- preserve arbitrary JSON message arguments as `JsonElement` values until the
  configured SignalR protocol serializes them;
- make outbound sends visibly confirmation-gated by default, while allowing a
  host to choose its own category and safety metadata;
- use the existing catalog, invocation-policy, CLI, MCP, and skill-generation
  surfaces without adding a parallel transport or authorization mechanism.

Non-goals: discovering Hub methods, invoking Hub methods from an agent,
listing connections/users/groups, tracking group membership, inferring client
method schemas, or providing message durability/retry semantics. SignalR does
not expose global connection or group enumeration through `IHubContext`; an
application that needs those concepts must maintain its own registry and expose
an explicit operation for it.

## Stable send operations

`AddSignalRSendOperations<THub>(...)` registers these four operations:

```text
send-signalr-all     --method-name ReceiveAlert --arguments '[{"text":"Maintenance begins soon"}]'
send-signalr-group   --group-name operators --method-name ReceiveAlert --arguments '[{"text":"Maintenance begins soon"}]'
send-signalr-user    --user-id alice --method-name ReceiveAlert --arguments '[{"text":"Maintenance begins soon"}]'
send-signalr-client  --connection-id abc123 --method-name ReceiveAlert --arguments '[{"text":"Maintenance begins soon"}]'
```

Each operation accepts a non-empty client method name and a JSON array of
arguments. The target-specific operations additionally accept their required
target identifier. The implementation forwards the arguments through the
matching `IHubClients` proxy's `SendCoreAsync` call. A successful call means
SignalR accepted the message for its configured delivery pipeline; it does not
acknowledge that a client received or processed it.

Every send returns a target-specific result record containing the target,
method name, argument count, and the UTC timestamp at which SignalR accepted
the send request. Results intentionally contain no connection inventory or
client response data.

The registration shape is:

```csharp
catalogBuilder.AddSignalRSendOperations<NotificationsHub>(hubContext, options =>
{
    options.Category = "Notifications";
    options.SendSafetyLevel = AgentSafetyLevel.Confirm;
});
```

The default category is `SignalR` and the default safety level is `Confirm`.
The satellite does not assume which messages are harmless: broadcasting,
targeting a group, targeting a user, and targeting a connection can all have
externally visible effects.

## Authorization and safety boundary

SignalR authorization attributes protect Hub connections and Hub-method
invocations. The send operations use `IHubContext<THub>` and do not invoke a
Hub method, so Hub-level or method-level `[Authorize]` metadata is not
consulted by SignalR for these server-originated sends. This is intentional,
but must be explicit to hosts: authorization of an agent request belongs at
the agent-surface invocation boundary, not to the recipient Hub pipeline.

The satellite adds no SignalR-specific authorization policy in this first
version. Hosts can attach existing `IOperationInvocationPolicy` implementations
to their invoker to authenticate and authorize the trusted agent caller before
an operation runs. In particular, the agent caller's principal must never be
confused with a SignalR recipient's `HubCallerContext.User`.

`AgentSafetyLevel` is catalog metadata, not enforcement by itself. A host that
registers sends with `Confirm` or `Dangerous` must configure a confirmation
policy implementing `IConfirmationEnforcingPolicy`; otherwise invocation fails
with the existing missing-policy diagnostic rather than silently sending an
unconfirmed message. The package therefore supplies safe metadata and relies
on the shared policy pipeline for enforcement.

## Host composition

A normal ASP.NET Core host registers SignalR and maps the application's Hub as
usual. It then obtains the typed hub context from dependency injection while
building the agent catalog:

```csharp
builder.Services.AddSignalR();

var app = builder.Build();
app.MapHub<NotificationsHub>("/notifications");

var catalog = new OperationCatalogBuilder()
    .AddSignalRSendOperations(
        app.Services.GetRequiredService<IHubContext<NotificationsHub>>())
    .Build();
```

The send catalog can be composed with attributed services, ApiExplorer
operations, Hangfire operations, and the CLI/MCP adapters. The host owns the
lifetime of the `IHubContext<THub>`, chooses confirmation and authorization
policies, and decides which catalog surfaces are exposed.

## Delivery status

1. **SR-1 — stable server-to-client send operations** (delivered): `DotNetAgentSurface.SignalR`'s
   `AddSignalRSendOperations<THub>(...)` registers `send-signalr-all`,
   `send-signalr-group`, `send-signalr-user`, and `send-signalr-client` against
   a supplied `IHubContext<THub>`. Target-identifier and method-name validation
   run before the `IHubClients` target selector is resolved, so a request that
   fails validation never causes a SignalR routing side effect.
2. **SR-2 — focused catalog and invocation tests** (delivered):
   `DotNetAgentSurface.SignalR.Tests` covers catalog registration, operation
   metadata, target routing, method/argument forwarding, validation, result
   shape, cancellation, and option overrides (11/11 tests passing).
3. **SR-3 — hosted SignalR sample and usage documentation** (delivered): the
   `DotNetAgentSurface.Samples.SignalR` host maps a Hub and composes the send
   catalog from a typed `IHubContext<THub>`, documented in
   [`samples/README.md`](../samples/README.md).
