# Deployment Process

Technical details of how packages are deployed to F&O environments via PPAC.

> **Diagrams:** [Deploy Template](diagrams/deploy-template.mmd) | [Artifact Promotion](diagrams/artifact-promotion.mmd)

<!-- SCREENSHOT: Deploy pipeline run in ADO showing the Submit & Wait and Verify jobs -->

## Deploy Template Overview

Every deployment (to any environment) uses `templates/deploy-finops-package.yml`.
This template implements a 2-job pattern:

```
Job 1: Submit & Wait
  ├── Teams notification (started)
  ├── Install Power Platform tools
  ├── Validate service connection (WhoAmI)
  ├── Wait for environment readiness (poll PPAC API)
  ├── Deploy package (synchronous wait up to 3 hours)
  └── ~3.5 hours max

Job 2: Verify
  ├── Install Power Platform tools
  ├── Poll PPAC Management API for operation outcome
  ├── Teams notification (success or failure)
  └── Fail stage if deployment is in bad state
```

## Environment Readiness Check

Before submitting a package, the pipeline polls the PPAC Management API to confirm the
environment is in a deployable state. This prevents the `EnvironmentStateInvalid` error
that occurs when deploying to an environment that's currently being serviced.

**How it works:**
1. Resolves the environment URL from the service connection
2. Acquires a PPAC Management API token via client credentials
3. Looks up the environment GUID by matching the org URL
4. Polls `GET /appmanagement/environments/{id}` every 30 seconds
5. Waits until state is `Ready` or `Enabled`
6. Times out after `readinessTimeoutMinutes` (default: 30 min)

**States that block deployment:**
- `Servicing` — another package is being applied (e.g., Microsoft One Version update)
- `Provisioning` — environment is still being created
- `Copying` — database copy in progress

## Package Deployment

The `PowerPlatformDeployPackage@2` task:
- Authenticates via the Power Platform SPN service connection
- Submits the deployable package (`.dll` file) to PPAC
- Waits synchronously for PPAC to finish applying the package
- `AsyncOperation: true` with `MaxAsyncWaitTime: 180` means the task polls PPAC
  internally for up to 3 hours

**Why synchronous wait is required:** If the task releases the agent before the package
is fully applied, PPAC cancels the deployment mid-operation. The task must remain connected
until PPAC reports completion.

`continueOnError: true` is set so that if the wait times out (>3 hours), the Verify job
still runs to check the actual status in PPAC.

## Verification

After the Submit job completes (success or timeout), the Verify job:
1. Acquires a PPAC Management API token independently
2. Resolves the environment GUID from the service connection
3. Queries recent operations: `GET /appmanagement/environments/{id}/operations`
4. Evaluates the most recent operation's state:

| State | Result |
|---|---|
| `Succeeded`, `Completed` | Stage passes |
| `Running`, `InProgress`, `Queued`, `Waiting` | Stage fails (still running after wait) |
| `Failed`, other | Stage fails |
| `Unknown` | Stage passes with warning (manual check recommended) |

5. Sends a Teams notification with the outcome
6. If the stage fails, downstream stages (e.g., DM after UAT, PROD after GOLD) are blocked

## Deployment Pipeline Chain

```
pr-validation.yml (on PR to main)
    ├── Stage 1: Compile (build-xpp.yml)
    └── Stage 2: Integration Test
          ├── Provision → 3h agentless wait → Copy DB → Deploy → DB Sync
          ├── test-finops-environment.yml (OData + SysTest)
          └── Teardown (always)

build.yml (release/* branch)
    │
    │ produces artifact
    ▼
deploy.yml
    ├── Stage: DeployUAT
    │     └── deploy-finops-package.yml (readiness → deploy → verify)
    │
    └── Stage: DeployDM (depends on UAT success)
          └── deploy-finops-package.yml (readiness → deploy → verify)
    │
    │ triggers release.yml (after DM succeeds)
    ▼
release.yml
    ├── Stage: DeployGOLD (approval gate)
    │     └── deploy-finops-package.yml (readiness → deploy → verify)
    │
    └── Stage: DeployPROD (depends on GOLD success, approval gate)
          └── deploy-finops-package.yml (readiness → deploy → verify)
```

## Deployment Parameters

| Parameter | Default | Description |
|---|---|---|
| `maxDeployWaitMinutes` | 180 | How long to wait for PPAC to apply the package |
| `readinessTimeoutMinutes` | 30 | How long to wait for environment to become ready |

These can be overridden per stage if some environments are known to take longer.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `EnvironmentStateInvalid` / state: `Servicing` | Readiness check timed out or was skipped | Increase `readinessTimeoutMinutes`, or wait for the current operation to finish in PPAC |
| `The Operation will be canceled` | Package apply interrupted (agent released too early) | Ensure `MaxAsyncWaitTime` ≥ 180 — the task must wait synchronously |
| Verify reports `Running` after wait | Package apply took longer than `maxDeployWaitMinutes` | Increase the parameter; check PPAC for the operation's actual status |
| `AADSTS7000215 Invalid client secret` | Service connection secret is expired/wrong | Update in Project Settings > Service connections (NOT the variable group) |
| Verify reports `Unknown` | Could not resolve environment from service connection | Verify the connection URL matches the environment in PPAC |

## Teams Notifications

Each deployment sends Teams notifications at:
- **Start** — package submitted, waiting for completion
- **Success** — deployment verified, PPAC operation details included
- **Failure** — PPAC status and operation ID included for investigation

Notifications use the Office 365 MessageCard format via the `TeamsWebhookUrl` from
the `Teams-Secrets` variable group.

## Security Enforcement

> **Diagram:** [Security Layers](diagrams/security-layers.mmd)

Deployments are controlled at multiple levels:

| Layer | Mechanism | What It Prevents |
|---|---|---|
| Pipeline resource trigger | `branches.include: [release/*]` | Non-release branches triggering deploy |
| Pipeline chaining | `release.yml` → `source: Deploy`, `stages: [DeployDM]` | Untested code reaching GOLD/PROD |
| Environment approvals | Approval gates on ADO Environments | Unauthorized promotions |
| Service connection approvals | Approvals + branch control on connections | Unauthorized credential use |
| Service connection branch control | `refs/heads/release/*` only | Wrong branch using production creds |
| Business hours | Time window on production connections | Off-hours deployments |
