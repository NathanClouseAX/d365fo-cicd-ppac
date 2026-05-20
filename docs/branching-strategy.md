# Branching Strategy

How branches map to environments, pipelines, and the release lifecycle.

## Branch Types

| Branch | Purpose | Builds? | Deploys To |
|---|---|---|---|
| `feature/*`, `bug/*`, `hotfix/*` | Development work | Yes | Ephemeral sandbox only |
| `main` | Integration — sprint PRs merge here | Yes | Nowhere (compilation validation only) |
| `release/sprint-N` | Sprint release candidate | Yes | UAT → DM → GOLD → PROD |

## Rules

- **Main never deploys.** It builds to catch compilation errors early, but no pipeline
  deploys main to any shared environment.
- **Release branches are the only path to shared environments.** Cutting a release branch
  is the deliberate signal that a batch is ready for validation.
- **Feature branches get ephemeral sandboxes.** Developers validate individually before
  merging to main. Sandboxes are tagged with their source branch ref and cleaned up
  automatically by the daily `cleanup-environments.yml` pipeline after branch deletion.
- **One release branch per sprint.** Named `release/sprint-N` (e.g., `release/sprint-12`).

## Pipeline Mapping

> **Diagram:** [Pipeline Architecture](diagrams/pipeline-architecture.mmd)

```
feature/* push
    │
    ├── build.yml                    (compile + package)
    ├── provision-environment.yml    (create sandbox, tag with branch ref)
    └── post-provision-copy.yml      (wait 4h, match governance, copy DB, DMF refresh)

main push
    │
    └── build.yml                    (compile only — no deploy)

release/* push
    │
    ├── build.yml                    (compile + package)
    ├── deploy.yml                   (UAT → DM validation)
    └── release.yml                  (GOLD → PROD, after DM succeeds)

scheduled (daily 06:00 UTC)
    │
    └── cleanup-environments.yml     (delete orphaned sandboxes whose branches are gone)
```

## Branch Lifecycle

> **Diagram:** [Branch Strategy](diagrams/branch-strategy.mmd)

```
Sprint start:
  main exists with prior sprint's code

During sprint:
  feature/xyz ── PR ──► main        (repeated per feature)

Sprint end:
  git checkout -b release/sprint-12 main

After go-live:
  release/sprint-12 stays open for hotfixes
  main continues accumulating next sprint's work

Next sprint end:
  git checkout -b release/sprint-13 main
```

## Protection Rules (Recommended)

Configure in Azure DevOps > Repos > Branches > Branch policies:

| Branch | Policies |
|---|---|
| `main` | Require PR, require `PR Validation` build to pass (compile + integration test), minimum 1 reviewer |
| `release/*` | Require PR (for hotfixes), require `PR Validation` build to pass |

> **Note:** PR Validation includes a full integration test (provision → 3-hour agentless
> wait → copy DB → deploy → DB sync → automated tests → teardown). This takes ~4-5 hours
> total but ensures every commit in `main` has been validated in a real F&O environment.
> The 3-hour wait uses a server pool (zero agent cost). By the time a release branch is
> cut, all code has already passed regression testing.

## When to Cut a Release Branch

Cut `release/sprint-N` from `main` when:

1. All features planned for this sprint have been merged to `main`
2. The daily build on `main` is green (no compilation errors)
3. The team agrees the sprint scope is complete

Do NOT wait for business user UAT sign-off before cutting — the release branch IS what
gets deployed to UAT for sign-off.

## Phased Go-Lives

For implementations with multiple user groups going live in stages:

- Each go-live wave corresponds to a sprint release: `release/sprint-8` (wave 1),
  `release/sprint-12` (wave 2), etc.
- Later waves include all features from earlier waves (since `main` accumulates everything)
- If a wave needs a hotfix after go-live while the next wave is in development,
  the hotfix targets the active release branch and is also merged back to `main`
