# Variable Groups & Service Connections

How to configure the Azure DevOps variable groups and service connections required by the pipelines.

## Entra ID App Registration

All pipelines authenticate to Power Platform using a single service principal.

### Step 1: Register the Application

1. Go to [Entra ID > App registrations](https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade)
2. Click **New registration**
3. Name: `D365-FO-Pipeline-SPN` (or your preferred name)
4. Supported account types: **Single tenant**
5. Click **Register**
6. Note the **Application (client) ID** and **Directory (tenant) ID**

### Step 2: Create a Client Secret

1. In the app registration, go to **Certificates & secrets**
2. Click **New client secret**
3. Description: `Pipeline auth`
4. Expiration: Choose based on your rotation policy (recommendation: 12 months)
5. **Copy the secret value immediately** — it will not be shown again

### Step 3: Assign Power Platform Admin Role

The service principal needs to be registered as a **Power Platform management application**.
This is a separate step from the Entra app registration — there is no UI for it in the
Power Platform admin center. It must be done programmatically.

> **Important:** Entra directory roles (Power Platform Administrator, Global Administrator)
> are **not** a substitute for this registration. They do not grant access to the BAP admin
> APIs. See [Microsoft docs](https://learn.microsoft.com/en-us/power-platform/admin/powerplatform-api-create-service-principal)
> and [RBAC reference](https://github.com/NathanClouseAX/PPACEnvironmentPrototyper/blob/main/docs/RBAC.md).

**Option A: PowerShell (recommended)**

An admin must run this in their own user context — the SPN cannot register itself:

```powershell
Install-Module Microsoft.PowerApps.Administration.PowerShell -Force
Add-PowerAppsAccount   # interactive login as a Power Platform admin
New-PowerAppManagementApp -ApplicationId "<your-application-id>"
```

**Option B: PAC CLI**

```
pac admin create-service-principal --environment <environment-id>
```

This both creates the Entra app registration and registers it with Power Platform.

**Option C: REST API**

```
PUT https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/adminApplications/{CLIENT_ID}?api-version=2020-10-01
Authorization: Bearer <admin-user-token>
```

The bearer token must come from an admin user's interactive login (client credentials flow
will not work for this call).

### Step 4: Register as F&O Application User (for OData access)

Required for `dmf-entity-refresh.yml` and any pipeline that calls F&O data APIs directly.
The Entra role alone is not sufficient for OData access to F&O.

1. Open the target F&O environment (e.g., `https://myenv.operations.dynamics.com`)
2. Navigate to **System administration > Setup > Microsoft Entra applications**
3. Click **New**
4. **Client ID**: Paste the Application (client) ID from Step 1
5. **Name**: `Pipeline SPN`
6. **User ID**: Select or create a service account user with appropriate F&O security roles

> Repeat this step for each F&O environment where the SPN needs OData access.

---

<!-- SCREENSHOT: ADO Pipelines > Library page showing the three variable groups (PowerPlatform-Auth, Teams-Secrets, FinOps-Options) -->

## Variable Group: PowerPlatform-Auth

**Used by:** All pipelines

### Setup

1. Azure DevOps > **Pipelines > Library** > **+ Variable group**
2. Name: `PowerPlatform-Auth`
3. Add these variables:

| Variable | Value | Secret? |
|---|---|---|
| `PP_TENANT_ID` | Entra tenant ID (GUID) | No |
| `PP_APPLICATION_ID` | Application (client) ID from the app registration | No |
| `PP_CLIENT_SECRET` | Client secret value from Step 2 | **Yes** (click the lock icon) |

### Where to Find These Values

| Value | Location |
|---|---|
| Tenant ID | Entra ID > Overview > **Tenant ID**. Or: `az account show --query tenantId` |
| Application ID | Entra ID > App registrations > your app > **Application (client) ID** |
| Client Secret | Created in Step 2 (not retrievable after creation — regenerate if lost) |

### Pipeline Permissions

Pre-authorize pipelines to avoid the "Permit" prompt on first run:

1. Click on the `PowerPlatform-Auth` variable group
2. Go to **Pipeline permissions** (or click **⋮** > **Security**)
3. Click **+** and add: PR Validation, Deploy, Release, Create Environment, Post-Provision Copy,
   Copy Environment, Delete Environment, DMF Entity Refresh, Cleanup Environments

Alternatively, click **Open access** to allow all pipelines in the project.

<!-- SCREENSHOT: Pipeline permissions dialog showing authorized pipelines on the PowerPlatform-Auth variable group -->

> **Tip:** The same "Pipeline permissions" pattern applies to all variable groups and
> service connections. Pre-authorizing during setup eliminates the "Permit" prompt
> that otherwise pauses pipelines on their first run.

---

## Variable Group: Teams-Secrets

**Used by:** `build.yml`, `deploy.yml`, `release.yml` (Teams notifications)

### Setup

1. Azure DevOps > **Pipelines > Library** > **+ Variable group**
2. Name: `Teams-Secrets`
3. Add:

| Variable | Value | Secret? |
|---|---|---|
| `TeamsWebhookUrl` | Incoming Webhook URL for your Teams channel | **Yes** |

### Getting a Teams Webhook URL

**Option A: Workflows (recommended)**

Microsoft is deprecating Office 365 Connectors. Use Power Automate Workflows instead:

1. Open Microsoft Teams > target channel
2. Click **...** > **Workflows**
3. Search for **Post to a channel when a webhook request is received**
4. Follow the prompts to create the workflow
5. Copy the webhook URL

**Option B: Incoming Webhook Connector (legacy)**

1. Open Microsoft Teams > target channel > **...** > **Connectors**
2. Find **Incoming Webhook** > **Configure**
3. Name it (e.g., `ADO Pipeline Notifications`)
4. Copy the webhook URL

> The pipeline's `teams-notify.yml` template uses the Office 365 MessageCard format.
> If using the Workflows approach (Option A), you may need to adapt the payload to
> Adaptive Card format depending on how the workflow is configured.

### Pipeline Permissions

Grant to: Build X++, Deploy, Release (any pipeline that sends Teams notifications).

---

## Variable Group: FinOps-Options

**Used by:** `post-provision-copy.yml`, `pr-validation.yml` (known good source environment for DB copy)

### Setup

1. Azure DevOps > **Pipelines > Library** > **+ Variable group**
2. Name: `FinOps-Options`
3. Add:

| Variable | Value | Secret? |
|---|---|---|
| `database-source-environment-guid` | GUID of the known good source environment to copy from | No |

### Finding the Environment GUID

**Option A: Power Platform Admin Center**

1. Go to [admin.powerplatform.microsoft.com](https://admin.powerplatform.microsoft.com/)
2. Click **Environments** > select the source environment
3. The GUID is in the browser URL: `environments/{GUID}/hub`

**Option B: PowerShell**

```powershell
Install-Module Microsoft.PowerApps.Administration.PowerShell -Force
Add-PowerAppsAccount
Get-AdminPowerAppEnvironment | Select-Object DisplayName, EnvironmentName | Format-Table
```

The `EnvironmentName` column contains the GUID.

**Option C: Power Platform CLI**

```
pac admin list
```

### Pipeline Permissions

Grant to: PR Validation, Post-Provision Copy.

---

## Service Connections

Each deploy stage in `deploy.yml` and `release.yml` requires a **Power Platform** service connection
to authenticate against the target F&O environment.

### Creating a Service Connection

1. Azure DevOps > **Project settings** > **Service connections** > **New service connection**
2. Select **Power Platform**
3. Authentication method: **Application Id and client secret**
4. Fill in:

| Field | Value |
|---|---|
| Server URL | Environment URL (e.g., `https://myenv.crm.dynamics.com`) |
| Tenant ID | Same as `PP_TENANT_ID` |
| Application ID | Same as `PP_APPLICATION_ID` |
| Client Secret | Same as `PP_CLIENT_SECRET` |
| Service connection name | Descriptive name (e.g., `FO-UAT`) |

5. Check **Grant access permission to all pipelines** (or grant per-pipeline later)
6. Click **Save**

<!-- SCREENSHOT: Power Platform service connection creation form in ADO Project Settings -->

### Required Connections

Create one service connection per target environment:

| Connection Name | Environment | Used By |
|---|---|---|
| `Nclouse-UAT` | UAT | `deploy.yml` Stage 1 (validation) |
| `Nclouse-DM` | Data Migration | `deploy.yml` Stage 2 (validation) |
| `Nclouse-GOLD` | Gold / Pre-Prod | `release.yml` Stage 1 (go-live promotion) |
| `Nclouse-Prod` | Production | `release.yml` Stage 2 (go-live promotion) |

> These are example names from this repo. Update the `serviceConnectionName` values
> in `deploy.yml` and `release.yml` to match your actual connection names.
> Note: Only `release/*` branch builds trigger `deploy.yml`. Main builds do not deploy.
> `release.yml` is chained from `deploy.yml` — artifacts must pass UAT + DM
> before they can reach GOLD or PROD.

### Finding the Environment URL

1. Power Platform Admin Center > **Environments** > select environment
2. Copy the **Environment URL** (e.g., `https://org12345.crm.dynamics.com`)

### Pipeline Permissions for Service Connections

To avoid the "Permit" prompt on first pipeline run, pre-authorize pipelines:

1. **Project settings** > **Service connections** > select the connection
2. Click **Security** (or the **⋮** menu > **Security**)
3. Under **Pipeline permissions**, click **+** and add each pipeline that needs access:

| Connection | Grant To |
|---|---|
| `Nclouse-UAT` | Build X++, Deploy |
| `Nclouse-DM` | Deploy |
| `Nclouse-GOLD` | Release |
| `Nclouse-Prod` | Release |

Alternatively, click **⋮** > **Open access** to allow all pipelines in the project (simpler
but less restrictive).

### Approvals and Checks on Service Connections

Service connections support the same **Approvals and checks** as environments. This adds
a second layer of protection — even if a pipeline has permission to use the connection,
it must pass the configured checks before the connection credentials are issued to the agent.

1. **Project settings** > **Service connections** > select the connection (e.g., `Nclouse-Prod`)
2. Click **Approvals and checks** (top menu)
3. Click **+** to add checks:

**Recommended checks for production connections (`Nclouse-GOLD`, `Nclouse-Prod`):**

| Check Type | Configuration | Purpose |
|---|---|---|
| **Approvals** | Add release managers as approvers | Human gate before credentials are issued |
| **Branch control** | Allow only `refs/heads/release/*` | Prevents main/feature branches from using production connections |
| **Business hours** | Set allowed deployment windows | Prevents off-hours production deployments |

**Recommended checks for validation connections (`Nclouse-UAT`, `Nclouse-DM`):**

| Check Type | Configuration | Purpose |
|---|---|---|
| **Branch control** | Allow `refs/heads/main` and `refs/heads/release/*` | Prevents feature branches from deploying to shared validation environments |

**How to configure each check:**

**Approvals:**
1. Click **+** > **Approvals**
2. Add approvers (users or groups)
3. Set minimum approvals required
4. Optionally set timeout (default: 30 days)

**Branch control:**
1. Click **+** > **Branch control**
2. Enter allowed branches: `refs/heads/release/*` (for GOLD/PROD) or `refs/heads/main, refs/heads/release/*` (for UAT/DM)
3. Check **Verify branch protection** to also require that the branch has policies enabled

**Business hours:**
1. Click **+** > **Business hours**
2. Set the allowed days and time window (e.g., Mon–Fri, 06:00–18:00 UTC)
3. Deployments queued outside this window will wait until the next allowed period

> **Approvals on connections vs. environments:** Both are valid enforcement points.
> Environment approvals gate the *stage*, while service connection approvals gate
> the *credential*. For defense-in-depth, use both — but if you only configure one,
> prefer connection-level checks since they cannot be bypassed by creating a new
> pipeline that references the same connection.

---

## Key Vault Integration (Optional)

For production use, link variable groups to Azure Key Vault instead of storing secrets
directly in Azure DevOps:

1. Azure DevOps > **Pipelines > Library** > **+ Variable group**
2. Toggle **Link secrets from an Azure key vault as variables**
3. Select your Azure subscription and Key Vault
4. Map secret names to the pipeline variable names (`PP_TENANT_ID`, `PP_APPLICATION_ID`, `PP_CLIENT_SECRET`)

Benefits: centralized secret management, automatic rotation, and audit logging.

---

## Quick Reference

| What You Need | Where to Find It |
|---|---|
| Tenant ID | Entra ID > Overview |
| Application (Client) ID | Entra ID > App registrations > your app |
| Client Secret | Entra ID > App registrations > Certificates & secrets |
| Environment GUID | PPAC URL bar, or `Get-AdminPowerAppEnvironment` |
| Environment URL | PPAC > Environment details |
| Teams Webhook URL | Teams channel > Workflows or Connectors |
