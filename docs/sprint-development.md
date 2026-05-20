# Sprint Development Process

How developers work during a sprint — from feature branch to merge.

> **Diagrams:** [Sprint Workflow](diagrams/sprint-workflow.mmd) | [Environment Lifecycle](diagrams/environment-lifecycle.mmd)

## Overview

During a sprint, developers work in feature branches with ephemeral sandbox environments.
Each feature is validated independently before merging to `main`. Main accumulates sprint
work but does not deploy to any shared environment.

## Step-by-Step

### 1. Create Feature Branch

```bash
git checkout main
git pull origin main
git checkout -b feature/inventory-adjustment
```

Branch naming conventions:
- `feature/*` — new functionality
- `bug/*` — defect fixes
- `hotfix/*` — urgent fixes (also used for release branch patches)

### 2. Develop and Push

```bash
# Make changes to X++ code in src/xpp/models/
git add .
git commit -m "Add inventory adjustment validation"
git push -u origin feature/inventory-adjustment
```

On push, two pipelines trigger automatically:
- **build.yml** — compiles X++ and publishes a deployable package artifact
- **provision-environment.yml** — creates an ephemeral PPAC sandbox named after the branch

### 3. Wait for Sandbox Provisioning

The provision pipeline:
1. Creates a new PPAC environment with F&O template (~15-30 min to submit request)
2. Tags the environment with the source branch ref in its description field
3. Triggers **post-provision-copy.yml** which:
   - Waits 240 minutes (4 hours) on a server pool (zero agent cost) for F&O to stabilize
   - Matches governance configuration (Managed Environment) between source and target
   - Copies the database from the known good source environment
   - Triggers a DMF entity refresh on the new environment

Total time: ~5-6 hours from push to a usable sandbox with production-like data.
The 4-hour wait uses an agentless server pool, so no hosted agent is consumed.

<!-- SCREENSHOT: Post-Provision Copy pipeline showing the 4-hour agentless delay stage -->

### 4. Validate in Sandbox

Once provisioning completes:
1. Open the sandbox environment (URL from PPAC admin center or pipeline output)
2. Test your feature with realistic data
3. Verify no regressions in affected areas

### 5. Create Pull Request

> **Diagram:** [PR Validation](diagrams/pr-validation.mmd)

```bash
# Ensure your branch is up to date
git fetch origin main
git rebase origin/main
git push --force-with-lease
```

Create a PR targeting `main`. This triggers **pr-validation.yml** which runs two stages:

**Stage 1: Compile** (~10 min)
- Compiles X++ and creates a deployable package

**Stage 2: Integration Test** (~4-5 hours)
- Provisions an ephemeral sandbox named `pr-<PR number>`
- Waits 3 hours (agentless server delay — zero agent cost) for F&O provisioning
- Polls for environment readiness (up to 60 min)
- Copies database from the known good source environment
- Deploys the package to the test environment
- Runs database synchronization
- Executes OData smoke tests (validates key entities respond)
- Runs SysTest suite (if configured)
- Tears down the environment (always, even on failure)

The PR **cannot merge** until both stages pass. This ensures every commit that reaches
`main` has been validated in a real F&O environment with production-like data.

<!-- SCREENSHOT: PR Validation pipeline showing all jobs in the Integration Test stage -->

### 6. Code Review and Merge

- Reviewers approve the PR
- PR validation (compile + integration test) passes
- Merge to `main` (squash merge recommended for clean history)

### 7. Sandbox Cleanup (Automated)

After merge, the ephemeral sandbox is cleaned up automatically:
- **cleanup-environments.yml** runs daily at 06:00 UTC
- It compares PPAC environments (tagged with `branch:refs/heads/...` in their description)
  against remote branches
- Environments whose branch has been deleted are classified as orphaned and removed
- No manual action required — just merge and delete the branch

For immediate cleanup before the scheduled run, use **delete-environment.yml** manually.

## What Happens to Main After Merge

- **build.yml** triggers and produces an artifact — this validates compilation
- **Nothing deploys** — UAT/DM are reserved for release candidates
- The artifact is available if needed but sits idle until a release branch is cut

## Tips

- Keep feature branches short-lived (days, not weeks) to minimize merge conflicts
- If a sandbox is no longer needed before the PR is ready, delete it early to save costs
- Multiple developers can work on separate features simultaneously — each gets their own sandbox
- If you need to test integration between features, both can be merged to main and
  a release branch can be cut early for UAT validation
