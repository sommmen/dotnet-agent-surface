# Renovate

## Secrets

There are GitHub Actions secrets defined for the self-hosted Renovate run.

- `RENOVATE_TOKEN` — a PAT (not `GITHUB_TOKEN`) so automerge can trigger
  further required workflow runs. Only needed for the rich workflow tier.

### Validating the config

Run this command in the repo root:

```powershell
npx --yes --package renovate -- renovate-config-validator
```

## Workflow triggers

The self-hosted workflow runs on a daily cron, on `workflow_dispatch`, and on
pushes to the default branch that touch certain paths. Get the path filter
wrong and Renovate silently stops reacting to the things that matter.

- **Watch both config files.** `renovate-config.json` is the runner config
  passed to the action via `configurationFile:`; `renovate.json` holds the
  `packageRules`. A filter listing only the runner config means editing a
  package rule never triggers a run, so the change sits dormant until the next
  cron.
- **Watch the shared dependency manifests.** When a dependency PR merges, every
  other open dependency PR is one commit behind the default branch, and PRs
  that touch the same manifest become conflicted. Triggering Renovate on a push
  to those manifests lets it rebase the rest immediately instead of leaving
  them stuck until the next cron. Here that means `Directory.Packages.props` at
  the repo root and `**/dotnet-tools.json`.
- **Add a concurrency group, do not cancel.** The merge trigger makes
  overlapping runs likely, and Renovate mutates branches. Use
  `group: renovate` with `cancel-in-progress: false`: an in-progress run may
  predate the merge that triggered the new one, so cancelling it could discard
  the rebase you wanted.

```yaml
concurrency:
  group: renovate
  cancel-in-progress: false
```

### Automerging

For automerge to actually merge PRs, GitHub's auto-merge feature must be
enabled on the repository, and any protected branch must allow it.

- <https://docs.renovatebot.com/key-concepts/automerge/>
- <https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/configuring-pull-request-merges/managing-auto-merge-for-pull-requests-in-your-repository#managing-auto-merge>
