# Presentation Outline

**Title:** Immutable Artifacts: Automated F&O Package Promotion from Commit to Production

**Duration:** 45 minutes

**Tagline:** *Once a deployable package is created, it must never change. Only its fate does.*

---

## Opening (3 min)

**Hook:** "How many of you have deployed a package to production and weren't 100% sure it was the same binary that passed UAT?"

- The promise: a single artifact, created once, carried forward untouched — or rolled back with certainty
- What we'll cover: immutable packaging, durable versioning, artifact promotion, environment automation
- What we won't cover: X++ development patterns, Dataverse solutions, Power Platform ALM

---

## Part 1: Why LCS Can't Get You There (5 min)

**Key message:** LCS was designed for a different era. The world moved on.

| LCS Reality | What We Want |
|---|---|
| Manual portal deployments | API-driven, pipeline-triggered |
| Build VMs (~$500/mo) | Hosted agents, pay-per-minute |
| Upload to Asset Library → apply manually | One artifact flows through all environments untouched |
| No automated PR validation | Full integration test on every PR |
| Environment provisioning = portal or support ticket | Branch push = sandbox in minutes |
| No structural enforcement | Pipelines make it impossible to skip validation |

**Talking point:** LCS isn't going away tomorrow, but PPAC APIs are where all the new investment is. This is the direction.

---

## Part 2: The Immutable Artifact (7 min)

**Key message:** The package is created exactly once. Everything after that is promotion.

### The principle

- A deployable package is compiled from source, versioned, and published as a pipeline artifact
- No build VM required — NuGet DevALM packages provide the compilation toolchain on hosted agents
- From the moment the artifact is published, it never changes — same binary in every environment

### Why immutability matters

- If UAT passes and GOLD fails, you **know** it's environmental — not a build variance
- Rollback means deploying a known-good artifact, not rebuilding from a commit you hope is right
- Auditors can trace any production binary back to the exact source commit

### Versioning strategy

- Build number baked into module metadata at compile time
- Application/platform version pinned in source control — the compilation environment is deterministic
- Cache invalidates on version bump — fresh dependencies, no drift

---

## Part 3: Branch Strategy & Structural Enforcement (7 min)

**Key message:** Branches control where code CAN go. Pipelines control where it DOES go.

### The model

```
feature/* → PR (integration test) → main → release/* → UAT → DM → GOLD → PROD
```

### PR as the quality gate

Every PR to main runs a full integration test:
1. Provision ephemeral sandbox
2. Copy database from known-good source
3. Deploy the package + DB sync
4. Run automated tests
5. Tear down (always, even on failure)

**Key insight:** By the time a release branch is cut from main, every commit has already been validated in a real F&O environment. The UAT deployment is for business acceptance, not regression catching.

### Enforcement layers (not just process)

- PR gate must pass before merge — main never has untested code
- Deploy pipeline only triggers from `release/*` — main can't reach UAT
- Release pipeline chains off deploy success — untested code can't reach GOLD
- Service connections have branch restrictions — production credentials only issued to release branches
- The artifact never changes — only its **fate** does (promoted or abandoned)

**Talking point:** "Process" means someone has to remember. "Structure" means they can't forget.

---

## Part 4: The Deploy Pattern (8 min)

**Key message:** Deploy once, verify independently, fail fast for downstream.

### The two-job pattern

For each environment, the pipeline runs two independent concerns:

```
Job 1: Submit & Wait
  ├── Readiness check (is the environment in a deployable state?)
  ├── Deploy package (synchronous — agent stays connected)
  └── Report outcome

Job 2: Verify
  ├── Independent status check via management API
  ├── Notify stakeholders
  └── Gate downstream environments
```

### Environment readiness

- Before deploying, poll the environment state
- Environments can be in "Servicing" from Microsoft updates or prior operations
- Without a readiness check: instant failure, manual retry, wasted time
- With it: the pipeline waits gracefully, deploys when ready

### Why synchronous wait matters

- If the agent disconnects mid-deploy, PPAC **cancels** the operation
- The agent must stay connected until the platform confirms completion
- Fire-and-forget = failed deployments you don't know about

### Verification as a separate concern

- Don't trust the deploy task's exit code alone
- Query the management API independently — what does the platform actually say?
- This is the gate for downstream: GOLD only proceeds if DM verification passes

---

## Part 5: When Things Go Wrong (5 min)

**Key message:** Immutable artifacts make failures diagnosable and recovery predictable.

### UAT acceptance testing fails

The artifact compiled, passed automated tests, and deployed successfully — but business users reject it.

- The release branch stays open. Fix the code on the release branch, not main.
- New commit → new artifact → redeploy to UAT. Same pipeline, same gates.
- The failed artifact is abandoned. The new artifact starts its own journey through environments.
- **Key point:** You never patch an artifact. You replace it with a new one that goes through the same process.

### Production needs a rollback

A critical issue is found after go-live.

- Every previous artifact still exists in the pipeline history, unchanged.
- Rollback = redeploy the last known-good artifact. No rebuild, no guessing.
- Because the artifact is immutable, you **know** you're restoring exactly what was running before.
- **Contrast with LCS:** Manually finding the right package in Asset Library, hoping it matches what was deployed.

### Hotfix process

An urgent fix is needed while a release is in flight or already in production.

- Branch from the **release branch**, not main (targets the exact deployed code)
- Minimal fix, same pipeline gates — still goes through UAT → DM → GOLD → PROD
- Merge back to main afterward to prevent regression in the next sprint
- The pipeline chain **cannot be bypassed by design** — even emergency fixes pass through all environments

### Environment is broken

A deploy corrupted the environment, database copy failed, or the environment is stuck.

- Environments are disposable — tear down, reprovision, redeploy
- The artifact hasn't changed, so the new environment gets exactly the same binary
- This is "cattle not pets" in practice: the code is the source of truth, not the environment

**Talking point:** "Every failure mode has the same answer: the artifact doesn't change, and environments are replaceable. That's the whole point."

---

## Part 6: Environment Automation (5 min)

**Key message:** Everything LCS did manually, PPAC does via API.

### Branch-triggered provisioning

- Push a feature branch → pipeline provisions a sandbox automatically
- Environment ready in 15-30 minutes with F&O template applied
- Developer gets a real environment with no portal clicks, no support tickets

### Database copy as a pipeline step

- After provisioning, copy data from a known-good source automatically
- Same mechanism for: sandbox initialization, UAT refresh, pre-release GOLD refresh
- Developers test against real data from day one

### DMF entity refresh

- After deploying new data entities, trigger a refresh via OData
- Automated — no manual "publish all entities" step
- Requires the service principal registered as an F&O application user (common gotcha)

### Lifecycle management

```
Developer pushes feature branch
  → Sandbox created automatically
  → DB copied from known-good source
  → Developer tests with real data
  → PR merged → sandbox deleted
  → Zero infrastructure to manage
```

**Talking point:** "Infrastructure as cattle, not pets." Environments are disposable. The code is what matters.

---

## Part 7: Security & Governance (3 min)

**Key message:** Defense-in-depth — multiple independent enforcement layers.

| Layer | What It Prevents |
|---|---|
| Branch triggers | Unauthorized code reaching deployment pipelines |
| Pipeline chaining | Skipping validation stages |
| Environment approvals | Unreviewed changes reaching sensitive environments |
| Service connection branch control | Rogue pipelines accessing production credentials |
| Business hours restriction | Off-hours production changes |

**Key point:** Even if someone creates a rogue pipeline referencing the production service connection, branch control on the connection blocks it. The credential simply isn't issued.

**Talking point:** Each layer is independent. Any single layer can fail and the others still hold. That's defense-in-depth.

---

## Part 8: Getting Started (5 min)

**Key message:** You don't have to do everything at once.

### The minimum viable pipeline

1. **Build pipeline** — compile on hosted agents, no build VM, publish artifact
2. **PR validation** — even if it's just compile + basic tests, it's better than nothing
3. **Deploy pipeline** — promote the artifact, don't rebuild

### Prerequisites

- Service principal registered with PPAC (`New-PowerAppManagementApp`)
- NuGet feed with DevALM packages (public or private)
- One variable group for secrets (tenant ID, client ID, client secret)

### Common gotchas

| Gotcha | Solution |
|---|---|
| "Package DLL is invalid" | Artifact path wrong — verify the artifact is actually published upstream |
| Secrets empty in scripts | ADO secrets need macro syntax `$(VarName)`, not environment variables |
| PPAC API routing errors | Use the PowerShell admin module, not raw REST |
| Deploy cancels mid-apply | Agent disconnected — use synchronous wait, never fire-and-forget |
| Build number format errors | Must be `#.#.#.#` — no text prefixes |

---

## Closing (2 min)

**Recap the promise:**

> Once a deployable package is created, it must never change. Only its fate does.

What this delivers:
- **Immutable artifact** — built once, promoted through environments unchanged
- **Structural enforcement** — impossible to skip validation, not just "don't forget"
- **Full automation** — provision, copy, deploy, verify, cleanup — no portal clicks
- **Zero drift** — same binary, deterministic compilation, versioned dependencies

**Call to action:**
- Start with build + PR validation — you'll never go back to build VMs
- Add deploy automation once you trust the artifact
- Environment provisioning is the cherry on top, not the first step

**Q&A transition:** "What's the one part of your current ALM process that keeps you up at night?"

---

## Appendix: Timing Budget

| Section | Minutes | Cumulative |
|---|---|---|
| Opening | 3 | 3 |
| Why LCS Can't Get You There | 5 | 8 |
| The Immutable Artifact | 7 | 15 |
| Branch Strategy & Enforcement | 7 | 22 |
| The Deploy Pattern | 8 | 30 |
| When Things Go Wrong | 5 | 35 |
| Environment Automation | 5 | 40 |
| Security & Governance | 3 | 43 |
| Getting Started / Closing | 4 | 47 |

**If running long:** Trim "Environment Automation" — point to docs/repo.
**If running short:** Expand "When Things Go Wrong" with audience horror stories — everyone has one.

---

## Appendix: Preparation Checklist

- [ ] At least one completed build → deploy → release chain to show artifact flow
- [ ] PPAC admin center showing environment operations history
- [ ] Pipeline run history showing the progression through environments
- [ ] Slide showing LCS workflow vs pipeline workflow side-by-side
- [ ] Backup screenshots in case live demo fails
