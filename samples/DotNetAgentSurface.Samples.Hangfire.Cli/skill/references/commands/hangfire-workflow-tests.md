# hangfire workflow tests

## `archive-old-records-job`

Runs ArchiveOldRecordsJob directly and captures its log transcript.

- Safety: `Confirm`
- Idempotent: `no`

## `damage-sync-job`

Runs DamageSyncJob directly and captures its log transcript.

- Safety: `Confirm`
- Idempotent: `no`

### Parameters
- `--workflow-options` (WorkflowJobOptions), required
