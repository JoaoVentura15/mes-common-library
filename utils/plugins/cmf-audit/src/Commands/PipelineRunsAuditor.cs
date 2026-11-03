using audit.Objects;
using audit.Objects.PipelineRuns;
using audit.Services;
using Cmf.CLI.Core;
using Cmf.CLI.Core.Attributes;
using Cmf.CLI.Core.Commands;
using Cmf.CLI.Utilities;
using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.IO.Abstractions;
using System.Text;
using AuditUtilities = audit.Objects.Utilities;

namespace Cmf.Cli.Plugin.Audit.Commands
{
    /// <summary>
    /// "pipeline-runs": reads Azure DevOps pipeline runs (with stages, steps, approvals, changes and tests),
    /// pull requests and agent pool jobs, and posts them as PostTelemetry events.
    /// </summary>
    [CmfCommand("pipeline-runs", Description = "Collects Azure DevOps pipeline runs, tests, approvals, pull requests and agent jobs as telemetry")]
    public class PipelineRunsAuditor : BaseCommand
    {
        /// <summary>Environment variable with a PAT, used when SYSTEM_ACCESSTOKEN is not set.</summary>
        public const string ENV_ADO_PAT = "CMF_AUDIT_ADO_PAT";

        private readonly Func<string, string, IAzureDevOpsRestClient>? clientFactory;
        private readonly Func<ClientConfiguration, IRestApiService>? postFactory;

        public PipelineRunsAuditor() : this(new FileSystem())
        {
        }

        public PipelineRunsAuditor(IFileSystem fileSystem,
            Func<string, string, IAzureDevOpsRestClient>? clientFactory = null,
            Func<ClientConfiguration, IRestApiService>? postFactory = null) : base(fileSystem)
        {
            this.clientFactory = clientFactory;
            this.postFactory = postFactory;
        }

        public override void Configure(Command cmd)
        {
            AuditCommandOptions.AddCommonOptions(cmd, generateReportByDefault: false);
            var defaults = AzureDevOpsOptions.GetDefaultValues();

            cmd.AddOption(new Option<string>(
                aliases: new[] { "--azureDevOpsUrl" },
                description: "Azure DevOps organization URL, e.g. https://dev.azure.com/<organization>/. predefined variable: System.TeamFoundationCollectionUri",
                getDefaultValue: () => defaults.azureDevOpsUrl));

            cmd.AddOption(new Option<string>(
                aliases: new[] { "--personalAccessToken" },
                description: $"Azure DevOps token (Build, Code, Test Management, Work Items and Agent Pools: Read). Prefer the SYSTEM_ACCESSTOKEN or {ENV_ADO_PAT} environment variables",
                getDefaultValue: () => ""));

            cmd.AddOption(new Option<string>(
                aliases: new[] { "--repositories" },
                description: "Comma-separated repository names or ids. Empty = every pipeline run of the project"));

            cmd.AddOption(new Option<int>(
                aliases: new[] { "--days" },
                description: "Collect runs that finished in the last N days (Azure DevOps keeps PR runs for about two weeks)",
                getDefaultValue: () => 14));

            cmd.AddOption(new Option<string>(
                aliases: new[] { "--since" },
                description: "Collect runs that finished after this UTC date/time (overrides --days), e.g. 2026-10-01T00:00:00Z"));

            cmd.AddOption(new Option<string>(
                aliases: new[] { "--pipelineRoles" },
                description: "Pipeline=Role pairs, e.g. \"CD-ContainersOnClient=Other\". Default: PR-* = PR, CI-Publish* = Publish, CI-* = CI, CD-* = CD, others = Other"));

            cmd.AddOption(new Option<string>(
                aliases: new[] { "--pipelineRolePrefixes" },
                description: "Pipeline-name prefix=Role pairs used when --pipelineRoles has no entry, e.g. \"Validate-=PR,Build-=CI,Deploy-=CD\". Default: PR-=PR,CI-Publish=Publish,CI-=CI,CD-=CD. Use none to turn the prefix rules off (everything not in --pipelineRoles is Other)"));

            cmd.AddOption(new Option<string>(
                aliases: new[] { "--approvalStages" },
                description: "Comma-separated approval stage names; a run is released when every one that ran succeeded. Default: none, so no stage counts as an approval",
                getDefaultValue: () => PipelineRunsSettings.DefaultApprovalStages));

            cmd.AddOption(new Option<string>(
                aliases: new[] { "--pools" },
                description: "Agent pools (names or ids, or \"all\" for every self-hosted pool) whose finished jobs are collected. Empty = none"));

            cmd.AddOption(new Option<string>(
                aliases: new[] { "--identity" },
                description: "How people are posted: none, id (Azure DevOps identity id, default) or name",
                getDefaultValue: () => "id"));

            cmd.AddOption(new Option<string>(
                aliases: new[] { "--linuxAgentPattern" },
                description: "Regex matching Linux agent names, for the Platform tag",
                getDefaultValue: () => "(runner|linux|ubuntu)"));

            cmd.AddOption(new Option<string>(
                aliases: new[] { "--include" },
                description: "What to collect: runs, steps, tests, changes, prs, approvals, jobs, schedule, tags, prfiles, scope or all (all includes none of schedule, tags, prfiles and scope; use --include schedule,tags alone for only the release Feature date history and the version tags, or --include scope alone for only the work item scope)",
                getDefaultValue: () => "all"));

            cmd.AddOption(new Option<int>(
                aliases: new[] { "--batchSize" },
                description: "Events per PostMultipleIoTEvents call",
                getDefaultValue: () => 200));

            cmd.AddOption(new Option<int>("--parallelism", () => 8, "Azure DevOps requests in flight at once while reading runs and pull requests (lower it if Azure DevOps answers 429)"));
            cmd.AddOption(new Option<bool>("--strictCollection", "Fail if requested collection is incomplete instead of posting partial results"));
            cmd.AddOption(new Option<bool>("--refreshOpenPullRequests", "Refresh all open pull requests, including those created before the lookback window"));
            cmd.AddOption(new Option<bool>("--refreshChangedWorkItems", "Also refresh the pull requests of any age that are linked to work items changed since the lookback window starts"));
            cmd.AddOption(new Option<int>("--batchDelayMs", () => 1000, "Pause between PostMultipleIoTEvents calls in milliseconds; 0 posts back to back (Kafka accepts the events and the housekeeper writes them to ClickHouse afterwards)"));
            cmd.AddOption(new Option<bool>("--typedEvents", "Also post the typed IoT events of the \"ADOTelemetry\" folder (PipelineRun, PipelineStep, Approval, RunChange, TestRun, TestFailure, PullRequest, PullRequestWorkItem, WorkItemFlow, WorkItemHistory, WorkItemScope, ReleaseSchedule, ReleaseTag, PullRequestFiles, Sprint, AgentJob), one ReplacingMergeTree table each. Their event definitions must be deployed first (see --writeEventDefinitions)"));
            cmd.AddOption(new Option<bool>("--telemetryEvents", () => true, "Post the generic PostTelemetry events; false posts only the typed events (with --typedEvents)"));
            cmd.AddOption(new Option<string>("--writeEventDefinitions", "Write the typed event definitions (MasterData JSON and one MES export XML per event, importable from the IoT Event Definition list) to this folder and exit; nothing is collected or posted"));
            cmd.AddOption(new Option<string>("--mesVersion", () => AdoTelemetryEvents.DefaultMesVersion, "MES version of the tenant (for example 11.2.3), used for the assembly versions of the exported event definition XML"));
            cmd.AddOption(new Option<string>("--workItemFields", "Alias=ReferenceName pairs, e.g. TargetPackageVersion=Custom.PackageVersion"));
            cmd.AddOption(new Option<string>("--teamFromIterationPath", "Regex with one capture group extracting the team from System.IterationPath"));
            cmd.AddOption(new Option<string>("--sprintFromIterationPath", "Regex with one capture group extracting the sprint from System.IterationPath"));
            cmd.AddOption(new Option<bool>("--workItemHistory", "Read work item revisions to measure state transitions and sprint carry-over"));
            cmd.AddOption(new Option<bool>("--stepErrors", () => true, "Send the error code and the first error line of failed steps, with URLs, paths, accounts, hosts, IPs, e-mails, secrets and ids scrubbed (max 300 chars); false sends neither"));
            cmd.AddOption(new Option<string>("--doneStates", () => "Resolved,Closed", "Comma-separated work item states considered done"));
            cmd.AddOption(new Option<string>("--branchVersions", "Branch=VersionPrefix pairs, e.g. main=1.6,release/1.3=1.3. Also names the branch (Line) of each release version; without an entry x.y.0 = development and x.y.z = release/x.y"));
            cmd.AddOption(new Option<string>("--releaseTitle", () => PipelineRunsSettings.DefaultReleaseTitle, "Regex with one capture group (the version) matching the title of a release Feature, for --include schedule"));
            cmd.AddOption(new Option<string>("--tagPattern", () => PipelineRunsSettings.DefaultReleaseTag, "Regex of a version tag name with named groups (?<version>) and optionally (?<date>yyyyMMdd) (the release day), for --include tags (needs --repositories)"));
            cmd.AddOption(new Option<string>("--pathRules", () => PipelineRunsSettings.DefaultPathRules, "Comma-separated prefix=category rules that classify the files a pull request changed, for --include prfiles (first matching prefix wins; categories: test-unit, test-business, test-iot, test-ui, test-e2e, test-other, business, iot, masterdata, ui, other; needs --repositories)"));
            cmd.AddOption(new Option<string>("--releaseTitleContains", () => PipelineRunsSettings.DefaultReleaseTitleContains, "Text the title of a release Feature contains (WIQL CONTAINS), for --include schedule"));
            cmd.AddOption(new Option<string>("--releaseKinds", "Kind=text pairs: the first text found in the title of a release Feature gives its ReleaseKind, e.g. Hotfix=hotfix,Planned=planned (default: none)"));
            cmd.AddOption(new Option<string>("--scopeTypes", () => PipelineRunsSettings.DefaultScopeTypes, "Comma-separated work item types read by --include scope (open items and items changed since --since; use --doneStates to say which states are done, for example Resolved,IT Tested,Closed)"));
            cmd.AddOption(new Option<string>("--milestoneField", () => PipelineRunsSettings.DefaultMilestoneField, "Reference name of the work item field read as Milestone by --include scope (empty = no milestone)"));
            cmd.AddOption(new Option<string>("--storyPointsField", () => PipelineRunsSettings.DefaultStoryPointsField, "Reference name of the estimate field (Microsoft.VSTS.Scheduling.StoryPoints, or for example Microsoft.VSTS.Scheduling.Effort / Size)"));
            cmd.AddOption(new Option<string>("--removedStates", () => PipelineRunsSettings.DefaultRemovedStates, "Comma-separated work item states that mean out of scope (never done, never counted), for --include scope"));
            cmd.AddOption(new Option<string>("--featureType", () => PipelineRunsSettings.DefaultFeatureType, "Work item type of the level above stories that --include scope reports as Feature"));
            cmd.AddOption(new Option<string>("--epicType", () => PipelineRunsSettings.DefaultEpicType, "Work item type of the topmost level that --include scope reports as Epic (the forecast root)"));
            cmd.AddOption(new Option<string>("--fieldsConfig", "Path of a JSON file that declares the project-specific fields and settings (milestoneField, storyPointsField, featureType, epicType, teamFromIterationPath, ...) with a note on what each is for. Command-line values that differ from the default win."));
            cmd.AddOption(new Option<int>("--resolvedAfterHours", () => 24, "Elapsed-hour threshold for merged PRs with work items not done"));

            cmd.Handler = CommandHandler.Create<PipelineRunsOptions>(ExecuteAsync);
        }

        public async Task ExecuteAsync(PipelineRunsOptions options)
        {
            if (!string.IsNullOrWhiteSpace(options.writeEventDefinitions))
            {
                this.fileSystem.Directory.CreateDirectory(options.writeEventDefinitions);
                this.fileSystem.File.WriteAllText(this.fileSystem.Path.Combine(options.writeEventDefinitions, $"MasterData-{AdoTelemetryEvents.Folder}-IoTEvents.json"), AdoTelemetryEvents.MasterDataJson());
                foreach (var schema in AdoTelemetryEvents.Schemas)
                {
                    this.fileSystem.File.WriteAllText(this.fileSystem.Path.Combine(options.writeEventDefinitions, $"{schema.Name}.xml"), AdoTelemetryEvents.ExportXml(schema, options.mesVersion));
                }
                Log.Information($"Wrote the event definitions of {AdoTelemetryEvents.Schemas.Count} typed event(s) (MasterData JSON and one export XML each) to {options.writeEventDefinitions}");
                return;
            }

            var settings = PipelineRunsSettings.From(options, DateTime.UtcNow);
            var (token, useBearer) = ResolveAdoToken(options.personalAccessToken,
                Environment.GetEnvironmentVariable(AzureDevOpsOptions.ENV_SYSTEM_ACCESSTOKEN),
                Environment.GetEnvironmentVariable(ENV_ADO_PAT));
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new CliException($"No Azure DevOps token: set SYSTEM_ACCESSTOKEN or {ENV_ADO_PAT}, or pass --personalAccessToken");
            }

            var ado = clientFactory?.Invoke(settings.OrganizationUrl, token) ?? new AzureDevOpsRestClient(settings.OrganizationUrl, token, useBearer: useBearer);
            List<PostTelemetry> events;
            try
            {
                events = await new PipelineRunsCollector(ado, settings, options.parallelism).CollectAsync();
            }
            finally
            {
                (ado as IDisposable)?.Dispose();
            }

            foreach (var group in events.GroupBy(e => e.Area?.Name ?? "").OrderBy(g => g.Key))
            {
                Log.Information($"  {group.Key}: {group.Count()} event(s)");
            }

            if (options.generateReport)
            {
                WriteReport(events, options.reportOutputFolder ?? Path.GetTempPath());
            }

            if (options.postEvent && events.Count > 0 && (options.telemetryEvents || options.typedEvents))
            {
                var tenant = options.tenantName;
                if (string.IsNullOrEmpty(tenant))
                {
                    tenant = CLI.Core.Objects.ExecutionContext.Instance.ProjectConfig?.Tenant
                        ?? throw new CliException("No MES tenant found: pass --tenantName or run from a project with a .project-config.json.");
                }
                var config = new ClientConfiguration(options.hostAddress ?? "localhost", tenant, options.clientId ?? "MES", options.securityAccessToken ?? "", options.useSSL);
                var poster = postFactory?.Invoke(config) ?? new RestApiService(config);
                if (poster is RestApiService rest) rest.BatchDelay = TimeSpan.FromMilliseconds(Math.Max(0, options.batchDelayMs));
                if (options.telemetryEvents)
                {
                    await poster.PostEventsAsync(events, Math.Max(1, options.batchSize));
                    Log.Information($"Posted {events.Count} PostTelemetry event(s) to {options.hostAddress}");
                }
                if (options.typedEvents)
                {
                    var typed = AdoTelemetryEvents.Convert(events);
                    await poster.PostTypedEventsAsync(typed, Math.Max(1, options.batchSize));
                    Log.Information($"Posted {typed.Count} typed IoT event(s) ({string.Join(", ", typed.GroupBy(x => x.Definition.Split('\\').Last()).Select(g => $"{g.Key}={g.Count()}"))}) to {options.hostAddress}");
                }
            }
            else if (!options.postEvent)
            {
                Log.Information("Nothing posted (use --postEvent to send the events to the MES Data Platform)");
            }
        }

        internal static (string Token, bool UseBearer) ResolveAdoToken(string? explicitPat, string? systemToken, string? environmentPat)
        {
            if (!string.IsNullOrWhiteSpace(explicitPat)) return (explicitPat, false);
            if (!string.IsNullOrWhiteSpace(systemToken)) return (systemToken, true);
            return (environmentPat ?? "", false);
        }

        /// <summary>One CSV per Area (tags and parameters as columns), merged into an Excel workbook.</summary>
        private void WriteReport(List<PostTelemetry> events, string outputFolder)
        {
            var root = this.fileSystem.DirectoryInfo.New(outputFolder);
            root.Create();
            var folder = root.CreateSubdirectory("PipelineRuns");
            foreach (var group in events.GroupBy(e => e.Area?.Name ?? "Unknown"))
            {
                var tagKeys = group.SelectMany(e => e.Tags ?? new List<Tag>()).Select(t => t.Key).Distinct().ToList();
                var paramKeys = group.SelectMany(e => e.Parameters ?? new List<Data>()).Select(p => p.Name).Distinct().ToList();
                var header = new[] { "Enterprise", "Site", "Facility", "Resource", "Flow", "Step" }.Concat(tagKeys).Concat(paramKeys).ToList();
                var csv = new StringBuilder();
                csv.AppendLine(string.Join(",", header.Select(Csv)));
                foreach (var e in group)
                {
                    var row = new List<string> { e.Enterprise?.Name ?? "", e.Site?.Name ?? "", e.Facility?.Name ?? "", e.Resource?.Name ?? "", e.Flow?.Name ?? "", e.Step?.Name ?? "" };
                    row.AddRange(tagKeys.Select(k => string.Join(";", (e.Tags ?? new List<Tag>()).Where(t => t.Key == k).Select(t => t.Value))));
                    row.AddRange(paramKeys.Select(k => (e.Parameters ?? new List<Data>()).FirstOrDefault(p => p.Name == k) is Data d
                        ? (d.NumericValues.Count > 0 ? d.NumericValues[0].ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Join(";", d.StringValues))
                        : ""));
                    csv.AppendLine(string.Join(",", row.Select(Csv)));
                }
                this.fileSystem.File.WriteAllText(this.fileSystem.Path.Combine(folder.FullName, $"{group.Key}.csv"), csv.ToString());
            }
            AuditUtilities.MergeDirectoryCSVsIntoExcel(folder, "PipelineRuns.xlsx");
            Log.Information($"Pipeline runs report written to {folder.FullName}");
        }

        private static string Csv(string value) =>
            value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
