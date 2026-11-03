using System.Text.RegularExpressions;

namespace audit.Objects.PipelineRuns
{
    /// <summary>
    /// Options of the "pipeline-runs" command. Property names match the option names so the
    /// System.CommandLine naming convention binder can fill them.
    /// </summary>
    public class PipelineRunsOptions
    {
        // common audit options (AuditCommandOptions.AddCommonOptions)
        public string? teamProject { get; set; }
        public bool generateReport { get; set; }
        public string? reportOutputFolder { get; set; }
        public bool postEvent { get; set; }
        public string? hostAddress { get; set; }
        public string? tenantName { get; set; }
        public string? clientId { get; set; }
        public string? securityAccessToken { get; set; }
        public bool useSSL { get; set; }

        // pipeline-runs options
        public string? azureDevOpsUrl { get; set; }
        public string? personalAccessToken { get; set; }
        public string? repositories { get; set; }
        public int days { get; set; } = 14;
        public string? since { get; set; }
        public string? pipelineRoles { get; set; }
        public string? pipelineRolePrefixes { get; set; }
        public string? approvalStages { get; set; }
        public string? pools { get; set; }
        public string? identity { get; set; }
        public string? linuxAgentPattern { get; set; }
        public string? include { get; set; }
        public int batchSize { get; set; } = 200;
        public int batchDelayMs { get; set; } = 1000;
        public int parallelism { get; set; } = 8;
        public bool strictCollection { get; set; }
        public bool refreshOpenPullRequests { get; set; }
        public bool refreshChangedWorkItems { get; set; }
        public bool typedEvents { get; set; }
        public bool telemetryEvents { get; set; } = true;
        public string? writeEventDefinitions { get; set; }
        public string? mesVersion { get; set; }
        public string? workItemFields { get; set; }
        public string? teamFromIterationPath { get; set; }
        public string? sprintFromIterationPath { get; set; }
        public bool workItemHistory { get; set; }
        public bool stepErrors { get; set; } = true;
        public string? doneStates { get; set; }
        public string? branchVersions { get; set; }
        public int resolvedAfterHours { get; set; } = 24;
        public string? releaseTitle { get; set; }
        public string? releaseTitleContains { get; set; }
        public string? releaseKinds { get; set; }
        public string? tagPattern { get; set; }
        public string? pathRules { get; set; }
        public string? scopeTypes { get; set; }
        public string? milestoneField { get; set; }
        public string? storyPointsField { get; set; }
        public string? removedStates { get; set; }
        public string? featureType { get; set; }
        public string? epicType { get; set; }
        public string? fieldsConfig { get; set; }
    }

    /// <summary>How people (PR authors, requesters, approvers, test owners) are posted.</summary>
    public enum IdentityMode
    {
        /// <summary>No person information at all.</summary>
        None,
        /// <summary>The Azure DevOps identity id (a stable GUID), so a project can map people to teams itself.</summary>
        Id,
        /// <summary>The Azure DevOps display name.</summary>
        Name,
    }

    /// <summary>Which record types to collect.</summary>
    [Flags]
    public enum PipelineRunsParts
    {
        None = 0,
        Runs = 1,
        Steps = 2,
        Tests = 4,
        Changes = 8,
        PullRequests = 16,
        Approvals = 32,
        AgentJobs = 64,
        /// <summary>Start and Target Date history of the release Feature work items. Not part of All: its event must be deployed first.</summary>
        ReleaseSchedule = 128,
        /// <summary>Version tags of the repositories (release date of each package). Not part of All: its event must be deployed first.</summary>
        ReleaseTags = 256,
        /// <summary>Files changed by each completed pull request (how many are test files). Not part of All: its event must be deployed first.</summary>
        PullRequestFiles = 512,
        /// <summary>Current state, team, estimate and Feature/Epic of every open or recently changed work item of the scope types (User Story and Bug by default), to forecast the remaining scope of an Epic. Not part of All: its event must be deployed first.</summary>
        WorkItemScope = 1024,
        All = Runs | Steps | Tests | Changes | PullRequests | Approvals | AgentJobs,
    }

    /// <summary>
    /// Parsed settings shared by the collector and the mapper.
    /// </summary>
    public class PipelineRunsSettings
    {
        public string Organization { get; init; } = "";
        public string OrganizationUrl { get; init; } = "";
        public string Project { get; init; } = "";
        public List<string> Repositories { get; init; } = new();
        public DateTime Since { get; init; }
        public Dictionary<string, string> RoleOverrides { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Pipeline-name prefix to role, longest prefix first. Default is the CM CLI naming convention; --pipelineRolePrefixes none turns it off.</summary>
        public List<(string Prefix, string Role)> RolePrefixes { get; init; } = ParseRolePrefixes(DefaultRolePrefixes);
        public HashSet<string> ApprovalStages { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Pools { get; init; } = new();
        public IdentityMode Identity { get; init; } = IdentityMode.Id;
        public Regex LinuxAgent { get; init; } = new("(runner|linux|ubuntu)", RegexOptions.IgnoreCase);
        public PipelineRunsParts Parts { get; init; } = PipelineRunsParts.All;
        public bool StrictCollection { get; init; }
        public bool RefreshOpenPullRequests { get; init; }
        /// <summary>Also re-read the pull requests (any age) linked to work items changed since --since, so their flow rows do not go stale.</summary>
        public bool RefreshChangedWorkItems { get; init; }
        public Dictionary<string, string> WorkItemFields { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Regex? TeamFromIterationPath { get; init; }
        public Regex? SprintFromIterationPath { get; init; }
        public bool WorkItemHistory { get; init; }
        /// <summary>Send the error code and the scrubbed first error line of failed steps (ErrorCode tag, ErrorMessage). On by default; --stepErrors false turns it off.</summary>
        public bool StepErrors { get; init; } = true;
        public HashSet<string> DoneStates { get; init; } = new(new[] { "Resolved", "Closed" }, StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> BranchVersions { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public int ResolvedAfterHours { get; init; } = 24;
        /// <summary>Title of a release Feature; the single capture group is its version (V1.3.0 - Hotfix Package Release -> 1.3.0).</summary>
        public Regex ReleaseTitle { get; init; } = new(DefaultReleaseTitle, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        /// <summary>Coarse WIQL filter on the title, applied before ReleaseTitle.</summary>
        public string ReleaseTitleContains { get; init; } = DefaultReleaseTitleContains;
        /// <summary>Ordered (kind, text) rules: the first text found in the latest title of a release Feature gives its ReleaseKind. Empty by default.</summary>
        public List<(string Kind, string Text)> ReleaseKinds { get; init; } = new();
        /// <summary>Name of a version tag. Named group "version" is the package version, optional named group "date" is the release day (yyyyMMdd).</summary>
        public Regex ReleaseTag { get; init; } = new(DefaultReleaseTag, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        /// <summary>Ordered rules (prefix, category) that classify a changed file; the first matching prefix wins (case-insensitive), a file that matches none is "other".</summary>
        public List<(string Prefix, string Category)> PathRules { get; init; } = ParsePathRules(DefaultPathRules);
        /// <summary>Work item types read by --include scope (a Task is part of its story, so tasks are left out by default).</summary>
        public List<string> ScopeTypes { get; init; } = SplitList(DefaultScopeTypes);
        /// <summary>Reference name of the field read as Milestone (empty = none).</summary>
        public string MilestoneField { get; init; } = DefaultMilestoneField;
        /// <summary>Reference name of the estimate field (story points, effort, size).</summary>
        public string StoryPointsField { get; init; } = DefaultStoryPointsField;
        /// <summary>States that mean out of scope: such an item is never done and is flagged Removed.</summary>
        public HashSet<string> RemovedStates { get; init; } = new(SplitList(DefaultRemovedStates), StringComparer.OrdinalIgnoreCase);
        public string FeatureType { get; init; } = DefaultFeatureType;
        public string EpicType { get; init; } = DefaultEpicType;
        public DateTime CollectedAt { get; init; } = DateTime.UtcNow;

        public const PipelineRunsParts MetadataParts = PipelineRunsParts.ReleaseSchedule | PipelineRunsParts.ReleaseTags | PipelineRunsParts.PullRequestFiles | PipelineRunsParts.WorkItemScope;
        public const string DefaultScopeTypes = "User Story,Bug";
        public const string DefaultMilestoneField = "Custom.Milestone";
        public const string DefaultStoryPointsField = "Microsoft.VSTS.Scheduling.StoryPoints";
        public const string DefaultRemovedStates = "Removed";
        public const string DefaultFeatureType = "Feature";
        public const string DefaultEpicType = "Epic";
        public const string DefaultApprovalStages = "";
        public const string DefaultReleaseTitle = @"^\s*V?(\d+\.\d+\.\d+)\b";
        public const string DefaultReleaseTitleContains = "";
        /// <summary>Categories: test-unit, test-business, test-iot, test-ui, test-e2e, test-other, business, iot, masterdata, ui, other.</summary>
        public const string DefaultPathRules =
            "/Cmf.Custom.Business/Cmf.Custom.UnitTests.Biz/=test-unit," +
            "/Cmf.Custom.Tests/Cmf.Custom.Tests.Biz=test-business," +
            "/Cmf.Custom.Tests/Cmf.Custom.Tests.IoT=test-iot," +
            "/Cmf.Custom.Tests/Cmf.CustomPlugin.IoTOrchestrator=test-iot," +
            "/Cmf.Custom.Tests/MasterData/IoTData=test-iot," +
            "/Cmf.Custom.Tests/Cmf.Custom.Tests.GUI=test-ui," +
            "/Cmf.Custom.Tests/Cmf.Custom.Tests.E2E=test-e2e," +
            "/Cmf.Custom.Tests/=test-other," +
            "/Cmf.Custom.Business/=business," +
            "/Cmf.Custom.Data/Cmf.Custom.Data.Packages/DEEs=business," +
            "/Cmf.Custom.Data/=masterdata," +
            "/Cmf.Custom.Database/=masterdata," +
            "/Cmf.Custom.IoT/=iot," +
            "/Cmf.Custom.HTML/=ui";
        public const string DefaultReleaseTag = @"^[A-Za-z][A-Za-z0-9]*-(?<date>\d{8})-(?<version>\d+\.\d+\.\d+)$";

        /// <summary>
        /// Builds the settings from the command-line options.
        /// </summary>
        public static PipelineRunsSettings From(PipelineRunsOptions o, DateTime utcNow)
        {
            if (string.IsNullOrWhiteSpace(o.azureDevOpsUrl))
            {
                throw new ArgumentException("--azureDevOpsUrl is required (e.g. https://dev.azure.com/<organization>/)");
            }
            if (string.IsNullOrWhiteSpace(o.teamProject))
            {
                throw new ArgumentException("--teamProject is required");
            }

            if (!string.IsNullOrWhiteSpace(o.fieldsConfig))
            {
                FieldsConfig.Apply(o, File.ReadAllText(o.fieldsConfig));
            }

            var url = o.azureDevOpsUrl.TrimEnd('/') + "/";
            DateTime since = utcNow.AddDays(-Math.Max(1, o.days));
            if (!string.IsNullOrWhiteSpace(o.since))
            {
                since = DateTime.Parse(o.since, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
            }

            return new PipelineRunsSettings
            {
                OrganizationUrl = url,
                Organization = OrganizationFromUrl(url),
                Project = o.teamProject!,
                Repositories = SplitList(o.repositories),
                Since = since,
                RoleOverrides = ParseRoles(o.pipelineRoles),
                RolePrefixes = ParseRolePrefixes(o.pipelineRolePrefixes),
                ApprovalStages = new HashSet<string>(SplitList(string.IsNullOrWhiteSpace(o.approvalStages) ? DefaultApprovalStages : o.approvalStages).Select(Normalize), StringComparer.OrdinalIgnoreCase),
                Pools = SplitList(o.pools),
                Identity = ParseIdentity(o.identity),
                LinuxAgent = new Regex(string.IsNullOrWhiteSpace(o.linuxAgentPattern) ? "(runner|linux|ubuntu)" : o.linuxAgentPattern, RegexOptions.IgnoreCase),
                Parts = ParseParts(o.include),
                StrictCollection = o.strictCollection,
                RefreshOpenPullRequests = o.refreshOpenPullRequests,
                RefreshChangedWorkItems = o.refreshChangedWorkItems,
                WorkItemFields = ParseRoles(o.workItemFields),
                TeamFromIterationPath = PathPattern(o.teamFromIterationPath),
                SprintFromIterationPath = PathPattern(o.sprintFromIterationPath),
                WorkItemHistory = o.workItemHistory,
                StepErrors = o.stepErrors,
                DoneStates = new HashSet<string>(SplitList(o.doneStates ?? "Resolved,Closed"), StringComparer.OrdinalIgnoreCase),
                BranchVersions = ParseRoles(o.branchVersions),
                ResolvedAfterHours = Math.Max(1, o.resolvedAfterHours),
                ReleaseTitle = ReleasePattern(o.releaseTitle),
                ReleaseTitleContains = string.IsNullOrWhiteSpace(o.releaseTitleContains) ? DefaultReleaseTitleContains : o.releaseTitleContains.Trim(),
                ReleaseKinds = ParseRoles(o.releaseKinds).Select(kv => (kv.Key, kv.Value)).ToList(),
                ReleaseTag = TagRegex(o.tagPattern),
                PathRules = ParsePathRules(string.IsNullOrWhiteSpace(o.pathRules) ? DefaultPathRules : o.pathRules),
                ScopeTypes = SplitList(string.IsNullOrWhiteSpace(o.scopeTypes) ? DefaultScopeTypes : o.scopeTypes),
                MilestoneField = (o.milestoneField ?? DefaultMilestoneField).Trim(),
                StoryPointsField = string.IsNullOrWhiteSpace(o.storyPointsField) ? DefaultStoryPointsField : o.storyPointsField.Trim(),
                RemovedStates = new HashSet<string>(SplitList(string.IsNullOrWhiteSpace(o.removedStates) ? DefaultRemovedStates : o.removedStates), StringComparer.OrdinalIgnoreCase),
                FeatureType = string.IsNullOrWhiteSpace(o.featureType) ? DefaultFeatureType : o.featureType.Trim(),
                EpicType = string.IsNullOrWhiteSpace(o.epicType) ? DefaultEpicType : o.epicType.Trim(),
                CollectedAt = utcNow,
            };
        }

        private static List<(string Prefix, string Category)> ParsePathRules(string rules)
        {
            var list = new List<(string, string)>();
            foreach (var rule in SplitList(rules))
            {
                var i = rule.LastIndexOf('=');
                if (i <= 0 || i == rule.Length - 1) throw new ArgumentException($"Invalid --pathRules entry '{rule}': use prefix=category");
                list.Add((NormalizePath(rule[..i]), rule[(i + 1)..].Trim().ToLowerInvariant()));
            }
            return list;
        }

        private static string NormalizePath(string path)
        {
            var p = path.Trim().Replace('\\', '/');
            if (!p.StartsWith('/')) p = "/" + p;
            return p;
        }

        private static Regex TagRegex(string? pattern)
        {
            var regex = new Regex(string.IsNullOrWhiteSpace(pattern) ? DefaultReleaseTag : pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            if (!regex.GetGroupNames().Contains("version"))
            {
                throw new ArgumentException("--tagPattern must have a named group (?<version>...) and may have (?<date>yyyyMMdd)");
            }
            return regex;
        }

        private static Regex ReleasePattern(string? pattern)
        {
            var regex = new Regex(string.IsNullOrWhiteSpace(pattern) ? DefaultReleaseTitle : pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            if (regex.GetGroupNumbers().Length != 2)
            {
                throw new ArgumentException("--releaseTitle must have exactly one capture group (the version)");
            }
            return regex;
        }

        private static Regex? PathPattern(string? pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return null;
            var regex = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            if (regex.GetGroupNumbers().Length != 2)
            {
                throw new ArgumentException("Iteration path patterns must have exactly one capture group");
            }
            return regex;
        }

        public static List<string> SplitList(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? new List<string>()
                : value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        public const string DefaultRolePrefixes = "PR-=PR,CI-Publish=Publish,CI-=CI,CD-=CD";

        /// <summary>"PR-=PR,CI-=CI" -> prefix rules, longest prefix first. Empty/unset = the CM convention, "none" = no prefix rules.</summary>
        public static List<(string Prefix, string Role)> ParseRolePrefixes(string? value)
        {
            if (string.Equals(value?.Trim(), "none", StringComparison.OrdinalIgnoreCase)) return new();
            return SplitList(string.IsNullOrWhiteSpace(value) ? DefaultRolePrefixes : value)
                .Select(i => i.Split('=', 2, StringSplitOptions.TrimEntries))
                .Where(p => p.Length == 2 && p[0].Length > 0 && p[1].Length > 0)
                .Select(p => (p[0], p[1]))
                .OrderByDescending(p => p.Item1.Length)
                .ToList();
        }

        /// <summary>"PR-Validate=PR,CI-Publish=Publish" -> map of pipeline name to role.</summary>
        public static Dictionary<string, string> ParseRoles(string? value)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in SplitList(value))
            {
                var parts = item.Split('=', 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
                {
                    map[parts[0]] = parts[1];
                }
            }
            return map;
        }

        public static IdentityMode ParseIdentity(string? value) => (value ?? "id").Trim().ToLowerInvariant() switch
        {
            "none" => IdentityMode.None,
            "name" => IdentityMode.Name,
            _ => IdentityMode.Id,
        };

        public static PipelineRunsParts ParseParts(string? value)
        {
            var items = SplitList(value);
            if (items.Count == 0 || items.Any(i => i.Equals("all", StringComparison.OrdinalIgnoreCase)))
            {
                return PipelineRunsParts.All;
            }
            var parts = PipelineRunsParts.None;
            foreach (var i in items)
            {
                parts |= i.ToLowerInvariant() switch
                {
                    "runs" => PipelineRunsParts.Runs,
                    "steps" => PipelineRunsParts.Steps,
                    "tests" => PipelineRunsParts.Tests,
                    "changes" => PipelineRunsParts.Changes,
                    "prs" or "pullrequests" => PipelineRunsParts.PullRequests,
                    "approvals" => PipelineRunsParts.Approvals,
                    "jobs" or "agentjobs" => PipelineRunsParts.AgentJobs,
                    "schedule" or "releaseschedule" => PipelineRunsParts.ReleaseSchedule,
                    "tags" or "releasetags" => PipelineRunsParts.ReleaseTags,
                    "prfiles" or "pullrequestfiles" => PipelineRunsParts.PullRequestFiles,
                    "scope" or "workitemscope" => PipelineRunsParts.WorkItemScope,
                    _ => throw new ArgumentException($"Unknown --include value '{i}'. Use runs, steps, tests, changes, prs, approvals, jobs, schedule, tags, prfiles, scope or all"),
                };
            }
            // schedule and tags alone read only the release Features and tags, no runs
            return (parts & ~MetadataParts) == PipelineRunsParts.None ? parts : parts | PipelineRunsParts.Runs;
        }

        /// <summary>https://dev.azure.com/org/ or https://org.visualstudio.com/ -> org</summary>
        public static string OrganizationFromUrl(string url)
        {
            var uri = new Uri(url);
            if (uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
            {
                return uri.Host.Split('.')[0];
            }
            return uri.AbsolutePath.Trim('/').Split('/')[0];
        }

        /// <summary>Lower-cased letters and digits only, so "Manual Approval" matches "ManualApproval".</summary>
        public static string Normalize(string value) => new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }
}
