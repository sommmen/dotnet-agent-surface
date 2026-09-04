---
name: "skill"
description: "Operations exposed by the skill CLI."
executable: "skill"
---

# skill

Use this skill when you need to invoke generated operations via the `skill` CLI.
Run `skill --help` to list available commands, then read the detailed reference in [references/commands.md](references/commands.md) when you need parameter details or examples.

## Command index

- [`list-recurring-hangfire`](references/commands.md) — Lists the recurring jobs currently registered in Hangfire storage.
- [`trigger-recurring-hangfire`](references/commands.md) — Requests that a currently registered recurring job be triggered. By default the job is enqueued on the application's configured Hangfire storage; the acknowledgement never claims that execution completed. When configured for isolated execution, the job runs to completion or failure on a short-lived in-memory Hangfire server that never touches configured storage.

## Examples

- `skill --help`

See [references/commands.md](references/commands.md) for the full generated reference.
