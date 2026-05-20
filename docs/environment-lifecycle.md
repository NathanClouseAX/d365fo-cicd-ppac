# Environment Lifecycle

How environments are provisioned, used, and cleaned up throughout the development process.

> **Diagram:** [Environment Lifecycle](diagrams/environment-lifecycle.mmd)

## Environment Types

| Environment | Purpose | Lifecycle | Managed By |
|---|---|---|---|
| Ephemeral sandboxes | Individual developer testing | Created per feature branch, deleted by automated cleanup | `provision-environment.yml`, `cleanup-environments.yml` |
| PR test environments | Automated integration testing | Created per PR, torn down automatically after tests | `pr-validation.yml` (Stage 2) |
| UAT | Sprint release validation by business users | Permanent, redeployed each sprint | `deploy.yml` |
| DM (Data Migration) | Integration testing with data migration scenarios | Permanent, redeployed each sprint | `deploy.yml` |
| GOLD | Pre-production final validation | Permanent, updated per release | `release.yml` |
| PROD | Production | Permanent, updated per release | `release.yml` |

## Ephemeral Sandbox Lifecycle

### Creation (Automatic)

When a developer pushes a feature branch:

```
1. provision-environment.yml triggers
   → New-AdminPowerAppEnvironment (template: D365_FinOps_Finance)
   → Environment named after branch: "feature-inventory-fix"
   → Branch ref tagged in description: "branch:refs/heads/feature/inventory-fix"
   → ~15-30 minutes to submit provisioning request

2. post-provision-copy.yml triggers (after provision completes)
   → Waits 240 minutes (4 hours) on server pool (zero agent cost)
   → Matches governance configuration (Managed Environment) between source and target
   → Copy-PowerAppEnvironment from known good source
   → Triggers DMF entity refresh on the new environment
   → ~60-90 minutes for copy after wait

Total: ~5-6 hours from push to usable sandbox with production-like data
```

<!-- SCREENSHOT: PPAC admin center showing ephemeral sandbox with branch tag in description field -->

### Usage

- Developer validates their feature with production-like data
- Environment is isolated — no other developers are affected
- Multiple sandboxes can exist simultaneously (one per feature branch)

### Deletion (Automated)

After the feature branch is merged and deleted, the environment is cleaned up automatically:

**Automated cleanup (`cleanup-environments.yml`)**
- Runs daily at 06:00 UTC on a schedule
- Compares PPAC environments (tagged with `branch:refs/heads/...` in their description)
  against remote branches
- Environments whose branch no longer exists are classified as **orphaned** and deleted
- Defaults to **dry run** mode — logs what would be deleted without actually deleting
- Set `dryRun: false` to enable actual cleanup

<!-- SCREENSHOT: Cleanup Environments pipeline output showing ACTIVE vs ORPHANED classification -->

**Manual deletion (fallback)**
- Run `delete-environment.yml` with the environment display name or GUID
- Useful for environments that predate branch tagging or need immediate removal

> **Cost consideration:** PPAC sandbox environments incur costs while running.
> The automated cleanup pipeline ensures orphaned environments are removed within
> 24 hours of branch deletion.

## PR Test Environment Lifecycle

PR test environments are fully automated — no manual intervention required.

### Creation (Automatic — per PR)

When a PR is created targeting `main`, `pr-validation.yml` Stage 2 runs:

```
1. Provision environment named "pr-<PR number>"
   → New-AdminPowerAppEnvironment (Sandbox, D365_FinOps_Finance)
   → Tagged with "branch:<source branch> pr:<PR number>" in description

2. Wait 3 hours (agentless server delay — zero agent cost)
   → Uses pool: server with Delay@1 task
   → No hosted agent is consumed during this wait

3. Wait for provisioning + stabilization (polls up to 60 min)
   → Confirms environment state is 'Succeeded'
   → Additional 5 min stabilization delay

4. Copy database from known good source
   → MinimalCopy from database-source-environment-guid
   → ~60-90 minutes

5. Deploy the package from Stage 1's artifact
   → pac package deploy

6. Database synchronization
   → Ensures schema changes are applied

7. Automated tests
   → OData smoke tests (SystemUsers, LegalEntities, Companies)
   → SysTest suite (if configured)
```

<!-- SCREENSHOT: PR Validation pipeline run showing the agentless delay job and integration test jobs -->

### Teardown (Automatic — always)

The teardown job runs with `condition: always()` — whether tests pass or fail:
- `Remove-AdminPowerAppEnvironment` deletes the environment
- No orphaned environments from failed PRs
- No manual cleanup required

### Cost Impact

Each PR creates and destroys a sandbox environment. Typical cost per PR:
- Environment exists for ~5-6 hours (provision + 3-hour wait + copy + test + teardown)
- PPAC sandbox billing is hourly
- Agent time is minimal — the 3-hour wait uses an agentless server pool at zero cost
- Consider this the cost of automated regression prevention

## Permanent Environment Lifecycle

### UAT and DM

- **Deployed to:** Every sprint release (via `deploy.yml`)
- **Reset frequency:** Each deployment applies the new package on top of existing state
- **Database refresh:** Can be manually refreshed from known good source using `copy-environment.yml`
  if data becomes stale or corrupted during testing
- **Who uses them:** Business users (UAT), integration testers (DM)

### GOLD

- **Deployed to:** Only after UAT + DM validation passes (via `release.yml`)
- **Purpose:** Final pre-production validation with production-like data and configuration
- **Database:** Should mirror production as closely as possible
- **Who uses them:** Release managers, key stakeholders for final sign-off

### PROD

- **Deployed to:** Only after GOLD approval (via `release.yml`)
- **Purpose:** Live production environment
- **Database:** Production data
- **Who uses them:** End users

## Known Good Source Environment

The **known good source** is the environment used as the database source for:
- Ephemeral sandbox provisioning (via `post-provision-copy.yml`)
- Manual database refreshes (via `copy-environment.yml`)

Configured in the `FinOps-Options` variable group as `database-source-environment-guid`.

**Best practice:** Use GOLD or a dedicated "master data" environment as the known good source.
This ensures developers work with realistic, up-to-date data.

## Environment State Management

The deploy template includes a **readiness check** that polls the PPAC Management API
before submitting a package. If the environment is in a non-ready state (e.g., `Servicing`
from a previous operation or Microsoft update), the pipeline waits up to 30 minutes
for it to become available.

Common environment states:

| State | Meaning | Pipeline Behavior |
|---|---|---|
| `Ready` / `Enabled` | Available for deployment | Proceeds immediately |
| `Servicing` | Another operation in progress | Waits (polls every 30s) |
| `Provisioning` | Still being created | Waits |
| `Copying` | Database copy in progress | Waits |
| `Deleting` | Being torn down | Fails (environment is gone) |

## Database Copy Operations

### When to Copy

| Scenario | Source | Target | Pipeline |
|---|---|---|---|
| New sandbox needs data | Known good source | Ephemeral sandbox | `post-provision-copy.yml` (automatic) |
| UAT data is stale/corrupted | Known good source | UAT | `copy-environment.yml` (manual) |
| Refresh GOLD before release | PROD or known good source | GOLD | `copy-environment.yml` (manual) |

### Timing Considerations

- Database copies can take 1-4 hours depending on database size
- The target environment is unavailable during the copy
- Do not start a copy if a deployment is pending — the deployment will fail with `EnvironmentStateInvalid`
- `post-provision-copy.yml` waits 240 minutes (4 hours) after provisioning before starting
  the copy, to allow the new F&O environment to fully stabilize
- Before copying, the pipeline matches governance configuration (Managed Environment)
  between source and target environments to prevent `EnvironmentGovernanceConfigurationCopyError`

## Cleanup Policy

| Environment Type | When to Delete | How |
|---|---|---|
| Ephemeral sandbox | Automatically when source branch is deleted | `cleanup-environments.yml` (daily scheduled) |
| PR test environment | Immediately after tests complete | `pr-validation.yml` teardown job (`condition: always()`) |
| Old release environments | Never — UAT/DM/GOLD/PROD are permanent | N/A |

### Automated Cleanup Pipeline

The `cleanup-environments.yml` pipeline runs daily at 06:00 UTC and handles orphan detection:

1. Fetches all remote branches from the repository
2. Fetches all PPAC environments with a `branch:` tag in their description
3. Compares the two lists:
   - **ACTIVE** — branch still exists, environment is kept
   - **ORPHANED** — branch has been deleted, environment is a cleanup candidate
4. In dry run mode (default): logs the classification without deleting
5. With `dryRun: false`: deletes orphaned environments via `Remove-AdminPowerAppEnvironment`

### Branch Tagging

The cleanup pipeline relies on environments having a `branch:refs/heads/...` tag in their
description field. This is set automatically by:
- `provision-environment.yml` — tags with `branch:$(Build.SourceBranch)`
- `pr-validation.yml` — tags with `branch:$(Build.SourceBranch) pr:$(System.PullRequest.PullRequestNumber)`

Environments without a `branch:` tag are ignored by the cleanup pipeline.

### Manual Cleanup (Fallback)

For environments that predate branch tagging or need immediate removal:
```powershell
# List all environments matching branch-derived names
Get-AdminPowerAppEnvironment | Where-Object { $_.DisplayName -like "feature-*" }
```

Run `delete-environment.yml` with the environment display name or GUID.
