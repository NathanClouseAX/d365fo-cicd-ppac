# AAXParameterWorkflow

A Dynamics 365 Finance & Operations X++ module that introduces **versioned system parameter management**. Instead of editing system parameters directly (and losing track of what changed, when, and why), this module lets you create named, versioned snapshots of system parameters, compare them to the current live values, and publish (activate) a version on a scheduled effective date.

**Publisher:** AtomicAX.com  
**Platform:** D365 F&O (X++)  
**Model Layer:** 14  

---

## Problem Statement

In standard D365 F&O, the `SystemParameters` form allows direct edits to global configuration values (language, currency, OData settings, batch settings, etc.). There is no audit trail, no ability to schedule future parameter changes, and no way to preview what a change will look like before applying it.

This module solves that by:

1. **Versioning** - every configuration change is captured as a numbered version (Build.Major.Minor.Revision)
2. **Date-effective activation** - versions have `FromDate`/`ToDate` fields, and the system automatically publishes the correct version based on the current date
3. **Diff/compare** - before activating a version, users can compare its values against the currently live parameters
4. **Locking the live form** - the standard `SystemParameters` form is extended to make most fields read-only, forcing changes through the versioning workflow

---

## Architecture Overview

### Status Lifecycle

Versions follow a simple two-state lifecycle defined by the `AAXParameterStatus` enum:

| Status | Value | Description |
|--------|-------|-------------|
| Draft  | 1     | Version is being prepared; all value fields are editable |
| Active | 2     | Version is live; fields become read-only on the form |

### Data Model

The central table is `AAXSystemParametersVersion`, which holds the version header (name, status, version numbers, effective date range). Each version has a **bundle** of child value tables that mirror the standard parameter tables:

```
AAXSystemParametersVersion (header)
 |
 |-- AAXSystemParametersValues          (mirrors SystemParameters)
 |-- AAXFileUploadConfigurationValues   (mirrors FileUploadConfiguration)
 |-- AAXSystemNotificationParametersValues (mirrors SystemNotificationParameters)
 |-- AAXBatchGlobalValues               (mirrors BatchGlobal)
```

All child tables have a foreign key (`AAXSystemParametersVersion`) back to the header, with cascading deletes. The `SystemParameters` table is extended with an `AAXSystemParametersVersionRecId` field to track which version is currently published.

### Tables

| Table | Type | Purpose |
|-------|------|---------|
| `AAXSystemParametersVersion` | Base data, cross-company | Version header: name, status, version string, FromDate/ToDate |
| `AAXSystemParametersValues` | Parameter, cross-company | Versioned copy of `SystemParameters` fields (language, currency, OData, certificates, etc.) |
| `AAXFileUploadConfigurationValues` | Parameter, cross-company | Versioned copy of `FileUploadConfiguration` (blob link expiration times) |
| `AAXSystemNotificationParametersValues` | Parameter, cross-company | Versioned copy of `SystemNotificationParameters` (threshold settings) |
| `AAXBatchGlobalValues` | System, cross-company | Versioned copy of `BatchGlobal` SingleScheduler entry (reserved capacity level) |
| `AAXtmpParameterCompare` | InMemory (temp) | Holds diff results for the compare dialog |
| `AAXSystemParametersVersionStaging` | Staging | DMF staging table for the data entity |

### Table Extension

| Extension | Purpose |
|-----------|---------|
| `SystemParameters.AAXParameterWorkflow` | Adds `AAXSystemParametersVersionRecId` (Int64) to track the currently published version |

### Classes

| Class | Purpose |
|-------|---------|
| `AAXParameterUtil` | Utility: copies field values from a standard parameter record to a version value record by matching field names via `SysDictTable`/`SysDictField` |
| `AAXParameterActivate` | Action menu item entry point: dispatches activation to the appropriate type-specific class and refreshes the form datasource |
| `AAXParameterActivate_SystemParameters` | Sets `Status = Active` on a given `AAXSystemParametersVersion` record |
| `AAXParameterPublish` | Action menu item entry point: dispatches publishing to the appropriate type-specific class and refreshes the form datasource |
| `AAXParameterPublish_SystemParameters` | Writes all versioned values back into the live parameter tables (`SystemParameters`, `FileUploadConfiguration`, `SystemNotificationParameters`) within a `ttsbegin`/`ttscommit` block |
| `AAXParameterCompare` | Base class for comparison logic: factory method `constuct()` resolves the correct subclass based on table ID; holds a temp table buffer |
| `AAXParameterCompare_SystemParameters` | Subclass: loads the live parameter tables and the version value tables, then calls `accumulateDifferences()` for each pair to populate the temp table |
| `AAXSystemParametersVersion_Form_EH` | Form event handler: shows the PortalUsers tab if the `CDSVirtualEntityFlighting` flight is enabled |
| `AAXSystemParameters_Form_Extension` | Chain-of-command extension on the standard `SystemParameters` form: makes most data sources and fields read-only to enforce versioned editing |
| `SystemParameters_AAXParameterWorkflow_Extension` | Chain-of-command extension on `SystemParameters::find()`: auto-creates a default version if none exists, then auto-publishes the effective version if it differs from what's currently applied |
| `Class1` | Experimental/scratch class for Dataverse integration queries (not part of the core workflow) |

### Forms

| Form | Style | Purpose |
|------|-------|---------|
| `AAXSystemParametersVersion` | SimpleListDetails | Main management form: left-side list of versions, right-side detail panel with tabs for General settings, Batch Global settings, System Notifications, and Portal Users. Action pane has Activate and Compare buttons. All fields lock when a version is Active. |
| `AAXParameterCompare` | DialogReadOnly | Read-only dialog showing a grid of differences between the selected version and the current live parameters (table, field, current value, new value) |

### Data Entity

| Entity | Public Name | Purpose |
|--------|-------------|---------|
| `AAXSystemParametersVersionEntity` | SystemParametersVersion | OData-enabled, DMF-enabled entity exposing the version header for integration scenarios |

### Security Privileges

| Privilege | Access |
|-----------|--------|
| `AAXSystemParametersVersionEntityMaintain` | Full CRUD on the data entity |
| `AAXSystemParametersVersionEntityView` | Read-only access to the data entity |

### Menu Items & Navigation

- **Display menu item** `AAXSystemParametersVersion` - opens the version management form
- **Display menu item** `AAXParameterCompare` - opens the compare dialog
- **Action menu item** `AAXParameterActivate_SystemParameters` - triggers version activation
- **Menu extension** `SystemAdministration.AAXParameterWorkflow` - places the version form under System Administration > Setup, after the standard System Parameters menu item

### Enums & EDTs

| Object | Type | Purpose |
|--------|------|---------|
| `AAXParameterStatus` | Enum | Draft (1), Active (2) |
| `AAXEffectiveAsOfSessionDate` | EDT | Display field showing whether a version is the effective one for the current session date |
| `AAXVersionCreationSequenceNumber` | EDT | Auto-incrementing sequence number for version ordering |

---

## Key Workflows

### Creating a New Version

1. Navigate to **System Administration > Setup > System Parameters Versions**
2. Click **New** - a draft version is created with auto-incremented version numbers
3. The `insert()` override on `AAXSystemParametersVersion` calls `createDefaultBundle()`, which snapshots current values from all four standard parameter tables into the corresponding version value tables
4. Edit the versioned values as needed on the detail tabs
5. Set the `FromDate` to when the version should take effect

### Comparing a Version

1. Select a draft version in the list
2. Click **Compare** in the action pane
3. The `AAXParameterCompare_SystemParameters.GetData()` method loads the four live parameter tables and the four version value tables, iterates every non-system field by name, and inserts differences into the `AAXtmpParameterCompare` temp table
4. The compare dialog displays a grid: Table Label, Field Label, Current Value, New Value

### Activating a Version

1. Select a draft version
2. Click **Activate** - sets the version status to Active
3. On the next call to `SystemParameters::find()` (which happens frequently in the system), the CoC extension checks if the effective version (by date range) differs from the currently published one
4. If different, it automatically calls `AAXParameterPublish::main()` which writes all versioned values back into the live tables in a transaction

### Auto-Publish on `SystemParameters::find()`

The `SystemParameters_AAXParameterWorkflow_Extension.find()` method is a chain-of-command extension that runs every time any code reads `SystemParameters`. It:
1. Ensures a default version exists (calls `CreateDefault()`)
2. Finds the effective version for the current date via `getEffectiveVersionForDate()`
3. If the effective version's RecId differs from `SystemParameters.AAXSystemParametersVersionRecId`, publishes the version values to the live tables

This means version activation is effectively automatic and date-driven once a version is marked Active.

---

## Module Dependencies

ApplicationCommon, ApplicationFoundation, ApplicationPlatform, ApplicationSuite, ApplicationWorkspaces, CDSVirtualEntity, ContactPerson, Currency, Dimensions, Directory, FiscalBooks, InboundTransportationManagement, Ledger, Retail, SourceDocumentationTypes, Tax, UserSecGov

---

## Project Structure

```
AAXParameterWorkflow/
  AxClass/                    # X++ classes
  AxDataEntityView/           # Data entities
  AxEdt/                      # Extended data types
  AxEnum/                     # Enumerations
  AxForm/                     # Forms
  AxMenuExtension/            # Menu extensions
  AxMenuItemAction/           # Action menu items
  AxMenuItemDisplay/          # Display menu items
  AxRuleSet/                  # Best practice rule sets
  AxSecurityPrivilege/        # Security privileges
  AxTable/                    # Tables
  AxTableExtension/           # Table extensions
Descriptor/                   # Model descriptor
Projects/                     # Visual Studio project files
```
