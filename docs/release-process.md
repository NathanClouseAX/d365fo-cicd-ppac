# Release Process

How to create a sprint release and promote it through environments to production.

> **Diagrams:** [Artifact Promotion](diagrams/artifact-promotion.mmd) | [Sprint Workflow](diagrams/sprint-workflow.mmd)

## Overview

At the end of each sprint, a release branch is cut from `main`. This triggers the
deployment chain: UAT → DM (validation) → GOLD → PROD (go-live). Only code that
passes validation in UAT and DM can reach production — this is enforced structurally
by the pipeline wiring, not just by approvals.

## Prerequisites

- All sprint features merged to `main`
- Daily build on `main` is green (no compilation errors)
- Team agrees sprint scope is complete

## Step-by-Step

### 1. Cut the Release Branch

```bash
git checkout main
git pull origin main
git checkout -b release/sprint-12
git push -u origin release/sprint-12
```

This immediately triggers:
1. **build.yml** — compiles and produces a deployable package artifact
2. **deploy.yml** — deploys the artifact to UAT, then DM

### 2. Monitor Validation Deployment (UAT → DM)

The deploy pipeline:
1. Checks environment readiness (waits if environment is in `Servicing` state)
2. Submits the package to UAT and waits up to 3 hours for completion
3. Verifies deployment status via PPAC Management API
4. If UAT succeeds, proceeds to DM with the same process

Monitor progress in Azure DevOps > Pipelines > Deploy.

<!-- SCREENSHOT: Deploy pipeline in ADO showing UAT and DM stages with Teams notification cards -->

Teams notifications are sent at each stage transition.

### 3. Business User Validation

Once UAT deployment succeeds:
1. Notify business users that UAT is ready for validation
2. Users test the sprint's features against acceptance criteria
3. Log any issues found

If issues are found → see [Hotfix Process](hotfix-process.md)

### 4. Approve for Production (GOLD → PROD)

After DM succeeds, **release.yml** triggers automatically:
1. Requests approval for GOLD environment (configured approvers are notified)
2. On approval: deploys to GOLD for final pre-production validation
3. Requests approval for PROD environment
4. On approval: deploys to production

Approvals are configured on:
- **Azure DevOps Environments** — gates the pipeline stage
- **Service connections** — gates the credential (defense-in-depth)

### 5. Post-Deployment

After production deployment succeeds:
1. Verify the deployment in PPAC admin center (release.yml's Verify job does this automatically)
2. Run **dmf-entity-refresh.yml** if new data entities were deployed
3. Notify stakeholders that the release is live

### 6. Keep the Release Branch Open

Do NOT delete the release branch immediately. Keep it open for hotfixes until:
- The next sprint's release branch is cut, OR
- A defined retention period has passed (e.g., 2 weeks post-go-live)

## Timeline Example

```
Day 1 (Sprint end):   Cut release/sprint-12 from main
Day 1 (automatic):    Build → Deploy to UAT (~1-3 hours)
Day 1-3:              Business users validate in UAT
Day 3:                DM deployment completes
Day 3-4:              Approve GOLD deployment
Day 4:                GOLD deployment + final validation
Day 5:                Approve PROD deployment → go-live
Day 5+:               Hotfixes if needed (see hotfix-process.md)
```

## Multiple Environments at Different Versions

During a phased go-live, different environments may be at different release versions:

| Environment | Version | State |
|---|---|---|
| UAT | release/sprint-13 (next sprint validating) | Testing |
| DM | release/sprint-13 | Testing |
| GOLD | release/sprint-12 (current production) | Stable |
| PROD | release/sprint-12 | Live |

This is expected. The pipeline chain ensures GOLD/PROD only receive code that has
already passed UAT/DM validation.

## Cancelling a Release

If a release needs to be abandoned (e.g., critical issue found in UAT that can't be
fixed quickly):

1. Do NOT delete the release branch (preserves history)
2. Cancel any pending approval requests in Azure DevOps
3. Communicate to stakeholders that the release is on hold
4. Either fix on the release branch (hotfix) or abandon and cut a new release later
