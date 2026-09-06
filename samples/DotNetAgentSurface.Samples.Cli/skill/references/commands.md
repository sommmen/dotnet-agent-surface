# Command reference

## `add-task`

Creates an incomplete task with a required title and optional notes.

- Safety: `Safe`
- Idempotent: `no`
- Category: `tasks`

### Parameters
- `--title` (String), required
- `--notes` (String), optional

### Examples
- `tasktracker-cli tasks add-task --title "Write docs" --notes "Include CLI skill generation"`

## `complete-task`

Marks the task identified by id as completed.

- Safety: `Safe`
- Idempotent: `no`
- Category: `tasks`

### Parameters
- `--id` (Int32), required

### Examples
- `tasktracker-cli tasks complete-task --id 1`

## `list-tasks`

Lists all tracked tasks in ascending identifier order.

- Safety: `Safe`
- Idempotent: `yes`
- Category: `tasks`

### Examples
- `tasktracker-cli tasks list-tasks`

## `remove-task`

Permanently deletes the task identified by id; invoke only after explicit user confirmation.

- Safety: `Dangerous`
- Idempotent: `no`
- Category: `tasks`

### Parameters
- `--id` (Int32), required

### Examples
- `tasktracker-cli tasks remove-task --id 1`
