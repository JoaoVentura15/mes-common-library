using audit.Services;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace audit.Objects.PipelineRuns
{
    /// <summary>
    /// Maps raw Azure DevOps JSON to PostTelemetry events.
    /// ISA95 levels: Enterprise = organization, Site = project, Facility = repository,
    /// Area = record type, Resource = record id, Flow = pipeline, Step = stage, step or test run.
    /// Times go into Parameter Timestamps as Unix seconds, like the other cmf-audit events.
    /// </summary>
    public static class PipelineRunsMapper
    {
        public const string AreaRun = "PipelineRun";
        public const string AreaStage = "PipelineStage";
        public const string AreaStep = "PipelineStep";
        public const string AreaApproval = "Approval";
        public const string AreaRunChange = "RunChange";
        public const string AreaTestRun = "TestRun";
        public const string AreaTestFailure = "TestFailure";
        public const string AreaPullRequest = "PullRequest";
        public const string AreaAgentJob = "AgentJob";
        public const string AreaPullRequestWorkItem = "PullRequestWorkItem";
        public const string AreaWorkItemFlow = "WorkItemFlow";
        public const string AreaSprint = "Sprint";
        public const string AreaWorkItemHistory = "WorkItemHistory";
        public const string AreaReleaseSchedule = "ReleaseSchedule";
        public const string AreaReleaseTag = "ReleaseTag";
        public const string AreaPullRequestFiles = "PullRequestFiles";
        public const string AreaWorkItemScope = "WorkItemScope";

        public const string OutcomeReleasedClean = "ReleasedClean";
        public const string OutcomeReleasedWithFailures = "ReleasedWithFailures";
        public const string OutcomeNotReleased = "NotReleased";

        private static readonly Regex MergedPr = new(@"Merged PR (\d+)", RegexOptions.IgnoreCase);
        private static readonly Regex PullRef = new(@"^refs/pull/(\d+)/", RegexOptions.IgnoreCase);
        private static readonly Regex PackageLine = new(@"packageId:\s*(\S+)", RegexOptions.IgnoreCase);
        private static readonly Regex VersionLine = new(@"version:\s*(\S+)", RegexOptions.IgnoreCase);
        private static readonly Regex PackageStep = new(@"^(?:Build|Pack) (\S+) @ ", RegexOptions.IgnoreCase);

        private static readonly (Regex pattern, string category)[] StepCategories =
        {
            (new Regex(@"^(Find changed|Check if released|Search for test cases|Detect)", RegexOptions.IgnoreCase), "detect"),
            (new Regex(@"^(Build|Pack) .+ @ ", RegexOptions.IgnoreCase), "build"),
            (new Regex(@"Build and pack changed packages", RegexOptions.IgnoreCase), "build"),
            (new Regex(@"(test)", RegexOptions.IgnoreCase), "test"),
            (new Regex(@"(Publish|reportgenerator|tool restore|coverage)", RegexOptions.IgnoreCase), "publish"),
            (new Regex(@"(Deploy|Install|Upgrade|Restore ?Point|Backup)", RegexOptions.IgnoreCase), "deploy"),
            (new Regex(@"^Clean", RegexOptions.IgnoreCase), "cleanup"),
            (new Regex(@"^(Checkout|Initialize|Install|Use |NuGet|npm|Login|Pull|Download|Set |Prepare)", RegexOptions.IgnoreCase), "setup"),
            (new Regex(@"^(Post-job|Finalize|Stop Containers|Report build status)", RegexOptions.IgnoreCase), "teardown"),
        };

        #region json helpers

        public static JsonElement? Get(JsonElement e, string path)
        {
            var current = e;
            foreach (var part in path.Split('.'))
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out var next) || next.ValueKind == JsonValueKind.Null)
                {
                    return null;
                }
                current = next;
            }
            return current;
        }

        public static string Str(JsonElement e, string path)
        {
            var v = Get(e, path);
            return v == null ? "" : v.Value.ValueKind switch
            {
                JsonValueKind.String => v.Value.GetString() ?? "",
                JsonValueKind.Number => v.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => "",
            };
        }

        /// <summary>Work item field; field names contain dots ("System.AreaPath"), so they are not split as a path.</summary>
        public static string Field(JsonElement workItem, string field) =>
            Get(workItem, "fields") is JsonElement f && f.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        public static long Long(JsonElement e, string path)
        {
            var v = Get(e, path);
            if (v == null) return 0;
            if (v.Value.ValueKind == JsonValueKind.Number)
            {
                if (v.Value.TryGetInt64(out var n)) return n;
                if (v.Value.TryGetDouble(out var d)) return (long)Math.Round(d); // ADO sends durations as doubles (7557.0)
            }
            return long.TryParse(Str(e, path), out var p) ? p : 0;
        }

        public static DateTime? Date(JsonElement e, string path)
        {
            var s = Str(e, path);
            return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) && d.Year > 1900 ? d : null;
        }

        public static long Unix(DateTime d) => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToUnixTimeSeconds();

        public static decimal Seconds(DateTime? from, DateTime? to) =>
            from == null || to == null ? 0 : Math.Max(0, (decimal)Math.Round((to.Value - from.Value).TotalSeconds));

        #endregion

        #region event builders

        private static PostTelemetry NewEvent(PipelineRunsSettings s, string repository, string area, string resource, string? flow = null, string? step = null) => new()
        {
            Enterprise = new Entity(s.Organization),
            Site = new Entity(s.Project),
            Facility = string.IsNullOrEmpty(repository) ? null! : new Entity(repository),
            Area = new Entity(area),
            Resource = new Entity(resource),
            Flow = string.IsNullOrEmpty(flow) ? null : new Entity(flow),
            Step = string.IsNullOrEmpty(step) ? null : new Entity(step),
            Tags = new List<Tag>(),
            Parameters = new List<Data>(),
        };

        private static void Tag(PostTelemetry t, string key, string? value)
        {
            if (!string.IsNullOrEmpty(value)) t.Tags.Add(new Tag(key, value));
        }

        private static void Number(PostTelemetry t, string cls, string name, decimal value, string unit, params DateTime?[] times) =>
            t.Parameters.Add(new Data(cls, name, new List<decimal> { value }, new List<string>(), times.Where(x => x != null).Select(x => Unix(x!.Value)).ToList(), unit));

        private static void Text(PostTelemetry t, string name, string? value)
        {
            if (!string.IsNullOrEmpty(value)) t.Parameters.Add(new Data("Property", name, new List<decimal>(), new List<string> { value }, new List<long>(), ""));
        }

        /// <summary>Person according to the identity mode: nothing, the identity id, or the display name.</summary>
        public static string? Person(JsonElement e, string path, IdentityMode mode)
        {
            var who = Get(e, path);
            if (who == null || mode == IdentityMode.None) return null;
            var value = mode == IdentityMode.Name ? Str(who.Value, "displayName") : Str(who.Value, "id");
            return string.IsNullOrEmpty(value) ? null : value;
        }

        #endregion

        /// <summary>
        /// Role of a pipeline: explicit --pipelineRoles mapping first, then the CM CLI naming conventions
        /// (PR-*, CI-Publish*, CI-*, CD-*), otherwise "Other".
        /// </summary>
        public static string ResolveRole(string pipelineName, PipelineRunsSettings s)
        {
            if (s.RoleOverrides.TryGetValue(pipelineName, out var role)) return role;
            foreach (var (prefix, prefixRole) in s.RolePrefixes)
            {
                if (pipelineName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return prefixRole;
            }
            return "Other";
        }

        public static List<string> Packages(JsonElement build) =>
            PackageLine.Matches(Str(build, "templateParameters.packages")).Select(m => m.Groups[1].Value).ToList();

        public static string Version(JsonElement build)
        {
            var m = VersionLine.Match(Str(build, "templateParameters.packages"));
            return m.Success ? m.Groups[1].Value : "";
        }

        /// <summary>Target environment from the CD/Publish parameters, or from an env/* branch.</summary>
        public static string Environment(JsonElement build)
        {
            foreach (var key in new[] { "templateParameters.Environment", "templateParameters.TargetEnvironment" })
            {
                var v = Str(build, key);
                if (!string.IsNullOrEmpty(v)) return v;
            }
            var branch = Str(build, "sourceBranch");
            return branch.StartsWith("refs/heads/env/", StringComparison.OrdinalIgnoreCase) ? branch.Substring("refs/heads/env/".Length) : "";
        }

        private static Dictionary<string, string> Parameters(JsonElement build)
        {
            try
            {
                var raw = Str(build, "parameters");
                if (string.IsNullOrWhiteSpace(raw)) return new();
                using var doc = JsonDocument.Parse(raw);
                return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText(), StringComparer.OrdinalIgnoreCase);
            }
            catch (JsonException)
            {
                return new();
            }
        }

        public static IEnumerable<JsonElement> Records(JsonElement? timeline) =>
            timeline != null && Get(timeline.Value, "records") is JsonElement r && r.ValueKind == JsonValueKind.Array ? r.EnumerateArray() : Enumerable.Empty<JsonElement>();

        public static string StepCategory(string name, string type)
        {
            if (type.Equals("Stage", StringComparison.OrdinalIgnoreCase)) return "stage";
            if (type.Equals("Job", StringComparison.OrdinalIgnoreCase) || type.Equals("Phase", StringComparison.OrdinalIgnoreCase)) return "job";
            if (type.StartsWith("Checkpoint", StringComparison.OrdinalIgnoreCase)) return "approval";
            foreach (var (pattern, category) in StepCategories)
            {
                if (pattern.IsMatch(name)) return category;
            }
            return "other";
        }

        /// <summary>
        /// Release outcome of a run with approval stages: released when every configured approval stage that ran succeeded;
        /// "with failures" when tests failed. Null when the run has no configured approval stage that ran.
        /// </summary>
        public static string? ReleaseOutcome(JsonElement? timeline, PipelineRunsSettings s, int failedTests)
        {
            var approvals = Records(timeline)
                .Where(r => Str(r, "type").Equals("Stage", StringComparison.OrdinalIgnoreCase))
                .Where(r => s.ApprovalStages.Contains(PipelineRunsSettings.Normalize(Str(r, "name"))) || s.ApprovalStages.Contains(PipelineRunsSettings.Normalize(Str(r, "identifier"))))
                .Where(r => !Str(r, "result").Equals("skipped", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (approvals.Count == 0) return null;
            var released = approvals.All(r => Str(r, "result").StartsWith("succeeded", StringComparison.OrdinalIgnoreCase));
            return !released ? OutcomeNotReleased : failedTests > 0 ? OutcomeReleasedWithFailures : OutcomeReleasedClean;
        }

        /// <summary>One PipelineRun event.</summary>
        public static PostTelemetry MapRun(JsonElement build, JsonElement? timeline, PipelineRunsSettings s, int? parentBuildId = null, int? failedTests = null, (string Id, string Version)? installed = null)
        {
            var pipeline = Str(build, "definition.name");
            var repository = Str(build, "repository.name");
            var t = NewEvent(s, repository, AreaRun, Str(build, "id"), pipeline);
            var p = Parameters(build);
            var pr = PullRef.Match(Str(build, "sourceBranch"));
            var queued = Date(build, "queueTime");
            var started = Date(build, "startTime");
            var finished = Date(build, "finishTime");
            var agents = Records(timeline).Where(r => Str(r, "type") == "Job").Select(r => Str(r, "workerName")).Where(a => a.Length > 0).Distinct().ToList();
            var packages = Packages(build);
            if (packages.Count == 0 && installed != null) packages = new List<string> { installed.Value.Id };
            var role = ResolveRole(pipeline, s);

            Tag(t, "BuildId", Str(build, "id"));
            Tag(t, "BuildNumber", Str(build, "buildNumber"));
            Tag(t, "DefinitionId", Str(build, "definition.id"));
            Tag(t, "Role", role);
            Tag(t, "Reason", Str(build, "reason"));
            Tag(t, "Result", Str(build, "result"));
            Tag(t, "SourceBranch", Str(build, "sourceBranch"));
            Tag(t, "SourceVersion", Str(build, "sourceVersion"));
            Tag(t, "TargetBranch", p.GetValueOrDefault("system.pullRequest.targetBranch"));
            Tag(t, "PullRequestId", pr.Success ? pr.Groups[1].Value : p.GetValueOrDefault("system.pullRequest.pullRequestId"));
            Tag(t, "PullRequestIteration", p.GetValueOrDefault("system.pullRequest.pullRequestIteration"));
            Tag(t, "ParentBuildId", parentBuildId?.ToString(CultureInfo.InvariantCulture));
            Tag(t, "Environment", Environment(build));
            Tag(t, "Version", installed?.Version ?? Version(build));
            Tag(t, "Platform", agents.Count == 0 ? "" : agents.Any(a => s.LinuxAgent.IsMatch(a)) ? "Linux" : "Windows");
            Tag(t, "Agents", string.Join(";", agents));
            Tag(t, "Packages", string.Join(";", packages));
            Tag(t, "RequestedFor", Person(build, "requestedFor", s.Identity));
            Tag(t, "ReleaseOutcome", ReleaseOutcome(timeline, s, failedTests ?? 0));

            Number(t, "Duration", "Duration", Seconds(started, finished), "s", started, finished);
            Number(t, "Duration", "QueueWait", Seconds(queued, started), "s", queued, started);
            // Absent counts are left out so the typed columns are NULL, not 0: no packages found is unknown, no test run is unknown.
            if (packages.Count > 0) Number(t, "Count", "PackageCount", packages.Count, "");
            if (failedTests != null) Number(t, "Count", "FailedTests", failedTests.Value, "");
            return t;
        }

        /// <summary>PipelineStage events (stages) and PipelineStep events (jobs, tasks, checkpoints).</summary>
        public static List<PostTelemetry> MapTimeline(JsonElement build, JsonElement? timeline, PipelineRunsSettings s)
        {
            var records = Records(timeline).ToList();
            var byId = records.Where(r => Str(r, "id").Length > 0).GroupBy(r => Str(r, "id")).ToDictionary(g => g.Key, g => g.First());
            string StageOf(JsonElement r)
            {
                var current = r;
                for (var i = 0; i < 10; i++)
                {
                    if (Str(current, "type") == "Stage") return Str(current, "name");
                    if (!byId.TryGetValue(Str(current, "parentId"), out var parent)) return "";
                    current = parent;
                }
                return "";
            }

            var pipeline = Str(build, "definition.name");
            var repository = Str(build, "repository.name");
            var events = new List<PostTelemetry>();
            foreach (var r in records)
            {
                var type = Str(r, "type");
                if (type is not ("Stage" or "Job" or "Task" or "Checkpoint" or "Checkpoint.Approval")) continue;
                var name = Str(r, "name");
                var started = Date(r, "startTime");
                var finished = Date(r, "finishTime");
                var isStage = type == "Stage";
                var t = NewEvent(s, repository, isStage ? AreaStage : AreaStep, Str(build, "id"), pipeline, name);
                Tag(t, "BuildId", Str(build, "id"));
                Tag(t, "RecordId", Str(r, "id"));
                Tag(t, "ParentId", Str(r, "parentId"));
                Tag(t, "RecordType", type);
                Tag(t, "IsApprovalStage", isStage && (s.ApprovalStages.Contains(PipelineRunsSettings.Normalize(name)) ||
                    s.ApprovalStages.Contains(PipelineRunsSettings.Normalize(Str(r, "identifier")))) ? "1" : "0");
                Tag(t, "Category", StepCategory(name, type));
                Tag(t, "Package", PackageStep.Match(name) is { Success: true } m ? m.Groups[1].Value : "");
                Tag(t, "StageName", isStage ? "" : StageOf(r));
                Tag(t, "Agent", Str(r, "workerName"));
                Tag(t, "Result", Str(r, "result"));
                Tag(t, "State", Str(r, "state"));
                Tag(t, "Attempt", Str(r, "attempt"));
                Tag(t, "Order", Str(r, "order"));
                Number(t, "Duration", "Duration", Seconds(started, finished), "s", started, finished);
                if (Long(r, "errorCount") > 0) Number(t, "Count", "Errors", Long(r, "errorCount"), "");
                if (Long(r, "warningCount") > 0) Number(t, "Count", "Warnings", Long(r, "warningCount"), "");
                if (s.StepErrors && Long(r, "errorCount") > 0)
                {
                    var (code, message) = FirstError(r);
                    Tag(t, "ErrorCode", code);
                    Text(t, "ErrorMessage", message);
                }
                events.Add(t);
            }
            return events;
        }

        /// <summary>First error issue of a timeline record (issues[] with type "error"): the error code (safe by construction) and the scrubbed first line.</summary>
        public static (string Code, string Message) FirstError(JsonElement record)
        {
            if (Get(record, "issues") is not { ValueKind: JsonValueKind.Array } issues) return ("", "");
            foreach (var issue in issues.EnumerateArray())
            {
                if (!string.Equals(Str(issue, "type"), "error", StringComparison.OrdinalIgnoreCase)) continue;
                var raw = Str(issue, "message");
                if (raw.Trim().Length == 0) continue;
                return (StepErrorScrubber.ErrorCode(raw), StepErrorScrubber.Scrub(raw));
            }
            return ("", "");
        }

        /// <summary>Approval stage name for every Checkpoint.Approval record (approval id -> stage name).</summary>
        public static Dictionary<string, string> ApprovalStagesById(JsonElement? timeline)
        {
            var records = Records(timeline).ToList();
            var byId = records.Where(r => Str(r, "id").Length > 0).GroupBy(r => Str(r, "id")).ToDictionary(g => g.Key, g => g.First());
            var result = new Dictionary<string, string>();
            foreach (var r in records.Where(r => Str(r, "type") == "Checkpoint.Approval"))
            {
                var current = r;
                var stage = "";
                for (var i = 0; i < 10 && byId.TryGetValue(Str(current, "parentId"), out var parent); i++)
                {
                    current = parent;
                    if (Str(current, "type") == "Stage") { stage = Str(current, "name"); break; }
                }
                result[Str(r, "id")] = stage;
            }
            return result;
        }

        /// <summary>One Approval event per approval: approver, status, comment and how long it waited.</summary>
        public static PostTelemetry MapApproval(JsonElement build, JsonElement approval, string stageName, PipelineRunsSettings s)
        {
            var steps = Get(approval, "steps") is JsonElement st && st.ValueKind == JsonValueKind.Array ? st.EnumerateArray().ToList() : new List<JsonElement>();
            var decided = steps.FirstOrDefault(x => !string.IsNullOrEmpty(Str(x, "actualApprover.id")));
            var created = Date(approval, "createdOn");
            var modified = Date(approval, "lastModifiedOn");
            var t = NewEvent(s, Str(build, "repository.name"), AreaApproval, Str(build, "id"), Str(build, "definition.name"), stageName);
            Tag(t, "BuildId", Str(build, "id"));
            Tag(t, "ApprovalId", Str(approval, "id"));
            Tag(t, "Status", Str(approval, "status"));
            Tag(t, "Environment", Environment(build));
            if (decided.ValueKind == JsonValueKind.Object)
            {
                Tag(t, "Approver", Person(decided, "actualApprover", s.Identity));
                var comment = Str(decided, "comment");
                Text(t, "Comment", comment.Length > 500 ? comment.Substring(0, 500) : comment);
            }
            Number(t, "Duration", "Wait", Seconds(created, modified), "s", created, modified);
            return t;
        }

        /// <summary>One RunChange event per run: commits as repeated "Commit" tags and merged PRs as "PullRequestId" tags.</summary>
        public static PostTelemetry? MapRunChanges(JsonElement build, List<JsonElement> changes, PipelineRunsSettings s)
        {
            if (changes.Count == 0) return null;
            var t = NewEvent(s, Str(build, "repository.name"), AreaRunChange, Str(build, "id"), Str(build, "definition.name"));
            Tag(t, "BuildId", Str(build, "id"));
            foreach (var c in changes)
            {
                Tag(t, "Commit", Str(c, "id"));
                var m = MergedPr.Match(Str(c, "message"));
                if (m.Success) Tag(t, "PullRequestId", m.Groups[1].Value);
            }
            Number(t, "Count", "Commits", changes.Count, "");
            return t;
        }

        /// <summary>One TestRun event per test run of the build.</summary>
        public static PostTelemetry MapTestRun(JsonElement build, JsonElement run, int failed, PipelineRunsSettings s)
        {
            var started = Date(run, "startedDate");
            var completed = Date(run, "completedDate");
            var t = NewEvent(s, Str(build, "repository.name"), AreaTestRun, Str(build, "id"), Str(build, "definition.name"), Str(run, "name"));
            Tag(t, "BuildId", Str(build, "id"));
            Tag(t, "TestRunId", Str(run, "id"));
            Tag(t, "State", Str(run, "state"));
            Tag(t, "Environment", Environment(build));
            Number(t, "Count", "Total", Long(run, "totalTests"), "");
            Number(t, "Count", "Passed", Long(run, "passedTests"), "");
            Number(t, "Count", "Failed", failed, "");
            Number(t, "Count", "NotExecuted", Long(run, "incompleteTests") + Long(run, "notApplicableTests"), "");
            Number(t, "Duration", "Duration", Seconds(started, completed), "s", started, completed);
            return t;
        }

        /// <summary>One TestFailure event per failed, aborted or timed-out test (Resource = the test).</summary>
        public static PostTelemetry MapTestFailure(JsonElement build, JsonElement run, JsonElement result, PipelineRunsSettings s)
        {
            var test = Str(result, "automatedTestName");
            if (test.Length == 0) test = Str(result, "testCaseTitle");
            var t = NewEvent(s, Str(build, "repository.name"), AreaTestFailure, test, Str(build, "definition.name"), Str(run, "name"));
            var since = Date(result, "failingSince.date");
            var finished = Date(build, "finishTime") ?? Date(run, "completedDate");
            Tag(t, "BuildId", Str(build, "id"));
            Tag(t, "TestRunId", Str(run, "id"));
            Tag(t, "Outcome", Str(result, "outcome"));
            Tag(t, "TestTitle", Str(result, "testCaseTitle"));
            Tag(t, "Container", Str(result, "automatedTestStorage"));
            Tag(t, "Owner", Person(result, "owner", s.Identity));
            Tag(t, "Environment", Environment(build));
            Tag(t, "FailingSinceBuildId", Str(result, "failingSince.build.id"));
            var errorText = Str(result, "errorMessage");
            Tag(t, "ErrorCode", StepErrorScrubber.ErrorCode(errorText));
            Text(t, "Error", StepErrorScrubber.Scrub(errorText));
            if (Get(result, "durationInMs") != null) Number(t, "Duration", "Duration", Math.Round((decimal)Long(result, "durationInMs") / 1000m, 3), "s");
            if (since != null)
            {
                Number(t, "Duration", "FailingFor", Math.Round((decimal)((finished ?? since.Value) - since.Value).TotalDays, 2), "d", since, finished);
            }
            return t;
        }

        /// <summary>One PullRequest event (latest state; reposting updates it).</summary>
        public static PostTelemetry MapPullRequest(JsonElement pr, List<JsonElement> workItems, PipelineRunsSettings s,
            List<JsonElement>? threads = null, List<JsonElement>? iterations = null)
        {
            var created = Date(pr, "creationDate");
            var closed = Date(pr, "closedDate");
            var t = NewEvent(s, Str(pr, "repository.name"), AreaPullRequest, Str(pr, "pullRequestId"));
            Tag(t, "PullRequestId", Str(pr, "pullRequestId"));
            Tag(t, "Status", Str(pr, "status"));
            Tag(t, "MergeStatus", Str(pr, "mergeStatus"));
            Tag(t, "IsDraft", Str(pr, "isDraft"));
            Tag(t, "SourceBranch", Str(pr, "sourceRefName"));
            Tag(t, "TargetBranch", Str(pr, "targetRefName"));
            Tag(t, "MergeCommit", Str(pr, "lastMergeCommit.commitId"));
            Tag(t, "Author", Person(pr, "createdBy", s.Identity));
            Moment(t, "Created", created);
            Moment(t, "Closed", closed);
            foreach (var wi in workItems)
            {
                Tag(t, "WorkItemId", Str(wi, "id"));
                Tag(t, "WorkItemType", Field(wi, "System.WorkItemType"));
                Tag(t, "AreaPath", Field(wi, "System.AreaPath"));
            }
            Text(t, "Title", Str(pr, "title"));
            if (created != null && closed != null)
            {
                Number(t, "Duration", "Open", Seconds(created, closed), "s", created, closed);
            }
            if (Str(pr, "status").Equals("completed", StringComparison.OrdinalIgnoreCase) && closed != null)
            {
                Number(t, "Timestamp", "Merged", Unix(closed.Value), "s", closed);
            }
            if (threads != null)
            {
                var votes = new List<(DateTime time, string reviewer)>();
                var comments = new List<DateTime>();
                foreach (var thread in threads)
                {
                    var threadComments = Array(thread, "comments").Where(c => Str(c, "isDeleted") != "true").ToList();
                    var published = Date(thread, "publishedDate") ?? threadComments.Select(c => Date(c, "publishedDate")).Min();
                    if (published != null && ThreadProperty(thread, "CodeReviewVoteResult") == "10")
                    {
                        var reviewer = ThreadProperty(thread, "CodeReviewVotedByIdentity");
                        if (Get(thread, "identities") is JsonElement identities && identities.TryGetProperty(reviewer, out var identity))
                            reviewer = Str(identity, "id");
                        if (reviewer.Length == 0) reviewer = threadComments.Select(c => Str(c, "author.id")).FirstOrDefault() ?? "";
                        votes.Add((published.Value, reviewer));
                    }
                    comments.AddRange(threadComments.Where(c => Str(c, "commentType").Equals("text", StringComparison.OrdinalIgnoreCase))
                        .Where(c => Str(c, "author.id").Length > 0 && Str(c, "author.id") != Str(pr, "createdBy.id"))
                        .Select(c => Date(c, "publishedDate")).OfType<DateTime>());
                }
                var firstReview = comments.Select(time => (DateTime?)time).Min();
                var firstApproval = votes.Select(v => (DateTime?)v.time).Min();
                var lastApproval = votes.Select(v => (DateTime?)v.time).Max();
                Moment(t, "FirstReview", firstReview);
                Moment(t, "FirstApproval", firstApproval);
                Moment(t, "LastApproval", lastApproval);
                Interval(t, "TimeToFirstReview", created, firstReview);
                Interval(t, "TimeToFirstApproval", created, firstApproval);
                Interval(t, "ApprovalToMerge", firstApproval, Str(pr, "status") == "completed" ? closed : null);
                Number(t, "Count", "Approvers", votes.Where(v => v.reviewer.Length > 0).Select(v => v.reviewer).Distinct().Count(), "");
            }
            if (iterations != null) Number(t, "Count", "Pushes", Math.Max(0, iterations.Count - 1), "");
            return t;
        }

        private static IEnumerable<JsonElement> Array(JsonElement element, string path) =>
            Get(element, path) is JsonElement array && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : Enumerable.Empty<JsonElement>();

        private static string ThreadProperty(JsonElement thread, string key)
        {
            if (Get(thread, "properties") is not JsonElement properties || !properties.TryGetProperty(key, out var property)) return "";
            return Str(property, "$value");
        }

        private static DateTime? FieldDate(JsonElement item, string name) =>
            DateTime.TryParse(Field(item, name), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time) && time.Year > 1900 ? time : null;

        /// <summary>Numeric work item field (ADO sends story points as a JSON number); null when the field is absent or empty.</summary>
        private static decimal? FieldNumber(JsonElement item, string name) =>
            Get(item, "fields") is JsonElement f && f.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n) ? n : null;

        private static void Moment(PostTelemetry telemetry, string name, DateTime? time)
        {
            if (time != null) Number(telemetry, "Timestamp", name, Unix(time.Value), "s", time);
        }

        private static void Interval(PostTelemetry telemetry, string name, DateTime? from, DateTime? to)
        {
            if (from != null && to != null && to >= from) Number(telemetry, "Duration", name, Seconds(from, to), "s", from, to);
        }

        private static string Extract(Regex? pattern, string value) => pattern?.Match(value) is { Success: true } match ? match.Groups[1].Value : "";

        /// <summary>One row per linked PR and work item, without collapsing team or iteration dimensions.</summary>
        public static PostTelemetry MapPullRequestWorkItem(JsonElement pr, JsonElement item, PipelineRunsSettings settings)
        {
            var telemetry = NewEvent(settings, Str(pr, "repository.name"), AreaPullRequestWorkItem, $"{Str(pr, "pullRequestId")}:{Str(item, "id")}");
            Tag(telemetry, "PullRequestId", Str(pr, "pullRequestId"));
            Tag(telemetry, "WorkItemId", Str(item, "id"));
            Tag(telemetry, "WorkItemType", Field(item, "System.WorkItemType"));
            Tag(telemetry, "State", Field(item, "System.State"));
            Tag(telemetry, "AreaPath", Field(item, "System.AreaPath"));
            var path = Field(item, "System.IterationPath");
            Tag(telemetry, "IterationPath", path);
            Tag(telemetry, "Team", Extract(settings.TeamFromIterationPath, path));
            Tag(telemetry, "Sprint", Extract(settings.SprintFromIterationPath, path));
            Tag(telemetry, "TargetBranch", Str(pr, "targetRefName"));
            foreach (var (alias, field) in settings.WorkItemFields) Tag(telemetry, alias, Field(item, field));
            var version = settings.WorkItemFields.TryGetValue("TargetPackageVersion", out var versionField) ? Field(item, versionField) : "";
            var branch = Str(pr, "targetRefName").Replace("refs/heads/", "", StringComparison.Ordinal);
            if (version.Length > 0 && settings.BranchVersions.TryGetValue(branch, out var expected))
                Number(telemetry, "Flag", "VersionMismatch", version == expected || version.StartsWith(expected + ".", StringComparison.Ordinal) ? 0 : 1, "");
            if (settings.WorkItemFields.ContainsKey("TargetPackageVersion"))
                Number(telemetry, "Flag", "DoneWithoutVersion", settings.DoneStates.Contains(Field(item, "System.State")) && version.Length == 0 ? 1 : 0, "");
            Moment(telemetry, "Created", Date(pr, "creationDate"));
            Moment(telemetry, "Merged", Str(pr, "status") == "completed" ? Date(pr, "closedDate") : null);
            return telemetry;
        }

        /// <summary>State transitions and carry-over for a PR/work-item pair; revisions are optional.</summary>
        public static PostTelemetry MapWorkItemFlow(JsonElement pr, JsonElement item, List<JsonElement>? revisions, PipelineRunsSettings settings)
        {
            var telemetry = MapPullRequestWorkItem(pr, item, settings);
            telemetry.Area = new Entity(AreaWorkItemFlow);
            var created = Date(pr, "creationDate");
            var merged = Str(pr, "status") == "completed" ? Date(pr, "closedDate") : null;
            var activated = FieldDate(item, "Microsoft.VSTS.Common.ActivatedDate");
            var done = settings.DoneStates.Contains(Field(item, "System.State"));
            var resolved = done ? FieldDate(item, "Microsoft.VSTS.Common.ResolvedDate") ?? FieldDate(item, "Microsoft.VSTS.Common.ClosedDate") : null;
            var history = revisions?.OrderBy(r => Long(r, "rev")).ToList();
            var currentPoints = FieldNumber(item, settings.StoryPointsField);
            decimal? pointsAtResolve = null;
            if (history is { Count: > 0 })
            {
                var initial = history.LastOrDefault(r => FieldDate(r, "System.ChangedDate") <= created);
                if (initial.ValueKind != JsonValueKind.Object) initial = history[0];
                Tag(telemetry, "InitialIterationPath", Field(initial, "System.IterationPath"));
                var paths = history.Where(r => FieldDate(r, "System.ChangedDate") >= created).Prepend(initial)
                    .Select(r => Field(r, "System.IterationPath")).Where(path => path.Length > 0).ToList();
                var changes = paths.Zip(paths.Skip(1)).Count(pair => pair.First != pair.Second);
                Number(telemetry, "Count", "IterationChanges", changes, "");
                Number(telemetry, "Flag", "CarriedOver", changes > 0 ? 1 : 0, "");
                var previousState = "";
                JsonElement? resolver = null;
                DateTime? stateChanged = null;
                foreach (var revision in history)
                {
                    var state = Field(revision, "System.State");
                    var changed = FieldDate(revision, "System.ChangedDate");
                    if (state != previousState)
                    {
                        stateChanged = changed;
                        if (state.Equals("Active", StringComparison.OrdinalIgnoreCase)) activated = changed;
                        if (settings.DoneStates.Contains(state) && !settings.DoneStates.Contains(previousState))
                        {
                            resolved = changed;
                            pointsAtResolve = FieldNumber(revision, settings.StoryPointsField);
                            resolver = Get(revision, "fields") is JsonElement fields && fields.TryGetProperty("System.ChangedBy", out var person) ? person : null;
                        }
                        else if (!settings.DoneStates.Contains(state)) { resolved = null; resolver = null; pointsAtResolve = null; }
                    }
                    previousState = state;
                }
                Moment(telemetry, "StateChanged", stateChanged);
                if (done && resolver != null) Tag(telemetry, "Resolver", settings.Identity == IdentityMode.None ? null : Str(resolver.Value, settings.Identity == IdentityMode.Name ? "displayName" : "id"));
            }
            if (telemetry.Tags.All(tag => tag.Key != "Resolver") && done && Get(item, "fields") is JsonElement currentFields && currentFields.TryGetProperty("Microsoft.VSTS.Common.ResolvedBy", out var resolvedBy))
                Tag(telemetry, "Resolver", settings.Identity == IdentityMode.None ? null : Str(resolvedBy, settings.Identity == IdentityMode.Name ? "displayName" : "id"));
            // Story points as estimated when the item was resolved (later re-estimates do not rewrite past weeks); current value when that revision had none. Absent stays NULL, never 0.
            var points = done ? pointsAtResolve ?? currentPoints : currentPoints;
            if (points != null) Number(telemetry, "Count", "StoryPoints", points.Value, "");
            Number(telemetry, "Flag", "HistoryAvailable", history is { Count: > 0 } ? 1 : 0, "");
            Moment(telemetry, "Activated", activated);
            Moment(telemetry, "Resolved", done ? resolved : null);
            Interval(telemetry, "ActiveToResolved", activated, done ? resolved : null);
            Interval(telemetry, "MergeToResolved", merged, done ? resolved : null);
            Number(telemetry, "Flag", "MergedNotDone", merged != null && !done && settings.CollectedAt >= merged.Value.AddHours(settings.ResolvedAfterHours) ? 1 : 0, "");
            return telemetry;
        }

        /// <summary>
        /// One event per work item revision that created the item or changed its state or iteration, so the state history is kept
        /// (WorkItemFlow only keeps the derived timestamps). Key: work item and revision, so re-posting is idempotent.
        /// </summary>
        public static List<PostTelemetry> MapWorkItemHistory(JsonElement item, List<JsonElement>? revisions, PipelineRunsSettings settings)
        {
            var events = new List<PostTelemetry>();
            var previousState = "";
            var previousPath = "";
            var previousChanged = (DateTime?)null;
            var first = true;
            foreach (var revision in (revisions ?? new List<JsonElement>()).OrderBy(r => Long(r, "rev")))
            {
                var state = Field(revision, "System.State");
                var path = Field(revision, "System.IterationPath");
                var stateChanged = state != previousState;
                var pathChanged = path != previousPath;
                var changedAt = FieldDate(revision, "System.ChangedDate");
                if (first || stateChanged || pathChanged)
                {
                    var t = NewEvent(settings, "", AreaWorkItemHistory, $"{Str(item, "id")}:{Long(revision, "rev")}");
                    Tag(t, "WorkItemId", Str(item, "id"));
                    Tag(t, "Rev", Long(revision, "rev").ToString(CultureInfo.InvariantCulture));
                    Tag(t, "WorkItemType", Field(revision, "System.WorkItemType") is { Length: > 0 } type ? type : Field(item, "System.WorkItemType"));
                    Tag(t, "State", state);
                    Tag(t, "PreviousState", previousState);
                    Tag(t, "ChangeKind", first ? "Created" : stateChanged && pathChanged ? "StateAndIteration" : stateChanged ? "State" : "Iteration");
                    Tag(t, "AreaPath", Field(revision, "System.AreaPath"));
                    Tag(t, "IterationPath", path);
                    Tag(t, "PreviousIterationPath", previousPath);
                    Tag(t, "Team", Extract(settings.TeamFromIterationPath, path));
                    Tag(t, "Sprint", Extract(settings.SprintFromIterationPath, path));
                    Tag(t, "Reason", Field(revision, "System.Reason"));
                    if (settings.Identity != IdentityMode.None && Get(revision, "fields") is JsonElement fields && fields.TryGetProperty("System.ChangedBy", out var person) && person.ValueKind == JsonValueKind.Object)
                        Tag(t, "ChangedBy", Str(person, settings.Identity == IdentityMode.Name ? "displayName" : "id"));
                    Moment(t, "ChangedAt", changedAt);
                    Interval(t, "InPreviousState", previousChanged, changedAt);
                    events.Add(t);
                }
                if (stateChanged || first) previousChanged = changedAt;
                previousState = state;
                previousPath = path;
                first = false;
            }
            return events;
        }

        /// <summary>Parent work item id (System.Parent), 0 when the item has no parent.</summary>
        public static int ParentId(JsonElement item) =>
            Get(item, "fields") is JsonElement f && f.TryGetProperty("System.Parent", out var p) && p.TryGetInt32(out var id) ? id : 0;

        /// <summary>
        /// One event per work item of the scope types (current state, not history): where it sits (team, sprint, Feature, Epic), its estimate and when it was created and
        /// done. Done = the state is a done state, or Resolved/Closed Date is set; a Removed item is never done. Key: work item, so re-posting updates the row.
        /// </summary>
        public static PostTelemetry? MapWorkItemScope(JsonElement item, JsonElement? feature, JsonElement? epic, PipelineRunsSettings settings)
        {
            var id = Str(item, "id");
            var created = FieldDate(item, "System.CreatedDate");
            if (id.Length == 0 || created == null) return null;
            var state = Field(item, "System.State");
            var removed = settings.RemovedStates.Contains(state);
            var resolved = FieldDate(item, "Microsoft.VSTS.Common.ResolvedDate");
            var closed = FieldDate(item, "Microsoft.VSTS.Common.ClosedDate");
            var done = !removed && (settings.DoneStates.Contains(state) || resolved != null || closed != null);
            var doneAt = done ? resolved ?? closed ?? FieldDate(item, "Microsoft.VSTS.Common.StateChangeDate") : null;
            var path = Field(item, "System.IterationPath");
            var t = NewEvent(settings, "", AreaWorkItemScope, id);
            Tag(t, "WorkItemId", id);
            Tag(t, "WorkItemType", Field(item, "System.WorkItemType"));
            Tag(t, "State", state);
            Tag(t, "Title", Field(item, "System.Title"));
            Tag(t, "AreaPath", Field(item, "System.AreaPath"));
            Tag(t, "IterationPath", path);
            Tag(t, "Team", Extract(settings.TeamFromIterationPath, path));
            Tag(t, "Sprint", Extract(settings.SprintFromIterationPath, path));
            if (settings.MilestoneField.Length > 0) Tag(t, "Milestone", Field(item, settings.MilestoneField));
            var parentId = ParentId(item);
            if (parentId > 0) Tag(t, "ParentId", parentId.ToString(CultureInfo.InvariantCulture));
            if (feature != null)
            {
                Tag(t, "FeatureId", Str(feature.Value, "id"));
                Tag(t, "FeatureTitle", Field(feature.Value, "System.Title"));
            }
            if (epic != null)
            {
                Tag(t, "EpicId", Str(epic.Value, "id"));
                Tag(t, "EpicTitle", Field(epic.Value, "System.Title"));
            }
            Number(t, "Count", "Done", done ? 1 : 0, "");
            Number(t, "Count", "Removed", removed ? 1 : 0, "");
            if (FieldNumber(item, settings.StoryPointsField) is { } points) Number(t, "Count", "StoryPoints", points, "");
            Moment(t, "CreatedAt", created);
            Moment(t, "ResolvedAt", resolved);
            Moment(t, "ClosedAt", closed);
            Moment(t, "DoneAt", doneAt);
            Moment(t, "ChangedAt", FieldDate(item, "System.ChangedDate"));
            return t;
        }

        private const string FieldStartDate = "Microsoft.VSTS.Scheduling.StartDate";
        private const string FieldTargetDate = "Microsoft.VSTS.Scheduling.TargetDate";

        /// <summary>Version (x.y.z) of a release Feature title, empty when the title is not a release.</summary>
        public static string ReleaseVersion(string title, PipelineRunsSettings settings) =>
            settings.ReleaseTitle.Match(title ?? "") is { Success: true } match && match.Groups.Count > 1 ? match.Groups[1].Value : "";

        /// <summary>
        /// Branch that carries a release version: the --branchVersions entry whose x.y matches, otherwise the default convention
        /// (development for x.y.0, release/x.y for patches).
        /// </summary>
        public static string ReleaseLine(string version, PipelineRunsSettings settings)
        {
            var parts = version.Split('.');
            if (parts.Length != 3) return "";
            var majorMinor = $"{parts[0]}.{parts[1]}";
            foreach (var (branch, mapped) in settings.BranchVersions)
            {
                if (string.Equals(mapped, majorMinor, StringComparison.OrdinalIgnoreCase)) return branch;
            }
            return parts[2] == "0" ? "development" : $"release/{majorMinor}";
        }

        /// <summary>
        /// One event per revision of a release Feature that created or changed its Start Date (code freeze) or Target Date (planned release), so every
        /// rescheduling is kept as its own row. Key: work item and revision, so re-posting is idempotent and the history can be rebuilt at any time.
        /// </summary>
        public static List<PostTelemetry> MapReleaseSchedule(int workItemId, List<JsonElement>? revisions, PipelineRunsSettings settings)
        {
            var events = new List<PostTelemetry>();
            var ordered = (revisions ?? new List<JsonElement>()).OrderBy(r => Long(r, "rev")).ToList();
            var latestTitle = ordered.Select(r => Field(r, "System.Title")).LastOrDefault(t => ReleaseVersion(t, settings).Length > 0) ?? "";
            var version = ReleaseVersion(latestTitle, settings);
            if (version.Length == 0) return events; // not a release Feature
            var kind = settings.ReleaseKinds.FirstOrDefault(r => latestTitle.Contains(r.Text, StringComparison.OrdinalIgnoreCase)).Kind ?? "";
            DateTime? previousStart = null;
            DateTime? previousTarget = null;
            var any = false;
            long lastEventRev = 0;

            PostTelemetry Build(JsonElement revision, string changeKind, DateTime? start, DateTime? target, bool startChanged, bool targetChanged)
            {
                var rev = Long(revision, "rev");
                var t = NewEvent(settings, "", AreaReleaseSchedule, $"{workItemId}:{rev}");
                Tag(t, "WorkItemId", workItemId.ToString(CultureInfo.InvariantCulture));
                Tag(t, "Rev", rev.ToString(CultureInfo.InvariantCulture));
                Tag(t, "Title", Field(revision, "System.Title"));
                Tag(t, "State", Field(revision, "System.State"));
                Tag(t, "Version", version);
                Tag(t, "Line", ReleaseLine(version, settings));
                Tag(t, "ReleaseKind", kind);
                if (Get(revision, "fields") is JsonElement fields && fields.TryGetProperty("System.Parent", out var parent) && parent.TryGetInt64(out var parentId))
                    Tag(t, "ParentId", parentId.ToString(CultureInfo.InvariantCulture));
                Tag(t, "ChangeKind", changeKind);
                if (settings.Identity != IdentityMode.None && Get(revision, "fields") is JsonElement changer && changer.TryGetProperty("System.ChangedBy", out var person) && person.ValueKind == JsonValueKind.Object)
                    Tag(t, "ChangedBy", Str(person, settings.Identity == IdentityMode.Name ? "displayName" : "id"));
                Moment(t, "StartDate", start);
                Moment(t, "TargetDate", target);
                Moment(t, "PreviousStartDate", previousStart);
                Moment(t, "PreviousTargetDate", previousTarget);
                Moment(t, "ChangedAt", FieldDate(revision, "System.ChangedDate"));
                if (startChanged && start != null && previousStart != null) Number(t, "Count", "StartShift", (decimal)Math.Round((start.Value - previousStart.Value).TotalDays), "d");
                if (targetChanged && target != null && previousTarget != null) Number(t, "Count", "TargetShift", (decimal)Math.Round((target.Value - previousTarget.Value).TotalDays), "d");
                return t;
            }

            foreach (var revision in ordered)
            {
                var start = FieldDate(revision, FieldStartDate);
                var target = FieldDate(revision, FieldTargetDate);
                var startChanged = start != previousStart;
                var targetChanged = target != previousTarget;
                if (startChanged || targetChanged)
                {
                    var changeKind = !any ? "Set" : start == null && target == null ? "Cleared" : startChanged && targetChanged ? "StartAndTarget" : startChanged ? "Start" : "Target";
                    events.Add(Build(revision, changeKind, start, target, startChanged, targetChanged));
                    any = true;
                    lastEventRev = Long(revision, "rev");
                }
                previousStart = start;
                previousTarget = target;
            }

            // The state of a Feature changes without its dates changing (for example to Resolved once the package is delivered). When the latest revision did not
            // change a date, add a "State" row for it so the latest state is always present. It repeats the current dates and is not a rescheduling.
            if (any && Long(ordered[^1], "rev") != lastEventRev)
                events.Add(Build(ordered[^1], "State", previousStart, previousTarget, false, false));
            return events;
        }

        /// <summary>
        /// One event per version tag. The release day is the date written in the tag name when there is one (it is the day the package was delivered; an annotated
        /// tag can be created later), otherwise the date of the annotated tag, otherwise the push that created the tag, otherwise the date of the tagged commit. PushedAt (when the tag reached the server) is kept too, so a tag created after its release day shows. Key: repository and tag name.
        /// </summary>
        public static PostTelemetry? MapReleaseTag(string repository, JsonElement tagRef, JsonElement? annotated, JsonElement? commit, PipelineRunsSettings settings, DateTime? pushedAt = null)
        {
            var refName = Str(tagRef, "name");
            var name = refName.StartsWith("refs/tags/", StringComparison.OrdinalIgnoreCase) ? refName.Substring(10) : refName;
            var match = settings.ReleaseTag.Match(name);
            if (!match.Success) return null;
            var version = match.Groups["version"].Value;
            if (version.Length == 0) return null;
            DateTime? nameDate = DateTime.TryParseExact(match.Groups["date"].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
            var taggedAt = annotated == null ? null : Date(annotated.Value, "taggedBy.date");
            var commitAt = commit == null ? null : Date(commit.Value, "committer.date");
            var releasedAt = nameDate ?? taggedAt ?? pushedAt ?? commitAt;
            if (releasedAt == null) return null;
            var t = NewEvent(settings, repository, AreaReleaseTag, name);
            Tag(t, "TagName", name);
            Tag(t, "Version", version);
            Tag(t, "Line", ReleaseLine(version, settings));
            Tag(t, "TagKind", annotated != null || Str(tagRef, "peeledObjectId").Length > 0 ? "Annotated" : "Lightweight");
            var commitId = Str(tagRef, "peeledObjectId");
            Tag(t, "CommitId", commitId.Length > 0 ? commitId : Str(tagRef, "objectId"));
            Moment(t, "ReleasedAt", releasedAt);
            Moment(t, "TaggedAt", taggedAt);
            Moment(t, "PushedAt", pushedAt);
            Moment(t, "CommitAt", commitAt);
            return t;
        }

        /// <summary>
        /// One event per completed pull request: how many files it changed and how many of them are test files (under one of the configured test paths).
        /// TestOnly is 1 when it changed files and all of them are test files. Returns null when the pull request has no merge commit or the changes could not be read.
        /// Key: organization, project and pull request id.
        /// </summary>
        public static PostTelemetry? MapPullRequestFiles(JsonElement pr, List<JsonElement>? changes, PipelineRunsSettings settings)
        {
            var mergeCommit = Str(pr, "lastMergeCommit.commitId");
            if (changes == null || mergeCommit.Length == 0) return null;
            var files = changes
                .Where(c => !(c.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object && item.TryGetProperty("isFolder", out var folder) && folder.ValueKind == JsonValueKind.True))
                .Select(c => Str(c, "item.path")).Where(path => path.Length > 0).ToList();
            int Count(string category) => files.Count(p => ClassifyFile(p, settings) == category);
            var testFiles = files.Count(p => ClassifyFile(p, settings).StartsWith("test-"));
            var t = NewEvent(settings, Str(pr, "repository.name"), AreaPullRequestFiles, Str(pr, "pullRequestId"));
            Tag(t, "PullRequestId", Str(pr, "pullRequestId"));
            Tag(t, "MergeCommit", mergeCommit);
            Tag(t, "TargetBranch", Str(pr, "targetRefName"));
            Moment(t, "Closed", Date(pr, "closedDate"));
            Number(t, "Count", "Files", files.Count, "files");
            Number(t, "Count", "TestFiles", testFiles, "files");
            Number(t, "Count", "TestOnly", files.Count > 0 && testFiles == files.Count ? 1 : 0, "");
            Number(t, "Count", "UnitTestFiles", Count("test-unit"), "files");
            Number(t, "Count", "BusinessTestFiles", Count("test-business"), "files");
            Number(t, "Count", "IoTTestFiles", Count("test-iot"), "files");
            Number(t, "Count", "UiTestFiles", Count("test-ui"), "files");
            Number(t, "Count", "E2ETestFiles", Count("test-e2e"), "files");
            Number(t, "Count", "OtherTestFiles", Count("test-other"), "files");
            Number(t, "Count", "BusinessFiles", Count("business"), "files");
            Number(t, "Count", "IoTFiles", Count("iot"), "files");
            Number(t, "Count", "MasterDataFiles", Count("masterdata"), "files");
            Number(t, "Count", "UiFiles", Count("ui"), "files");
            Number(t, "Count", "OtherFiles", Count("other"), "files");
            return t;
        }

        /// <summary>Category of a changed file: the first path rule whose prefix it starts with, else "other".</summary>
        public static string ClassifyFile(string path, PipelineRunsSettings settings)
        {
            foreach (var (prefix, category) in settings.PathRules)
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return category;
            return "other";
        }

        /// <summary>Flatten dated iteration nodes. Sprint Finish is exclusive, after the inclusive ADO finish date.</summary>
        public static List<PostTelemetry> MapSprints(JsonElement root, PipelineRunsSettings settings)
        {
            var events = new List<PostTelemetry>();
            void Visit(JsonElement node, string parent)
            {
                var path = parent.Length == 0 ? Str(node, "name") : parent + "\\" + Str(node, "name");
                var start = Date(node, "attributes.startDate");
                var finish = Date(node, "attributes.finishDate");
                if (start != null && finish != null)
                {
                    var telemetry = NewEvent(settings, "", AreaSprint, path);
                    Tag(telemetry, "IterationPath", path);
                    Tag(telemetry, "Team", Extract(settings.TeamFromIterationPath, path));
                    Tag(telemetry, "Sprint", Extract(settings.SprintFromIterationPath, path));
                    Moment(telemetry, "Start", start.Value.Date);
                    Moment(telemetry, "Finish", finish.Value.Date.AddDays(1));
                    events.Add(telemetry);
                }
                foreach (var child in Array(node, "children")) Visit(child, path);
            }
            Visit(root, "");
            return events;
        }

        /// <summary>One AgentJob event per finished job request (Resource = the agent).</summary>
        public static PostTelemetry MapAgentJob(JsonElement job, string poolName, PipelineRunsSettings s)
        {
            var queued = Date(job, "queueTime");
            var assigned = Date(job, "assignTime");
            var finished = Date(job, "finishTime");
            var agent = Str(job, "reservedAgent.name");
            var t = NewEvent(s, "", AreaAgentJob, agent.Length > 0 ? agent : "unassigned", Str(job, "definition.name"));
            Tag(t, "RequestId", Str(job, "requestId"));
            Tag(t, "BuildId", Str(job, "owner.id"));
            Tag(t, "Pool", poolName);
            Tag(t, "PlanType", Str(job, "planType"));
            Tag(t, "Result", Str(job, "result"));
            Number(t, "Duration", "Wait", Seconds(queued, assigned), "s", queued, assigned);
            Number(t, "Duration", "Busy", Seconds(assigned, finished), "s", assigned, finished);
            return t;
        }
    }
}
