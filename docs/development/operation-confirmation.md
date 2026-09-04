# Non-interactive operation confirmation

`DangerousOperationConfirmationPolicy` is fail-closed. It never prompts and a
host must attach it to the `OperationInvoker` used by every exposed surface.
The same operation metadata and `OperationConfirmation` contract apply to CLI
and MCP requests:

| Safety level | Required confirmation |
|---|---|
| `Safe` | None |
| `Confirm` | `OperationConfirmation.IsConfirmed` |
| `Dangerous` | `OperationConfirmation.IsDangerousConfirmed` |

An omitted, malformed, or insufficient confirmation denies the operation before
input binding or execution. The stable denial result is:

```text
Operation '<operation-name>' requires explicit confirmation.
```

No interactive prompt is attempted. A caller must resend an explicit approval.

## CLI

The generated CLI maps its global flags to the shared contract:

| Request | Invocation |
|---|---|
| A `Confirm` operation | `your-host <category> <operation> --confirm` |
| A `Dangerous` operation | `your-host <category> <operation> --confirm --yes` |

For example, the default Hangfire recurring trigger is `Confirm`:

```powershell
your-host Hangfire trigger-recurring-hangfire --job-id nightly-cleanup
# exits 1; Operation 'trigger-recurring-hangfire' requires explicit confirmation.

your-host Hangfire trigger-recurring-hangfire --job-id nightly-cleanup --confirm
# invokes the operation
```

`--yes` strengthens an already explicit `--confirm`; it is required for an
operation marked `Dangerous`. It does not cause a prompt or bypass a policy.
A policy denial has exit code **1**. Cancellation propagates as exit code
**130**. Command/input usage errors have exit code **2**.

## MCP

MCP callers send the shared confirmation information in request `_meta` using
`io.dotnetagentsurface/confirmation`:

```json
{
  "name": "trigger-recurring-hangfire",
  "arguments": { "jobId": "nightly-cleanup" },
  "_meta": {
    "io.dotnetagentsurface/confirmation": {
      "confirmed": true,
      "dangerousConfirmed": false
    }
  }
}
```

That approves a `Confirm` operation. For a `Dangerous` operation, set both
values to `true`:

```json
{
  "_meta": {
    "io.dotnetagentsurface/confirmation": {
      "confirmed": true,
      "dangerousConfirmed": true
    }
  }
}
```

Omitting the metadata, supplying only `confirmed` for a dangerous operation,
or using false values returns the stable denial error content and does not
execute the operation. The MCP adapter itself takes an `OperationConfirmation`;
`McpOperationServer` translates the `_meta` object above before it calls the
adapter.
