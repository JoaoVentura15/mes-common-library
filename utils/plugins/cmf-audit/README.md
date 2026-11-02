# cmf-audit

Plugin used to audit CM CLI based projects. This repo is split in two parts:

- **Root** (`package.json`) — the build/release orchestrator. Not published; it drives `dotnet publish` for every platform and wraps the `npm/` package's version bump/publish scripts.
- **`npm/`** (`npm/package.json`) — the actual package published as `@criticalmanufacturing/audit`. `postinstall.js` copies the right platform binary from `dist/<rid>` into `node_modules/.bin/...` when a consumer installs it. Its `files` field (`dist`, `postinstall.js`, `run.js`, `utils.js`) is exactly what ends up in the published tarball.

## Prerequisites

- **.NET SDK 8.0.x**.
- **Node.js** (>= 16.7) and **npm**.
- `CriticalManufacturing.CLI.Core` is restored from nuget.org (see the repository's `NuGet.Config`).

## Telemetry and data handling

- **Telemetry is off by default.** Set `cmf_audit_enable_telemetry=1` (and optionally `cmf_audit_enable_extended_telemetry=1`) to opt in.
- **`--postEvent`** sends audit results to the MES system given by `--hostAddress`. For Data packages this includes the full content of master data records. Only use it against systems you are allowed to send that data to, and keep `--useSSL` enabled.
- **Tokens:** prefer environment variables over command-line arguments, which show up in process lists and shell history. Use `SYSTEM_ACCESSTOKEN` for the Azure DevOps pipeline OAuth token or `CMF_AUDIT_ADO_PAT` for a PAT. Set `CMF_AUDIT_SECURITY_TOKEN` to the MES refresh token.
- **Local debugging:** copy `src/Properties/launchSettings.example.json` to `launchSettings.json`, which is git-ignored, and never commit real tokens.

## Auditing a project hosted on GitHub

`review-remote-github` downloads a project folder from GitHub (as a repository archive, extracting only that folder) and audits it with the same auditors as `review`. By default it targets `criticalmanufacturing/mes-common-library` at ref `11.3/dev`:

```powershell
# audit features/<project> (set GITHUB_TOKEN if the repository is private)
cmf-audit review-remote-github --project <project>

# any folder / ref
cmf-audit review-remote-github --path features/<project> --gitRef 11.3/dev --validatorsToRun Data,IoT

# a single package of the project
cmf-audit review-remote-github data --project <project> --packageId Cmf.Custom.Data
```

Subcommands (one per auditor, each runnable on its own): `root`, `data`, `iot`, `pipeline`. `pipeline` also takes `--azureDevOpsUrl` and `--azureDevOpsRepository`, because the generated pipelines embed an Azure DevOps repository URL that has to match the committed ones. Options: `--repository` (owner/name), `--gitRef`, `--project`, `--path` (wins over `--project`), `--githubToken` (prefer the `GITHUB_TOKEN` environment variable), plus the common reporting options.

## Collecting Azure DevOps pipeline runs (`pipeline-runs`)

`pipeline-runs` reads a project's Azure DevOps pipeline history and posts it as `PostTelemetry` events, next to the
audit data: every pipeline run with its stages and steps, approvals, the commits and merged pull requests it included,
test runs and failed tests, pull requests with their linked work items, and agent pool jobs.

ISA95 mapping: Enterprise = organization, Site = project, Facility = repository, Area = record type
(`PipelineRun`, `PipelineStage`, `PipelineStep`, `Approval`, `RunChange`, `TestRun`, `TestFailure`, `PullRequest`,
`AgentJob`), Resource = record id (build id, test name, pull request id, agent name), Flow = pipeline, Step = stage,
step or test run.

```powershell
# token with Build, Code, Test Management, Work Items and Agent Pools: Read (or run inside a pipeline with SYSTEM_ACCESSTOKEN)
$env:CMF_AUDIT_ADO_PAT = "<pat>"

# dry run: last 14 days, CSV and Excel report only
cmf-audit pipeline-runs --azureDevOpsUrl https://dev.azure.com/<organization>/ --teamProject <project> `
  --repositories <repository> --pools <agent pool> --generateReport --reportOutputFolder C:\Temp\pipeline-runs

# post to an MES Data Platform
$env:CMF_AUDIT_SECURITY_TOKEN = "<MES refresh token>"
cmf-audit pipeline-runs --azureDevOpsUrl https://dev.azure.com/<organization>/ --teamProject <project> `
  --repositories <repository> --pools <agent pool> --postEvent --hostAddress <mes host> --tenantName <tenant>
```

Options: `--days` (default 14) or `--since`; `--repositories` (names or ids, empty = all runs of the project);
`--include` (runs, steps, tests, changes, prs, approvals, jobs, all; `scope` and `schedule` are named explicitly and are not part of `all`); `--pools` (names, ids or `all`);
`--pipelineRoles` (exact pipeline name = role, wins over the prefixes); `--pipelineRolePrefixes` (prefix=Role pairs; default is the CM CLI
convention PR-=PR,CI-Publish=Publish,CI-=CI,CD-=CD, `none` turns it off, anything not matched is Other); `--approvalStages` (default none, so no stage counts as an approval until you name them; a run is released when every one that ran
succeeded, "with failures" when tests failed); `--identity` (`id` default, `name` or `none`); `--batchSize` (events
per post, default 200).

For unattended collection, `--strictCollection` fails when requested data cannot be collected, and
`--refreshOpenPullRequests` refreshes all open PRs even when they predate the lookback window. The
lookback still applies to completed runs and recently created or closed PRs. Changes to work items linked
only to older closed PRs are not independently refreshed; use a wider `--since` for a backfill.

### Daily pipeline

`cmf-audit init` generates `Builds/CD-AdoTelemetry.yml` and its collector task. Commit them and register
the YAML as an Azure DevOps pipeline. The default schedule runs at 02:00 UTC on `main` every day; edit
the branch and schedule in the generated file for the project. Install `Cmf.Audit` in the target MES
environment before enabling the schedule.

Set pipeline variables `AuditMesHost` (hostname without scheme) and `AuditMesTenant`, and set the
secret `AuditMesRefreshToken`. Map the pipeline's job token with the required Build, Code, Test
Management and Work Items read permissions (plus Agent Pools: Read when agent jobs are collected). The agent must reach both ADO and MES.
The install task may also need the registry credentials configured for the chosen npm registry.

Set the root pipeline's `CollectorArguments` array to additional command-line arguments, for example
`['--teamFromIterationPath', '<regex>', '--sprintFromIterationPath', '<regex>']`.
Use the same mappings on every run. Do not put credentials in the array. The pipeline posts typed
events only, enables strict collection, open-PR refresh and changed-work-item refresh, and uses the default 14-day overlap.
`--refreshChangedWorkItems` finds the work items changed since the window starts and re-reads the pull requests (of any age) linked to them, so a work item that
moves sprint or closes long after its PR merged does not keep stale values in the flow rows.
By default the pipeline collects `runs,steps,tests,changes,prs,approvals,tags` and leaves agent jobs out. Agent-job telemetry is opt-in:
add `'--pools', 'Pool A'` (names, ids or `all`) to `CollectorArguments` and the pipeline adds `jobs` to the collected parts; this needs the
Agent Pools: Read permission. Passing your own `--include` replaces the default list completely.
An ADO acknowledgement precedes asynchronous Data Platform ingestion; it does not prove that the
datasets already show the posted rows.

Release options (for `--include schedule`): `--releaseTitleContains` is required and names the text that identifies a release Feature; `--releaseTitle` is a regex with one capture group for the version (default: a leading version number); `--releaseKinds` maps a kind to a text found in the title, for example `Hotfix=hotfix,Planned=planned` (default none, so `ReleaseKind` stays empty). The branch (`Line`) of a release version comes from `--branchVersions` (`main=1.6,release/1.3=1.3` puts 1.6.x on `main` and 1.3.x on `release/1.3`); versions without an entry fall back to `development` for x.y.0 and `release/x.y` otherwise.

### Work item scope

`--include scope --typedEvents` posts one `WorkItemScope` row per User Story/Bug (`--scopeTypes`, default `User Story,Bug`):
state, team and sprint (from the iteration path), story points, when it became done, and the Feature and the topmost
Epic above it. Open items and items changed since `--since` are read; re-running updates the rows. A Removed item is
never done. Deploy the `WorkItemScope` event definition first using the `Cmf.Audit.Data` package.

**Team and sprint from the iteration path.** `--teamFromIterationPath` and `--sprintFromIterationPath` (regexes with exactly one capture group) fill the `Team` and `Sprint` columns of every work item event (`WorkItemScope`, `WorkItemFlow`, `PullRequestWorkItem`, `WorkItemHistory`, `Sprint`). A team pattern that requires a sprint node after the team, such as `^[^\\]+\\[^\\]+\\([^\\]+)\\`, leaves `Team` empty for items that sit in the team backlog node without a sprint (for example `Contoso\Product Backlog\Team A`). Use a pattern that also accepts the team node itself, for example `^[^\\]+\\Product Backlog\\([^\\]+)` for `Contoso\Product Backlog\<Team>[\Sprint N]`, and pass the same patterns on every run that posts these events (a run without them overwrites the columns with empty values). After changing a pattern, re-post the events (rows are replaced by key).


## ADO Grafana dashboards

The event definitions ship as MasterData in the `Cmf.Audit.Data` package, which creates the IoT event definitions folder before import.
The typed datasets (`Dataset-ADO*Typed.xml`) ship in the `Cmf.Audit.ExportedObjects` package, `ExportedObjects/2.0.0`, next to the objects of version 1.0.0. Eight dashboards ship in the
`Cmf.Audit.Grafana` package, `2.0.0/dashboards/ADO Data`: Pipeline hot spots, PR flow hot spots, Team progress, Sprint handoff,
Agents and tests, Pipeline investigation, Package release flow and Epic forecast. They read the typed datasets
(`UserDefined.ADOTelemetry.*Typed`) over the CM Data Platform OData datasource, with multi-select filters and Grafana's time
range. Replace the datasource UID in them if it differs from yours.

The Epic forecast dashboard (Monte Carlo forecast of an epic or a set of milestones, per team) works for any number of projects. It reads `ADOEpicForecastTyped`, `ADORemainingWorkTyped`, `ADOReleasesTyped` and, for its filters, `ADOScopeDimensionsTyped`. Pick the Organization and Project (several allowed; each project keeps its own rows, so ids and milestone names of different projects never mix), then the scope: milestones (several allowed), features (several allowed) or an epic id, a target date and the history to learn from. A story that has no Feature is grouped under its Epic (listed as `Epic: ... (no feature)`), or under `(standalone)` when it has no parent either, so it can still be forecast; the table "Open items in scope without a Feature" lists them so their links can be fixed in Azure DevOps.

To collect the data for a project, run the collector with that project's own settings (the generated pipeline does this for the project it lives in; pass the arguments through its `CollectorArguments`):

- `--include scope --typedEvents` with `--since` early enough to cover the history window (default 20 weeks). `--teamFromIterationPath` and `--sprintFromIterationPath` give the team and sprint; the datasets read `Team` as posted, so a project with another backlog layout only needs another regex.
- Fields that differ between projects are options: `--milestoneField` (default `Custom.Milestone`, empty for none), `--storyPointsField` (default `Microsoft.VSTS.Scheduling.StoryPoints`, for example `Microsoft.VSTS.Scheduling.Effort`), `--scopeTypes`, `--doneStates`, `--removedStates` (default `Removed`), `--featureType` and `--epicType` (default `Feature` and `Epic`, the levels above the stories).
- `--include schedule` (with `--releaseTitleContains`, and `--releaseTitle`/`--releaseKinds`/`--branchVersions` to match the project's release Features) for the release panel.

#### Project-specific fields (one place to keep them)

The collector reads a few fields whose names differ from one Azure DevOps project to another. Keep them together in the pipeline's `CollectorArguments` for that project, and record here what each one is for, so the next person knows why it is set. Fill in the right-hand column for your own project; the defaults apply when an option is left out.

| Option | Default | What it is used for | Forecast dashboard behaviour |
|---|---|---|---|
| `--milestoneField` | `Custom.Milestone` | Field that tags an item with a milestone or release target | Filter `Scope = Milestone`; empty means no milestone scope |
| `--storyPointsField` | `Microsoft.VSTS.Scheduling.StoryPoints` | Size of an item (for example `Microsoft.VSTS.Scheduling.Effort`) | `Unit = Story points`; unsized items count as the team average |
| `--featureType` / `--epicType` | `Feature` / `Epic` | Work item types of the levels above the stories | `Scope = Feature` groups by Feature, falls back to the Epic, then to "(standalone)" |
| `--teamFromIterationPath` | none | Regex with one capture group that extracts the team from the iteration path | Per-team throughput and remaining work; items without a team are spread automatically |
| `--sprintFromIterationPath` | none | Regex with one capture group for the sprint | Sprint column in the item tables |
| `--scopeTypes`, `--doneStates`, `--removedStates` | see above | Which types are counted, which states mean done, which mean out of scope | Throughput history and remaining work |
| `--workItemFields` | none | Extra fields copied into the events | Available to custom panels |

Instead of repeating these in every pipeline, declare them once in a JSON file and pass `--fieldsConfig <path>`. Each entry is either the plain value or an object with the value and a note on what it is for (the note is documentation only). A command-line value that differs from the built-in default wins over the file.

```json
{
  "fields": {
    "milestoneField": { "value": "Custom.Milestone", "purpose": "release target an item is planned for (Scope = Milestone)" },
    "storyPointsField": { "value": "Microsoft.VSTS.Scheduling.Effort", "purpose": "size of an item (Unit = Story points)" },
    "teamFromIterationPath": { "value": "^[^\\\\]+\\\\Backlog\\\\([^\\\\]+)", "purpose": "team that owns the item" },
    "featureType": "Feature",
    "epicType": "Epic"
  }
}
```

Settings the file can hold: `milestoneField`, `storyPointsField`, `featureType`, `epicType`, `removedStates`, `doneStates`, `scopeTypes`, `teamFromIterationPath`, `sprintFromIterationPath`, `workItemFields`. An unknown name is rejected. Keep the file next to the pipeline definition.

The capacity assumptions (CapacityPct, HistoryWeeks, SkipLatestWeeks, TrimNewTeams, Trials) are dashboard variables. To give a project its own defaults, open the dashboard with that project and values in the URL (`var-Project=...&var-CapacityPct=80`) and save the link, or save a copy of the dashboard with those defaults.

The shared package contains exports from both MES 11.2 and 11.3. Initialization preserves these exports;
importing the older audit objects into MES 11.2 and importing the full package into MES 11.3 remain unverified.

### Notes and limits

- Qualified follow-up PRs require the same linked WI to match the ending sprint in its current or recorded initial
  iteration and have a usable target package matching the package filter. Target versions are latest WI snapshots;
  historical package assignments and test-fix causation are not established. Unverified links remain inspectable.
- General handoff activity uses unqualified created/merged populations in the same handoff window, not whole-sprint
  totals. The package selector applies only to dedicated qualified follow-up panels, not the activity curve or table.
  PR cohort reconciliation deduplicates by organization, project, repository, sprint, and PR, then separates events
  seen on both sides from creation-only and merge-only records. A merge without a creation event in the window is
  not automatically prior-sprint carryover. This is not a complete open-backlog balance. Team calendar dates are
  available in the evidence section because the same sprint number can have different dates across teams.
- Linked-work completion is not full sprint commitment: WIs without merged PRs are absent. Full planned scope requires
  all planned WIs and their historical sprint-start assignments.
- The team drilldown deduplicates WIs within each initial sprint before calculating completion and move share.
  Initial iteration means assignment around PR creation, not original commitment. Current team ownership can include
  transferred WIs. Only observed merged-PR links with history are included, with a 20,000-link query cap.
  Overview sprint labels omit the full iteration prefix; non-sprint paths display as unassigned.
- Branch/version mapping disagreements are not package-delivery mistakes. The delivery KPI remains unmeasured until
  original commitment history is available; no branch-based mismatch total is presented as a delivery failure count.
- CD approval/retention is not a proven production deployment. Approval-stage non-release is not production change
  failure rate. Production deployment and incident/recovery linkage is required for production DORA metrics.
- Task elapsed seconds do not establish active developer time or flow efficiency. Platform trends mix workloads
  unless pipeline, branch, and scope are controlled; they are not causal platform-effect estimates.

## 1. Install dependencies

From `utils/plugins/cmf-audit/`:

```powershell
npm install
```

This installs the build tooling (`rimraf`, `dotnet-bump`, `standard-version`) used by the `build:*`/`bump:*` scripts.

Then install the publishable package's own dependencies (`debug`, `mkdirp`, `node_modules-path`, `rimraf` — these are what `postinstall.js`/`run.js` need at runtime on a consumer's machine):

```powershell
cd npm
npm install
cd ..
```

This also (re)generates `npm/package-lock.json`, which must be committed alongside `npm/package.json`.

To run the unit tests: `dotnet test tests`.

> `dotnet restore` does not need to be run separately — `dotnet publish` (used below) restores automatically.

## 2. Build the binaries

Clean any previous build output, then cross-compile for all three platforms into `npm/dist/`, which is what actually gets bundled into the tarball (the root's `dist/` from `build:prod` is not part of the npm package):

```powershell
npm run build:clean
npm run build:bundle
```

This runs `dotnet publish` for `win-x64`, `linux-x64` and `osx-x64` into `npm/dist/<rid>/`. To build a single platform instead, use one of:

```powershell
npm run build:bundle:win
npm run build:bundle:linux
npm run build:bundle:osx
```

## 3. Generate the tarball

With `npm/dist/<rid>/` populated, pack the `npm/` folder — this respects the `files` field in `npm/package.json`, so it only includes `dist`, `postinstall.js`, `run.js` and `utils.js`:

```powershell
cd npm
npm pack
cd ..
```

This produces `criticalmanufacturing-audit-<version>.tgz` in `npm/`. Sanity-check its contents before publishing:

```powershell
tar -tzf npm/criticalmanufacturing-audit-*.tgz
```

You should see `package/dist/win-x64/...`, `package/dist/linux-x64/...`, `package/dist/osx-x64/...`, plus `package/postinstall.js`, `package/run.js`, `package/utils.js` and `package/package.json`. If `dist/` is missing or only has one platform, re-run step 2 — a partial `build:bundle` (e.g. only `build:bundle:win`) will produce a tarball that only works on that platform.

## 4. Publishing (optional)

If you also want to publish instead of just producing the tarball locally, bump the version first (`npm run bump:pre` / `bump:patch` / `bump:feature` / `bump:breaking`, which bumps both `npm/package.json` and the `.csproj` version in lockstep), rebuild with `build:bundle`, then:

```powershell
npm run publish       # publishes with the "next" tag
npm run publish:live  # publishes with the "latest" tag
```

Both `cd` into `npm/` and run `npm publish` there, so the same `files` filtering applies as in step 3.
