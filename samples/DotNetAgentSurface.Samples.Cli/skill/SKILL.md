---
name: "tasktracker-cli"
description: "Manage the task tracker through its generated CLI operations."
executable: "tasktracker-cli"
---

# tasktracker-cli

Use this skill when you need to invoke generated operations via the `tasktracker-cli` CLI.
Run `tasktracker-cli --help` to list available commands, then read the detailed reference in [references/commands.md](references/commands.md) when you need parameter details or examples.

## Command index

- [`add-task`](references/commands.md) — Creates an incomplete task with a required title and optional notes.
- [`complete-task`](references/commands.md) — Marks the task identified by id as completed.
- [`list-tasks`](references/commands.md) — Lists all tracked tasks in ascending identifier order.
- [`remove-task`](references/commands.md) — Permanently deletes the task identified by id; invoke only after explicit user confirmation.

## Examples

- `tasktracker-cli tasks add-task --title "Write docs" --notes "Include CLI skill generation"`
- `tasktracker-cli tasks complete-task --id 1`
- `tasktracker-cli tasks list-tasks`

See [references/commands.md](references/commands.md) for the full generated reference.
