# D365 Finance & Operations — CI/CD with PPAC

> **Presentation repo**: Full CI/CD pipeline examples for Dynamics 365 Finance and Operations
> using Power Platform Admin Center (PPAC) APIs, replacing the legacy LCS deployment model.

## Why PPAC?

Microsoft is retiring Lifecycle Services (LCS) for D365 F&O environment management. PPAC provides:

- **API-first environment management** — provision, copy, delete, and manage environments programmatically
- **Unified platform** — F&O environments managed alongside Dataverse, Power Apps, and Power Automate
- **Modern deployment** — async package deployment with status polling via Management API
- **No build VMs required** — X++ compilation on hosted agents using NuGet DevALM packages
- **One Version alignment** — pin to specific application/platform versions via `packages.config`

## Branch Strategy

```
feature/*  ──PR──►  main  ──cut──►  release/sprint-N
                      │                    │
              build only          deploy.yml triggers
              (no deploy)                  │
                                           ▼
                                      UAT ──► DM
                                           │
                                    release.yml triggers
                                    (only after DM succeeds)
                                           │
                                           ▼
                                     GOLD ──► PROD
```

| Branch | Purpose | Deploys To |
|---|---|---|
| `feature/*`, `bug/*`, `hotfix/*` | Development work | Ephemeral sandbox (provision pipeline) |
| `main` | Integration — sprint PRs merged here | Build only (no deployment) |
| `release/sprint-N` | Sprint release candidate | UAT, DM (validation) → GOLD, PROD (go-live) |

**Sprint-based release workflow:**
1. During sprint: developers merge features to `main` via PR (build validates, devs test in ephemeral sandboxes)
2. Sprint end → cut `release/sprint-N` from `main`
3. Release branch build → deploy.yml validates in UAT + DM → release.yml promotes to GOLD → PROD
4. Hotfix → branch off `release/sprint-N`, fix, merge back to release AND `main`
5. Next sprint → cut `release/sprint-N+1` from `main` (includes all new work)

## Pipeline Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    PULL REQUEST                              │
│                  (target: main)                              │
└────────────────────────┬────────────────────────────────────┘
                         │
                         ▼
┌────────────────────────────────────────┐
│  pr-validation.yml                     │
│  Stage 1: Compile X++ + Package        │  (~10 min)
│  Stage 2: Integration Test             │  (~4-5 hours)
│    Provision → 3h agentless wait       │
│    → Copy DB → Deploy → DB Sync       │
│    → Test → Teardown                   │
│  Must pass before merge allowed        │
└────────────────────────────────────────┘


┌─────────────────────────────────────────────────────────────┐
│                      BRANCH PUSH                            │
│    main, feature/*, bug/*, hotfix/*, release/*              │
└────────────────────────┬────────────────────────────────────┘
                         │
                         ▼
┌────────────────────────────────────────┐
│  build.yml                             │  Compile + create
│  Uses: templates/build-xpp.yml         │  deployable package
│  Publishes artifact: 'drop'            │  (all branches)
└────────────────────────┬───────────────┘
                         │
              │
     (release/* builds only)
              │
              ▼
┌─────────────────────────────────┐
│  deploy.yml                     │
│  UAT ──► DM (validation)       │
└────────────────┬────────────────┘
                 │
        (after DM succeeds)
                 │
                 ▼
┌─────────────────────────────────┐
│  release.yml                    │
│  GOLD ──► PROD (go-live)       │
└─────────────────────────────────┘


┌─────────────────────────────────────────────────────────────┐
│                FEATURE BRANCH PUSH                           │
│              (feature/*, bug/*, hotfix/*)                    │
└────────────────────────┬────────────────────────────────────┘
                         │
                         ▼
┌────────────────────────────────────────┐
│  provision-environment.yml             │  Provisions PPAC sandbox
│  + templates/provision-finops-env...   │  with F&O template
└────────────────────────┬───────────────┘
                         │ triggers
                         ▼
┌────────────────────────────────────────┐
│  post-provision-copy.yml               │  Waits 4 hours, matches
│  + templates/copy-finops-env...        │  governance, copies DB,
│                                        │  refreshes DMF entities
└────────────────────────────────────────┘


┌────────────────────────────────────────┐
│  cleanup-environments.yml              │  Daily scheduled: deletes
│  (daily 06:00 UTC, dry run default)    │  orphaned branch envs
└────────────────────────────────────────┘


┌────────────────────────────────────────┐
│  dmf-entity-refresh.yml               │  Post-deploy: refresh
│  (manual trigger)                      │  DMF entity list
└────────────────────────────────────────┘


┌────────────────────────────────────────┐
│  delete-environment.yml                │  Manual cleanup: tear down
│  (manual trigger)                      │  specific environments
└────────────────────────────────────────┘
```

## Pipeline Files

### Core Pipelines (`Pipelines/`)

| File | Purpose | Trigger |
|---|---|---|
| `build.yml` | Compile X++ and publish deployable package artifact | Push to main, feature/\*, bug/\*, hotfix/\*, release/\* |
| `deploy.yml` | Deploy to UAT → DM for validation | Artifact from `build.yml` (release/\* branches only) |
| `release.yml` | Promote through GOLD → PROD for go-live | Chained from `deploy.yml` (release/\* only, after DM succeeds) |
| `pr-validation.yml` | Compile + integration test: provision env, copy DB, deploy, test, teardown | PR to `main` |
| `provision-environment.yml` | Provision a new PPAC environment with F&O template | `feature/*`, `bug/*`, `hotfix/*` branch push |
| `post-provision-copy.yml` | After provisioning completes, wait then copy F&O database from source | Triggered by provision pipeline completion |
| `copy-environment.yml` | Copy/restore an environment (standalone — accepts GUID or display name) | Manual |
| `cleanup-environments.yml` | Daily cleanup: delete orphaned environments whose branches no longer exist | Scheduled (daily 06:00 UTC) |
| `delete-environment.yml` | Delete a specific PPAC environment by GUID or display name | Manual |
| `dmf-entity-refresh.yml` | Refresh DMF entity list on a F&O environment via OData | Manual |

### Reusable Templates (`Pipelines/templates/`)

| Template | Used By | What It Does |
|---|---|---|
| `build-xpp.yml` | `build.yml`, `pr-validation.yml` | Composable steps: NuGet restore, compile X++, create package, publish artifact |
| `test-finops-environment.yml` | `pr-validation.yml` | OData smoke tests + SysTest suite execution against a deployed environment |
| `deploy-finops-package.yml` | `deploy.yml`, `release.yml` | 2-job deploy: readiness check → Submit & Wait (up to 3h) → Verify via PPAC API |
| `provision-finops-environment.yml` | `provision-environment.yml` | Provisions PPAC environment via `New-AdminPowerAppEnvironment` |
| `copy-finops-environment.yml` | `post-provision-copy.yml`, `copy-environment.yml` | Copies environment via `Copy-PowerAppEnvironment` |
| `teams-notify.yml` | All pipelines | Sends Microsoft Teams webhook notifications (MessageCard format) |

### Legacy — LCS-Era Comparison (`Pipelines/legacy/`)

| File | What It Shows |
|---|---|
| `standard-build.yml` | Old build approach: VS 2019, separate NuGet repo, single-job, `XppCreatePackage@0` |
| `bap-rest-provision.yml` | Direct BAP REST API provisioning (`api.bap.microsoft.com` v2020-08-01) |
| `build-deploy.yml` | Monolithic build+deploy in one pipeline (before split into build → deploy → release) |

## Documentation

### Process Guides (`docs/`)

| Document | Audience | Description |
|---|---|---|
| **[Branching Strategy](docs/branching-strategy.md)** | All | Branch types, rules, pipeline mapping, when to cut releases |
| **[Sprint Development](docs/sprint-development.md)** | Developers | Feature branch workflow, ephemeral sandboxes, PR process |
| **[Release Process](docs/release-process.md)** | Team leads, release managers | Cutting a release, validation, approval, go-live timeline |
| **[Hotfix Process](docs/hotfix-process.md)** | Developers, release managers | Fixing bugs on active releases, merge-back requirements |
| **[Deployment Process](docs/deployment-process.md)** | DevOps, architects | Technical deployment mechanics, readiness checks, troubleshooting |
| **[Environment Lifecycle](docs/environment-lifecycle.md)** | All | Sandbox creation/deletion, permanent environments, known good source |

### Setup Guides (`docs/`)

| Document | Audience | Description |
|---|---|---|
| **[Pipeline Setup](docs/pipeline-setup.md)** | DevOps admins | Creating pipeline definitions, environments, NuGet feed, troubleshooting |
| **[Variable Groups & Service Connections](docs/variable-groups.md)** | DevOps admins | Entra app registration, variable groups, service connections, approvals/checks |

### Diagrams (`docs/diagrams/`)

Mermaid diagrams for presentations and documentation:

| File | Description |
|---|---|
| [pr-validation.mmd](docs/diagrams/pr-validation.mmd) | PR integration test: provision → copy → deploy → test → teardown |
| [pipeline-architecture.mmd](docs/diagrams/pipeline-architecture.mmd) | Full pipeline graph: triggers → build → deploy → release |
| [branch-strategy.mmd](docs/diagrams/branch-strategy.mmd) | Feature → main → release → environments |
| [artifact-promotion.mmd](docs/diagrams/artifact-promotion.mmd) | Immutable artifact flowing through all environments |
| [deploy-template.mmd](docs/diagrams/deploy-template.mmd) | Two-job deploy pattern: readiness → deploy → verify |
| [sprint-workflow.mmd](docs/diagrams/sprint-workflow.mmd) | End-to-end sprint: dev → release → go-live |
| [environment-lifecycle.mmd](docs/diagrams/environment-lifecycle.mmd) | Ephemeral sandbox: provision → use → delete |
| [hotfix-flow.mmd](docs/diagrams/hotfix-flow.mmd) | Hotfix: branch from release → fix → deploy → merge back |
| [security-layers.mmd](docs/diagrams/security-layers.mmd) | Defense-in-depth: 6 gates before credential is issued |
| [lcs-vs-ppac.mmd](docs/diagrams/lcs-vs-ppac.mmd) | Side-by-side comparison of legacy vs modern |

## PPAC vs LCS

| Capability | LCS (Legacy) | PPAC (This Repo) |
|---|---|---|
| **Build** | Build VMs optional (~$500/mo) or hosted agents via NuGet | Hosted agents + NuGet DevALM packages (monthly parallel job) |
| **Deploy** | Upload to Asset Library → manual apply or limited API | `PowerPlatformDeployPackage@2` directly to environment |
| **Provision** | Manual in LCS portal | `New-AdminPowerAppEnvironment` — branch-triggered |
| **Environment copy** | LCS portal only | `Copy-PowerAppEnvironment` in pipeline |
| **Environment delete** | LCS portal only | `Remove-AdminPowerAppEnvironment` in pipeline |
| **Deploy wait cost** | Synchronous — agent blocked up to 4 hours | Readiness check + synchronous wait with verification |
| **Status polling** | Limited LCS API | Full PPAC Management API (`api.powerplatform.com`) |
| **PR validation** | Build VM required | Full integration test: provision → 3h wait → copy DB → deploy → test → teardown per PR |
| **Notifications** | None built-in | Teams webhooks at every stage |
| **Environment lifecycle** | Manual, portal-driven | Fully automated: provision → copy DB → deploy → verify → cleanup |
| **Release management** | Manual LCS release cadence | Branch-based: main → validate, release/\* → promote |

## Prerequisites

> For detailed setup instructions with screenshots and troubleshooting, see the
> [Pipeline Setup Guide](docs/pipeline-setup.md) and
> [Variable Groups & Service Connections](docs/variable-groups.md).

### Azure DevOps Variable Groups

| Group | Variables | Used By |
|---|---|---|
| `PowerPlatform-Auth` | `PP_TENANT_ID`, `PP_APPLICATION_ID`, `PP_CLIENT_SECRET` | All pipelines |
| `Teams-Secrets` | `TeamsWebhookUrl` | Teams notifications |
| `FinOps-Options` | `database-source-environment-guid` | Post-provision DB copy |

### Service Connections

Each target environment needs a **Power Platform SPN** service connection in Azure DevOps.
The names below are examples — replace with your own:

| Connection Name | Environment |
|---|---|
| `FO-UAT` | UAT |
| `FO-DM` | Data Migration |
| `FO-GOLD` | Gold / Pre-Prod |
| `FO-Prod` | Production |

> **Note:** The pipelines in this repo use `Nclouse-UAT`, `Nclouse-DM`, etc. as example names.
> Update the `serviceConnectionName` values in `deploy.yml` and `release.yml` to match yours.
> The `release.yml` pipeline reuses the same UAT/DM connections via `deploy.yml` before promoting.

### Service Principal Permissions

The app registration (from `PowerPlatform-Auth`) needs:

- **Power Platform Administrator** or **Dynamics 365 Administrator** role in Entra ID
- For environment management: provisioning, copy, delete, and deployment operations
- For DMF refresh (`dmf-entity-refresh.yml`): the SPN must also be registered as an
  **application user** in the target F&O environment — the Entra role alone is not sufficient
  for OData access (see [detailed instructions](docs/variable-groups.md#step-4-register-as-fo-application-user-for-odata-access))

### Azure Artifacts NuGet Feed

X++ DevALM compiler packages are pulled from an Azure Artifacts feed.
Feed configuration: `src/xpp/build/nuget.config`

Build pipelines use `Cache@2` to cache the ~2 GB DevALM packages between runs.
On cache hit, the NuGet download is skipped entirely — the cache key is derived from
`packages.config`, so version bumps automatically invalidate the cache.

### Package Version Pinning (One Version)

`src/xpp/build/packages.config` pins the F&O application and platform versions used for compilation:

```xml
<!-- Application version (e.g., 10.0.2428.99 = 10.0.42 CU) -->
<package id="Microsoft.Dynamics.AX.Application.DevALM.BuildXpp" version="10.0.2428.99" />

<!-- Platform/compiler version -->
<package id="Microsoft.Dynamics.AX.Platform.CompilerPackage" version="7.0.7778.47" />
```

Update these versions when adopting a new One Version update. The version must match
the target environment's application version — mismatches will cause deployment failures.

### F&O Environment Templates

The provisioning pipeline uses `D365_FinOps_Finance` as the default template.
Available templates include:

| Template | Description |
|---|---|
| `D365_FinOps_Finance` | Dynamics 365 Finance |
| `D365_FinOps_SCM` | Dynamics 365 Supply Chain Management |
| `D365_FinOps_Commerce` | Dynamics 365 Commerce |
| `D365_FinOps_Project` | Dynamics 365 Project Operations |

### PowerShell Module Versioning

The pipelines install `Microsoft.PowerApps.Administration.PowerShell` without version pinning
for simplicity. For production use, pin to a specific version with `-RequiredVersion` to ensure
reproducible builds:

```powershell
Install-Module -Name Microsoft.PowerApps.Administration.PowerShell -RequiredVersion 2.0.190 -Force -Scope CurrentUser
```

## Repository Structure

```
src/
  xpp/
    models/            ← X++ modules (source of truth, compiled by build pipeline)
      AAXParameterWorkflow/
      AAXBusinessEvents/
      AAXJSONGenerator/
      AAXWarehouseDimensionLink/
      AAXPOCloseHelper/
      AAXDataEntities/
    projects/          ← Solution and project files (Build.sln references X++ + C#)
      Build/           ← Master build solution
    build/             ← NuGet feed config + packages.config (version pinning)
  csharp/              ← C# class libraries called by X++ via CLR interop
    AAXIntegration/    ← Example: integration helpers consumed by X++ modules
  dualwrite/           ← Dual write map configurations (deployed post-F&O)
Pipelines/             ← Azure DevOps YAML pipeline definitions
  templates/           ← Composable pipeline templates
  legacy/              ← LCS-era comparison files
docs/                  ← Process guides, setup guides, Mermaid diagrams
```

### X++ Modules (`src/xpp/models/`)

| Module | Purpose |
|---|---|
| `AAXParameterWorkflow` | Parameter versioning and workflow-driven configuration |
| `AAXBusinessEvents` | Custom business events (vendor create/update) |
| `AAXJSONGenerator` | JSON serialization utilities |
| `AAXWarehouseDimensionLink` | Warehouse dimension linking extensions |
| `AAXPOCloseHelper` | Purchase order closing utilities |
| `AAXDataEntities` | Custom data entities for integrations |

Build solution: `src/xpp/projects/Build/Build.sln`

### C# Class Libraries (`src/csharp/`)

C# class libraries that X++ references live under `src/csharp/` and are included
in `src/xpp/projects/Build/Build.sln`. MSBuild compiles them alongside X++ — the
output DLLs are included in the same deployable package.

X++ calls C# via CLR interop (assembly references in the X++ model descriptor).
Common use cases: HTTP clients, JSON serialization, complex integration logic.

| Project | Purpose |
|---|---|
| `AAXIntegration` | Integration helpers (HTTP, payload construction) |

**Pure standalone C# projects** (Azure Functions, microservices, integration middleware)
belong in their own repositories. Consume them via NuGet package reference or
Azure Artifacts if cross-repo dependency is needed.

### Dual Write Configurations (`src/dualwrite/`)

Dual write map configurations are stored as XML config files. These define which
entity maps to deploy, their sync direction (master), target state, and execution mode.

Dual write configs are deployed separately after F&O is running — they depend on both
F&O entities and Dataverse tables being available. Deployment uses the Data Integrator
API and can be automated as a post-deploy step.

## Presentation Walkthrough

Recommended order:

1. **The problem** — LCS limitations: manual provisioning, build VMs, synchronous deploys, limited APIs
2. **Branch strategy** — main for validation, release/\* for go-live waves, feature/\* for ephemeral sandboxes
3. **PR validation** — `pr-validation.yml`: compile X++ on PRs using hosted agents, no VMs
4. **Build** — `build.yml`: NuGet DevALM packages on hosted agents, composable `templates/build-xpp.yml`
5. **The deploy pattern** — `templates/deploy-finops-package.yml`: readiness check → Submit & Wait → Verify
6. **Validation chain** — `deploy.yml`: release/\* builds validate in UAT → DM (main does not deploy)
7. **Release promotion** — `release.yml`: chains off deploy.yml (only after DM succeeds) → GOLD → PROD with approval gates
8. **Artifact gating** — release.yml can only see artifacts that passed deploy.yml — structural enforcement, not just process
9. **Lifecycle automation** — DB copy after provisioning, governance matching, DMF refresh, automated environment cleanup, Teams notifications
10. **Compare** — `Pipelines/legacy/` files: what the same workflow looked like under LCS
