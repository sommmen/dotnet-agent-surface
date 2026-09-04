# Command reference

## `list-recurring-hangfire`

Lists the recurring jobs currently registered in Hangfire storage.

- Safety: `Safe`
- Idempotent: `no`
- Category: `Hangfire`

## `trigger-recurring-hangfire`

Requests that a currently registered recurring job be triggered. By default the job is enqueued on the application's configured Hangfire storage; the acknowledgement never claims that execution completed. When configured for isolated execution, the job runs to completion or failure on a short-lived in-memory Hangfire server that never touches configured storage.

- Safety: `Confirm`
- Idempotent: `no`
- Category: `Hangfire`

### Parameters
- `--jobId` (String), required
