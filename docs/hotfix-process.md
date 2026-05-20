# Hotfix Process

How to apply urgent fixes to a release that has already been deployed.

> **Diagram:** [Hotfix Flow](diagrams/hotfix-flow.mmd)

## When to Use

Use this process when:
- A bug is found in UAT/DM during release validation
- A production issue requires an urgent fix
- A fix is needed on the release branch that wasn't caught during sprint development

## Step-by-Step

### 1. Create Hotfix Branch from Release

```bash
git checkout release/sprint-12
git pull origin release/sprint-12
git checkout -b hotfix/fix-payment-calc
```

Branch from the **release branch**, not from `main`. This ensures your fix targets
the exact code that's deployed/being validated.

### 2. Develop and Test the Fix

- Make the minimal change required to fix the issue
- Test locally or in an ephemeral sandbox (provision pipeline will create one)
- Keep the scope narrow — hotfixes should not include new features

### 3. Merge to Release Branch

Create a PR targeting `release/sprint-12`:
- PR validation build must pass
- Get expedited review (hotfix reviewers should be pre-identified)
- Merge to the release branch

On merge, the full chain triggers automatically:
```
build.yml → deploy.yml (UAT → DM) → release.yml (GOLD → PROD)
```

### 4. Merge Back to Main

**Critical step** — do not skip this. If you only fix the release branch, the next
sprint's release will reintroduce the bug.

```bash
git checkout main
git pull origin main
git merge release/sprint-12
# Resolve any conflicts
git push origin main
```

Or create a PR from the release branch to `main` (preferred for audit trail).

### 5. Verify Deployment

Monitor the pipeline chain:
1. UAT deployment succeeds → verify the fix in UAT
2. DM deployment succeeds
3. Approve GOLD → verify in GOLD
4. Approve PROD → confirm fix in production

## Emergency Hotfix (Skip UAT Validation)

In rare cases where production is severely impacted and UAT validation would cause
unacceptable delay:

1. Follow steps 1-3 above (still merge to release branch)
2. Deploy pipeline still runs UAT → DM (this is structural, cannot be skipped)
3. Expedite approvals — pre-notify GOLD/PROD approvers to approve immediately
4. Business hours check on service connections can be temporarily disabled if needed

> **Note:** The pipeline chain (UAT → DM → GOLD → PROD) cannot be bypassed by design.
> Even emergency hotfixes must pass through all environments. This ensures no untested
> code reaches production. If this is unacceptable for your organization, consider adding
> a separate "emergency" service connection with fewer checks.

## Hotfix vs. Next Sprint

| Scenario | Action |
|---|---|
| Bug found in UAT, sprint release not yet in PROD | Hotfix on release branch |
| Bug found in PROD after go-live | Hotfix on release branch |
| Non-urgent improvement identified during validation | Add to next sprint backlog |
| Bug in code not yet released (still in main) | Fix in main during normal sprint |

## Multiple Hotfixes

If multiple hotfixes are needed on the same release:
- Each hotfix gets its own branch from the release (`hotfix/fix-a`, `hotfix/fix-b`)
- Merge them to the release branch sequentially (not in parallel)
- Each merge triggers the full deployment chain
- Each must also be merged back to `main`

Avoid batching hotfixes into a single deployment unless they're interdependent —
smaller deployments are easier to validate and roll back if needed.
