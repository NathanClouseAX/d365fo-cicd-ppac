# Pipeline Setup Guide

Step-by-step instructions for configuring the Azure DevOps pipelines in this repository.

> **Diagram:** [Pipeline Architecture](diagrams/pipeline-architecture.mmd)

## Prerequisites

- Azure DevOps organization and project
- Project Collection Administrator or Build Administrator role
- Variable groups configured (see [Variable Groups & Service Connections](variable-groups.md))
- [Power Platform Build Tools](https://marketplace.visualstudio.com/items?itemName=microsoft-IsvExpTools.PowerPlatform-BuildTools) extension installed from the Visual Studio Marketplace
- [Dynamics 365 Finance and Operations Tools](https://marketplace.visualstudio.com/items?itemName=Dyn365FinOps.dynamics365-finops-tools) extension installed (provides `XppUpdateModelVersion`, `XppCreatePackage` tasks)

## Creating Pipeline Definitions

For each pipeline, navigate to **Pipelines > New Pipeline > Azure Repos Git**, select this repository, then choose **Existing Azure Pipelines YAML file** and pick the YAML path.

### 1. PR Validation

| Setting | Value |
|---|---|
| YAML file | `Pipelines/pr-validation.yml` |
| Name | `PR Validation` |
| Trigger | Automatic on PRs to `main` |
| Variable groups | `PowerPlatform-Auth`, `FinOps-Options` |

Two-stage validation gate that must pass before merge:

**Stage 1: Compile** — builds X++ and creates a deployable package (~10 min)

**Stage 2: Integration Test** — provisions ephemeral environment, waits 3 hours (agentless
server delay — zero agent cost) for F&O provisioning, copies DB from known good source,
deploys the package, runs DB sync, executes automated tests (OData smoke tests +
other tests), then tears down the environment (~4-5 hours total)

<!-- SCREENSHOT: PR Validation pipeline run showing Compile and Integration Test stages -->

This ensures every PR is validated in a real F&O environment before reaching `main`.
By the time a release branch is cut, all code has already passed automated regression.

### 2. Build X++

| Setting | Value |
|---|---|
| YAML file | `Pipelines/build.yml` |
| Name | `Build X++` |
| Trigger | Push to main, feature/\*, bug/\*, hotfix/\*, release/\* |
| Variable groups | `Teams-Secrets` |

Compiles X++ and publishes a deployable package artifact. Triggers on all branches —
downstream pipelines (`deploy.yml`, `release.yml`) control which artifacts are promoted.

Uses `templates/build-xpp.yml` with `createPackage: true, publishArtifact: true`.

Also runs on a daily cron schedule (06:00 UTC) for the `main` branch.

> **Important:** The pipeline definition name in Azure DevOps **must** be `Build X++`
> (or you must update the `source` field in both `deploy.yml` and `release.yml`
> `resources.pipelines` sections to match).

### 3. Deploy (UAT + DM)

| Setting | Value |
|---|---|
| YAML file | `Pipelines/deploy.yml` |
| Name | `Deploy` |
| Trigger | Automatic (triggered by `Build X++` completion on `release/*` branches only) |
| Variable groups | `PowerPlatform-Auth`, `Teams-Secrets` |

Deploys sprint release candidates to UAT and DM for validation. Only `release/*` branches
trigger this pipeline — `main` builds produce artifacts but do not deploy anywhere.
This is the gateway to production — `release.yml` only triggers after this pipeline's
DM stage succeeds.

> **Important:** The pipeline definition name in Azure DevOps **must** be `Deploy`
> (or you must update the `source` field in `release.yml` `resources.pipelines` to match).

> **Note:** Main does not deploy to UAT. During the sprint, developers validate their
> work in ephemeral sandboxes (provision pipeline). UAT is reserved for the sprint's
> release candidate — a deliberate batch for business user validation.

**Environment Setup (Approval Gates):**

| Environment Name | Approvers | Purpose |
|---|---|---|
| `Nclouse-uat` | (optional) | UAT deployment |
| `Nclouse-DM` | Team leads | Data migration deployment |

### 4. Release (GOLD + PROD)

| Setting | Value |
|---|---|
| YAML file | `Pipelines/release.yml` |
| Name | `Release` |
| Trigger | Automatic (chained from `Deploy` pipeline after DM stage succeeds on `release/*` branches) |
| Variable groups | `PowerPlatform-Auth`, `Teams-Secrets` |

Promotes packages through GOLD and PROD for go-live. **Only artifacts that have
successfully passed UAT and DM (via `deploy.yml`) can reach this pipeline.** This is
structural enforcement — not just process or approval gates.

The pipeline resource uses `stages: [DeployDM]` to ensure it only triggers after the
DM stage succeeds, and `branches.include: [release/*]` to restrict to release branches.
Main-branch builds do not trigger this pipeline.

> **Important:** The pipeline definition name for deploy.yml in Azure DevOps **must** be `Deploy`
> (or you must update the `source` field in `release.yml` `resources.pipelines` to match).

**Environment Setup (Approval Gates):**

| Environment Name | Approvers | Purpose |
|---|---|---|
| `Nclouse-GOLD` | Release managers | Gold / pre-prod deployment |
| `Nclouse-Prod` | Release managers + stakeholders | Production deployment |

To add approval gates: select the environment > **Approvals and checks** > **+** > **Approvals**.

**Additional hardening (recommended):**
- Add **Branch control** checks on GOLD and PROD environments:
  Approvals and checks > **+** > **Branch control** > allow only `refs/heads/release/*`.
  This prevents manual queue attempts from non-release branches.
- Add **Approvals and branch control** on the `Nclouse-GOLD` and `Nclouse-Prod`
  service connections — this gates the credential itself, not just the stage.
  See [Service Connection Approvals and Checks](variable-groups.md#approvals-and-checks-on-service-connections).

### 5. Create Environment

| Setting | Value |
|---|---|
| YAML file | `Pipelines/provision-environment.yml` |
| Name | `Create Environment` |
| Trigger | Push to `feature/*`, `bug/*`, `hotfix/*` branches |
| Variable groups | `PowerPlatform-Auth` |

When a developer pushes a feature branch, this pipeline automatically provisions a PPAC
sandbox environment named after the branch (e.g., `feature-inventory-fix`). The environment
is tagged with the source branch ref in its description field (e.g., `branch:refs/heads/feature/inventory-fix`)
for automated cleanup tracking.

<!-- SCREENSHOT: PPAC admin center showing a provisioned ephemeral environment with branch tag in description -->

**Parameters (configurable at queue time):**

| Parameter | Default | Description |
|---|---|---|
| Environment SKU | `Sandbox` | `Production`, `Sandbox`, or `Trial` |
| Template | `D365_FinOps_Finance` | F&O application template |
| Location | `unitedstates` | Azure region |
| DevToolsEnabled | `true` | Enable development tools |
| DemoDataEnabled | `false` | Provision with demo data |

### 6. Post-Provision Copy

| Setting | Value |
|---|---|
| YAML file | `Pipelines/post-provision-copy.yml` |
| Name | `Post-Provision Copy` |
| Trigger | Automatic (triggered by Create Environment completion) |
| Variable groups | `PowerPlatform-Auth`, `FinOps-Options` |

**Important:** The `source` field in `resources.pipelines` must exactly match the Azure DevOps
pipeline definition name of the Create Environment pipeline:

```yaml
resources:
  pipelines:
    - pipeline: provisionRun
      source: Create Environment   # must match the ADO pipeline name exactly
      trigger: true
```

After provisioning completes, this pipeline waits 240 minutes (4 hours) on a server pool
(zero agent cost) for F&O to fully stabilize, then resolves the target environment by
display name, matches governance configuration (Managed Environment) between source and
target, copies the F&O database from the known good source, and triggers a DMF entity refresh.

<!-- SCREENSHOT: Post-Provision Copy pipeline run showing WaitForProvision, CopyDatabase, and DmfRefresh stages -->

### 7. Copy Environment

| Setting | Value |
|---|---|
| YAML file | `Pipelines/copy-environment.yml` |
| Name | `Copy Environment` |
| Trigger | Manual only |
| Variable groups | `PowerPlatform-Auth` |

Standalone pipeline for copying a F&O database between environments.
Accepts either a target environment GUID directly or a display name to resolve.

### 8. Delete Environment

| Setting | Value |
|---|---|
| YAML file | `Pipelines/delete-environment.yml` |
| Name | `Delete Environment` |
| Trigger | Manual only |
| Variable groups | `PowerPlatform-Auth` |

Deletes a PPAC environment by GUID or display name.
Use for cleaning up ephemeral feature branch sandboxes after merge.

### 9. DMF Entity Refresh

| Setting | Value |
|---|---|
| YAML file | `Pipelines/dmf-entity-refresh.yml` |
| Name | `DMF Entity Refresh` |
| Trigger | Manual only |
| Variable groups | `PowerPlatform-Auth` |

Refreshes the Data Management Framework entity list via the `InitializeDataManagement`
OData action. Run this after deploying new data entities.

**Required parameter:** The F&O environment base URL (e.g., `https://myenv.operations.dynamics.com`).

### 10. Cleanup Environments

| Setting | Value |
|---|---|
| YAML file | `Pipelines/cleanup-environments.yml` |
| Name | `Cleanup Environments` |
| Trigger | Scheduled daily at 06:00 UTC |
| Variable groups | `PowerPlatform-Auth` |

Scheduled pipeline that finds PPAC environments provisioned from branches that no longer
exist and deletes them. Uses the environment description field (`branch:refs/heads/...`)
to match environments to remote branches.

**Parameters:**

| Parameter | Default | Description |
|---|---|---|
| dryRun | `true` | Log orphaned environments without deleting them |

The pipeline defaults to dry run mode — it reports which environments would be deleted
without actually deleting them. Set `dryRun` to `false` to enable actual cleanup.

<!-- SCREENSHOT: Cleanup Environments pipeline run showing ACTIVE vs ORPHANED environment classification -->

## Pipeline Execution Order

### During sprint (development + individual testing)

```
1.  Developer pushes feature/xyz
2.  Build X++                          (automatic — produces artifact)
3.  Create Environment                 (automatic — creates ephemeral sandbox, tags with branch ref)
4.  Post-Provision Copy                (automatic — waits 4 hours, matches governance, copies DB, refreshes DMF)
5.  Developer validates in sandbox
6.  Create PR to main
7.  PR Validation Stage 1: Compile     (automatic — ~10 min)
8.  PR Validation Stage 2: Integration (automatic — ~4-5 hours)
    → Provision test env → 3-hour agentless delay → Copy DB → Deploy → DB Sync → Test → Teardown
9.  Code review + approval
10. Merge to main                      (builds but does NOT deploy to UAT)
11. Cleanup Environments               (automatic — daily scheduled, deletes orphaned sandboxes)
```

### Sprint end (release candidate validation + promotion)

```
1. Cut release/sprint-N from main     (git checkout -b release/sprint-12 main)
2. Build X++                          (automatic — produces artifact from release branch)
3. Deploy                             (automatic — validates in UAT → DM)
4. Business users validate in UAT
5. Release                            (automatic after DM succeeds — GOLD → PROD)
6. DMF Entity Refresh                 (manual, if new data entities were deployed)
```

### Hotfix flow

```
1. Branch from release/sprint-N       (git checkout -b hotfix/fix-xyz release/sprint-12)
2. Fix and merge to release/sprint-N  (PR to release branch)
3. Also merge to main                 (PR to main to keep branches in sync)
4. Build X++                          (automatic — produces artifact)
5. Deploy                             (automatic — validates in UAT → DM)
6. Release                            (automatic — after DM succeeds, GOLD → PROD)
```

## Source Code Structure

The repo contains three types of deployable content:

| Component | Location | Build | Deploy |
|---|---|---|---|
| X++ modules | `src/xpp/models/` | Compiled by `build-xpp.yml` via MSBuild | Part of deployable package (`.dll`) |
| C# class libraries | `src/csharp/` | Compiled alongside X++ in `Build.sln` (MSBuild resolves references) | Included in same deployable package |
| Dual write configs | `src/dualwrite/` | No compilation needed | Deployed separately after F&O is running via Data Integrator API |

**X++ + C# relationship:** X++ can reference C# assemblies via CLR interop. C# projects
live in `src/csharp/` and are included in `src/xpp/projects/Build/Build.sln`. MSBuild
compiles both as part of the same build. The resulting deployable package contains all
assemblies — no separate C# deploy step needed.

**Pure standalone C# projects** (Azure Functions, microservices, integration middleware)
should live in their own repositories. Reference them via NuGet packages or consume
their artifacts independently.

**Dual write** configs depend on both F&O entities and Dataverse tables being available.
They are deployed as a post-deploy step after the F&O package is applied and DB sync
is complete.

## Template Composition

The pipelines use composable templates to avoid duplication:

```
templates/build-xpp.yml          ← Steps: NuGet, compile, package, publish
    Used by: build.yml (full), pr-validation.yml (compile + package for integration test)

templates/test-finops-environment.yml  ← Steps: OData smoke tests + SysTest
    Used by: pr-validation.yml

templates/deploy-finops-package.yml  ← Jobs: readiness check, deploy, verify
    Used by: deploy.yml, release.yml

templates/provision-finops-environment.yml  ← Steps: provision PPAC env
    Used by: provision-environment.yml

templates/copy-finops-environment.yml  ← Steps: copy PPAC env
    Used by: post-provision-copy.yml, copy-environment.yml

templates/teams-notify.yml       ← Steps: send Teams webhook
    Used by: all pipelines

cleanup-environments.yml         ← Standalone: scheduled orphan cleanup
    Uses: branch description tags from provision-environment.yml
```

### build-xpp.yml Parameters

| Parameter | Type | Default | Description |
|---|---|---|---|
| `createPackage` | boolean | `true` | Run `XppCreatePackage@2` to create deployable package |
| `publishArtifact` | boolean | `true` | Publish pipeline artifact for downstream consumption |
| `artifactName` | string | `drop` | Name of the published artifact |

### deploy-finops-package.yml Parameters

| Parameter | Type | Default | Description |
|---|---|---|---|
| `environmentName` | string | (required) | Azure DevOps Environment for approval gates |
| `serviceConnectionName` | string | (required) | Power Platform SPN service connection |
| `packageFile` | string | (required) | Path to TemplatePackage.dll |
| `stageLabel` | string | (required) | Display label: UAT, DM, GOLD, PROD |
| `maxDeployWaitMinutes` | number | `180` | Max time to wait for package apply |
| `readinessTimeoutMinutes` | number | `30` | Max time to wait for environment readiness |

## Azure Artifacts NuGet Feed

The build pipelines pull X++ DevALM compiler packages from an Azure Artifacts feed.

### Setup

1. Create a feed in **Azure Artifacts** (e.g., named `D365`)
2. Upload the DevALM NuGet packages from your LCS Shared Asset Library or NuGet.org
3. Update `src/xpp/build/nuget.config` with your feed URL:

```xml
<configuration>
  <packageSources>
    <clear />
    <add key="D365" value="https://{org}.pkgs.visualstudio.com/{project}/_packaging/{feed}/nuget/v3/index.json" />
  </packageSources>
</configuration>
```

4. Update `src/xpp/build/packages.config` with versions matching your target F&O environment

### NuGet Package Caching

The build template (`templates/build-xpp.yml`) uses the `Cache@2` task to cache DevALM
packages between runs. On cache hit, the ~2 GB NuGet download is skipped entirely,
reducing build time by several minutes.

The cache key is derived from the `packages.config` file hash — when you bump F&O versions,
the cache automatically invalidates and fresh packages are downloaded.

## Troubleshooting

| Issue | Solution |
|---|---|
| NuGet install fails with 401 | Ensure `NuGetAuthenticate@1` runs before `NuGetCommand@2`. The feed must be in the same ADO org, or configure PAT-based auth. |
| Build fails with missing references | Verify `packages.config` versions match the target environment's application version. |
| Deploy fails with EnvironmentStateInvalid | Environment is in `Servicing` state — the readiness check should wait automatically (up to `readinessTimeoutMinutes`). If it still fails, check PPAC for a stuck operation. |
| Deploy times out in Verify job | Increase `maxDeployWaitMinutes` parameter in the deploy stage (default: 180 min). |
| Provision fails with 403 | Service principal needs Power Platform Administrator role in Entra ID. |
| `EnvironmentGovernanceConfigurationCopyError` on copy | Source and target environments have different governance (Managed Environment) settings. Post-provision copy now auto-aligns governance before copying. For manual copies, match the Managed Environment setting in PPAC. |
| Cleanup pipeline deletes active environment | Environments must have a `branch:refs/heads/...` tag in their description. Untouched environments are ignored. Always run with `dryRun: true` first. |
| DMF refresh returns 401 | Service principal must be registered as an application user in the F&O environment (see [variable-groups.md](variable-groups.md#step-4-register-as-fo-application-user-for-odata-access)). |
| Pipeline trigger doesn't fire | Verify the `source` name in `resources.pipelines` exactly matches the pipeline definition name in ADO. For `deploy.yml` and `release.yml`, the build pipeline must be named `Build X++`. |
| Cache not working | Check that `$(NugetsPath)` resolves correctly. The cache key includes `$(Agent.OS)` to prevent cross-platform collisions. |
| Release pipeline triggered unexpectedly | `release.yml` chains off `Deploy` (not `Build X++`) with `stages: [DeployDM]`. Only release/* branches reach deploy.yml, so only they can trigger release.yml. Add Branch control checks on GOLD/PROD environments for defense-in-depth. |
| AADSTS7000215 Invalid client secret | Check the **service connection** secret (Project Settings > Service connections), not the variable group. These are separate credentials. |
| Pipeline pauses waiting for "Permit" | Pre-authorize variable groups and service connections before first run. See [Pipeline Permissions](variable-groups.md#pipeline-permissions-for-service-connections) and [Variable Group Permissions](variable-groups.md#pipeline-permissions). |
