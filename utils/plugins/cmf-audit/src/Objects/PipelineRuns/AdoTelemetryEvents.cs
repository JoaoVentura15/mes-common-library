using audit.Services;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace audit.Objects.PipelineRuns
{
    public enum ColumnType { String, Integer, DateTime }

    /// <summary>One column of a typed IoT event. KeyOrder &gt; 0 marks it IsIndexed with that IndexOrder, which the Data Platform turns into the ORDER BY of the ReplacingMergeTree (the dedupe key).</summary>
    public record EventColumn(string Name, ColumnType Type, string Description, int KeyOrder = 0, Func<PostTelemetry, object?>? Value = null);

    /// <summary>A typed event ready for PostMultipleIoTEvents: the event definition, a stable EventTime (it picks the table partition, so it must not change between posts) and the flat column data.</summary>
    public record TypedEvent(string Definition, DateTime EventTime, Dictionary<string, object?> Data);

    public class EventSchema
    {
        public required string Name { get; init; }
        public required string Description { get; init; }
        public int RetentionDays { get; init; } = 0;
        /// <summary>Days an event row is kept (counted from the time it was written, so re-posting resets it).</summary>
        public int TimeToLiveDays { get; init; } = 0;
        public required IReadOnlyList<EventColumn> Columns { get; init; }
        /// <summary>Collector areas (PostTelemetry Area names) that map to this event.</summary>
        public required string[] Areas { get; init; }
        /// <summary>Stable time of the entity: queue time of a run, start time of a step.</summary>
        public required Func<PostTelemetry, DateTime?> EntityTime { get; init; }

        public string Path => $"\\IoTEventDefinitions\\{AdoTelemetryEvents.Folder}\\{Name}";
    }

    /// <summary>
    /// Typed IoT events for the pipeline telemetry (folder "ADOTelemetry"). The Data Platform creates one ReplacingMergeTree table per event definition:
    /// ORDER BY = the key columns (IsIndexed, by IndexOrder), no version column (the last post wins), no partition on this tenant.
    /// Keys never change between posts and EventTime is derived from the entity, so re-posting history replaces rows instead of adding them.
    /// </summary>
    public static class AdoTelemetryEvents
    {
        public const string Folder = "ADOTelemetry";

        #region extractors

        private static string? TagOf(PostTelemetry e, string key) => e.Tags?.FirstOrDefault(t => t.Key == key)?.Value;
        private static Func<PostTelemetry, object?> Tag(string key) => e => TagOf(e, key) ?? "";
        private static Func<PostTelemetry, object?> TagInt(string key) => e => long.TryParse(TagOf(e, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0L;

        private static Data? Param(PostTelemetry e, string name) => e.Parameters?.FirstOrDefault(p => p.Name == name);
        private static Func<PostTelemetry, object?> Count(string name) => e => Param(e, name)?.NumericValues.FirstOrDefault() is { } v ? (long)Math.Round(v) : 0L;

        /// <summary>Time from a parameter's Unix-second timestamps; only trusted when both ends exist (the mapper omits missing ones).</summary>
        private static DateTime? TimeOf(PostTelemetry e, string name, int index)
        {
            var ts = Param(e, name)?.Timestamps;
            return ts is { Count: 2 } ? DateTimeOffset.FromUnixTimeSeconds(ts[index]).UtcDateTime : null;
        }
        private static Func<PostTelemetry, object?> Time(string name, int index) => e => TimeOf(e, name, index);

        /// <summary>Like Count, but null (column left empty) when the collector did not produce the parameter, so averages do not count it as 0.</summary>
        private static Func<PostTelemetry, object?> CountN(string name) => e => Param(e, name)?.NumericValues.FirstOrDefault() is { } v ? (long)Math.Round(v) : null;

        /// <summary>Unix-second value of a Timestamp parameter (the mapper only adds it when the time exists).</summary>
        private static Func<PostTelemetry, object?> Stamp(string name) => e => Param(e, name)?.NumericValues.FirstOrDefault() is { } v && v > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)v).UtcDateTime : null;
        private static Func<PostTelemetry, object?> Text(string name) => e => Param(e, name)?.StringValues.FirstOrDefault() ?? "";
        /// <summary>Distinct values of a repeated tag, joined with semicolons (query with splitByChar(';', ...)).</summary>
        private static Func<PostTelemetry, object?> Joined(string key) => e => string.Join(";", (e.Tags ?? new List<Tag>()).Where(t => t.Key == key).Select(t => t.Value).Distinct());
        private static DateTime? StampOf(PostTelemetry e, string name) => Stamp(name)(e) as DateTime?;

        private static Func<PostTelemetry, object?> Isa(Func<PostTelemetry, string?> f) => e => f(e) ?? "";

        #endregion

        private static EventColumn Key(string name, ColumnType type, string description, int order, Func<PostTelemetry, object?> value) => new(name, type, description, order, value);
        private static EventColumn S(string name, string description, Func<PostTelemetry, object?>? value = null) => new(name, ColumnType.String, description, 0, value ?? Tag(name));
        private static EventColumn I(string name, string description, Func<PostTelemetry, object?>? value = null) => new(name, ColumnType.Integer, description, 0, value ?? TagInt(name));
        private static EventColumn D(string name, string description, Func<PostTelemetry, object?> value) => new(name, ColumnType.DateTime, description, 0, value);

        public static readonly EventSchema PipelineRun = new()
        {
            Name = "PipelineRun",
            Description = "One Azure DevOps pipeline run (build). Key: Organization, Project, BuildId.",
            Areas = new[] { PipelineRunsMapper.AreaRun },
            EntityTime = e => TimeOf(e, "QueueWait", 0) ?? TimeOf(e, "Duration", 0),
            Columns = new[]
            {
                Key("Organization", ColumnType.String, "Azure DevOps organization.", 1, Isa(e => e.Enterprise?.Name)),
                Key("Project", ColumnType.String, "Azure DevOps project.", 2, Isa(e => e.Site?.Name)),
                Key("BuildId", ColumnType.Integer, "Azure DevOps build id.", 3, TagInt("BuildId")),
                S("Repository", "Repository of the run.", Isa(e => e.Facility?.Name)),
                S("Pipeline", "Pipeline (definition) name.", Isa(e => e.Flow?.Name)),
                S("BuildNumber", "Build number."),
                I("DefinitionId", "Pipeline definition id."),
                S("Role", "PR, CI, Publish, CD or Other."),
                S("Reason", "Build reason (pullRequest, manual, batchedCI, ...)."),
                S("Result", "succeeded, failed, canceled, partiallySucceeded or empty while running."),
                S("SourceBranch", "Source branch or refs/pull/<id>/merge."),
                S("SourceVersion", "Source commit."),
                S("TargetBranch", "Pull request target branch."),
                I("PullRequestId", "Pull request id, 0 when the run has no pull request."),
                I("PullRequestIteration", "Pull request iteration, 0 when none."),
                I("ParentBuildId", "Parent run when this run was queued by another, 0 when none."),
                S("Environment", "Target environment of a CD run."),
                S("Version", "Version built or installed."),
                S("Platform", "Linux or Windows, from the agents that ran the jobs."),
                S("Agents", "Agent names, separated by semicolons."),
                S("Packages", "Packages built or installed, separated by semicolons."),
                S("RequestedFor", "Who requested the run (per the identity mode of the collector)."),
                S("ReleaseOutcome", "ReleasedClean, ReleasedWithFailures or NotReleased (CD runs)."),
                D("QueuedAt", "Time the run was queued (UTC).", Time("QueueWait", 0)),
                D("Started", "Time the run started (UTC).", Time("Duration", 0)),
                D("Finished", "Time the run finished (UTC).", Time("Duration", 1)),
                I("DurationSeconds", "Run duration in seconds.", Count("Duration")),
                I("QueueSeconds", "Seconds waiting in the queue.", Count("QueueWait")),
                I("FailedTests", "Failed tests in the run, empty when the run has no test run.", CountN("FailedTests")),
                I("PackageCount", "Number of packages, empty when none could be determined.", CountN("PackageCount")),
            },
        };

        public static readonly EventSchema PipelineStep = new()
        {
            Name = "PipelineStep",
            Description = "One timeline record of a run: stage, job, task or checkpoint. Key: Organization, Project, BuildId, RecordId.",
            Areas = new[] { PipelineRunsMapper.AreaStage, PipelineRunsMapper.AreaStep },
            EntityTime = e => TimeOf(e, "Duration", 0),
            Columns = new[]
            {
                Key("Organization", ColumnType.String, "Azure DevOps organization.", 1, Isa(e => e.Enterprise?.Name)),
                Key("Project", ColumnType.String, "Azure DevOps project.", 2, Isa(e => e.Site?.Name)),
                Key("BuildId", ColumnType.Integer, "Azure DevOps build id of the run.", 3, TagInt("BuildId")),
                Key("RecordId", ColumnType.String, "Timeline record id.", 4, Tag("RecordId")),
                S("Repository", "Repository of the run.", Isa(e => e.Facility?.Name)),
                S("Pipeline", "Pipeline (definition) name.", Isa(e => e.Flow?.Name)),
                S("StepName", "Name of the stage, job or task.", Isa(e => e.Step?.Name)),
                S("ParentId", "Timeline id of the parent record."),
                S("RecordType", "Stage, Job, Task, Checkpoint or Checkpoint.Approval."),
                I("IsApprovalStage", "1 when this stage matches a configured release approval stage, otherwise 0."),
                S("Category", "detect, build, test, publish, deploy, cleanup, setup or teardown."),
                S("Package", "Package of a Build or Pack step."),
                S("StageName", "Stage the record belongs to."),
                S("Agent", "Agent that ran the record."),
                S("Result", "Result of the record."),
                S("State", "State of the record."),
                I("Attempt", "Attempt number."),
                I("RecordOrder", "Order inside the timeline.", TagInt("Order")),
                D("Started", "Start time (UTC).", Time("Duration", 0)),
                D("Finished", "Finish time (UTC).", Time("Duration", 1)),
                I("DurationSeconds", "Duration in seconds.", Count("Duration")),
                I("Errors", "Error count.", Count("Errors")),
                I("Warnings", "Warning count.", Count("Warnings")),
                S("ErrorCode", "Error code of the first error (empty when none)."),
                S("ErrorMessage", "Scrubbed first line of the first error.", e => Param(e, "ErrorMessage")?.StringValues.FirstOrDefault() ?? ""),
            },
        };


        public static readonly EventSchema Approval = new()
        {
            Name = "Approval",
            Description = "One approval check of a stage. Key: Organization, Project, BuildId, ApprovalId.",
            Areas = new[] { PipelineRunsMapper.AreaApproval },
            EntityTime = e => TimeOf(e, "Wait", 0),
            Columns = new[]
            {
                Key("Organization", ColumnType.String, "Azure DevOps organization.", 1, Isa(e => e.Enterprise?.Name)),
                Key("Project", ColumnType.String, "Azure DevOps project.", 2, Isa(e => e.Site?.Name)),
                Key("BuildId", ColumnType.Integer, "Azure DevOps build id.", 3, TagInt("BuildId")),
                Key("ApprovalId", ColumnType.String, "Approval id.", 4, Tag("ApprovalId")),
                S("Repository", "Repository.", Isa(e => e.Facility?.Name)),
                S("Pipeline", "Pipeline (definition) name.", Isa(e => e.Flow?.Name)),
                S("StageName", "Stage waiting for the approval.", Isa(e => e.Step?.Name)),
                S("Status", "Approval status."),
                S("Environment", "Target environment."),
                S("Approver", "Who decided (per the identity mode)."),
                S("Comment", "Decision comment (up to 500 characters).", Text("Comment")),
                D("Created", "Approval requested (UTC).", Time("Wait", 0)),
                D("Decided", "Approval decided (UTC).", Time("Wait", 1)),
                I("WaitSeconds", "Seconds between request and decision.", Count("Wait")),
            },
        };

        public static readonly EventSchema RunChange = new()
        {
            Name = "RunChange",
            Description = "Commits and merged pull requests built by a run. One row per run. Key: Organization, Project, BuildId.",
            Areas = new[] { PipelineRunsMapper.AreaRunChange },
            EntityTime = e => null, // the run's time is used
            Columns = new[]
            {
                Key("Organization", ColumnType.String, "Azure DevOps organization.", 1, Isa(e => e.Enterprise?.Name)),
                Key("Project", ColumnType.String, "Azure DevOps project.", 2, Isa(e => e.Site?.Name)),
                Key("BuildId", ColumnType.Integer, "Azure DevOps build id.", 3, TagInt("BuildId")),
                S("Repository", "Repository.", Isa(e => e.Facility?.Name)),
                S("Pipeline", "Pipeline (definition) name.", Isa(e => e.Flow?.Name)),
                I("Commits", "Number of commits in the run.", Count("Commits")),
                S("PullRequestIds", "Merged pull request ids found in the commit messages, separated by semicolons.", Joined("PullRequestId")),
                S("CommitIds", "Commit ids, separated by semicolons.", Joined("Commit")),
            },
        };

        public static readonly EventSchema TestRun = new()
        {
            Name = "TestRun",
            Description = "One test run of a build. Key: Organization, Project, BuildId, TestRunId.",
            Areas = new[] { PipelineRunsMapper.AreaTestRun },
            EntityTime = e => TimeOf(e, "Duration", 0),
            Columns = new[]
            {
                Key("Organization", ColumnType.String, "Azure DevOps organization.", 1, Isa(e => e.Enterprise?.Name)),
                Key("Project", ColumnType.String, "Azure DevOps project.", 2, Isa(e => e.Site?.Name)),
                Key("BuildId", ColumnType.Integer, "Azure DevOps build id.", 3, TagInt("BuildId")),
                Key("TestRunId", ColumnType.Integer, "Test run id.", 4, TagInt("TestRunId")),
                S("Repository", "Repository.", Isa(e => e.Facility?.Name)),
                S("Pipeline", "Pipeline (definition) name.", Isa(e => e.Flow?.Name)),
                S("RunName", "Name of the test run.", Isa(e => e.Step?.Name)),
                S("State", "State of the test run."),
                S("Environment", "Target environment."),
                I("Total", "Total tests.", Count("Total")),
                I("Passed", "Passed tests.", Count("Passed")),
                I("Failed", "Failed tests.", Count("Failed")),
                I("NotExecuted", "Incomplete or not applicable tests.", Count("NotExecuted")),
                D("Started", "Start time (UTC).", Time("Duration", 0)),
                D("Completed", "Completion time (UTC).", Time("Duration", 1)),
                I("DurationSeconds", "Duration in seconds.", Count("Duration")),
            },
        };

        public static readonly EventSchema TestFailure = new()
        {
            Name = "TestFailure",
            Description = "One failed, aborted or timed-out test of a build. Key: Organization, Project, BuildId, TestRunId, TestName.",
            Areas = new[] { PipelineRunsMapper.AreaTestFailure },
            EntityTime = e => null, // the run's time is used
            Columns = new[]
            {
                Key("Organization", ColumnType.String, "Azure DevOps organization.", 1, Isa(e => e.Enterprise?.Name)),
                Key("Project", ColumnType.String, "Azure DevOps project.", 2, Isa(e => e.Site?.Name)),
                Key("BuildId", ColumnType.Integer, "Azure DevOps build id.", 3, TagInt("BuildId")),
                Key("TestRunId", ColumnType.Integer, "Test run id.", 4, TagInt("TestRunId")),
                Key("TestName", ColumnType.String, "Automated test name (or title).", 5, Isa(e => e.Resource?.Name)),
                S("Repository", "Repository.", Isa(e => e.Facility?.Name)),
                S("Pipeline", "Pipeline (definition) name.", Isa(e => e.Flow?.Name)),
                S("RunName", "Name of the test run.", Isa(e => e.Step?.Name)),
                S("Outcome", "Failed, Aborted or Timeout."),
                S("TestTitle", "Test case title."),
                S("Container", "Test container (assembly)."),
                S("Owner", "Test owner (per the identity mode)."),
                S("Environment", "Target environment."),
                I("FailingSinceBuildId", "Build id the test started failing in, 0 when unknown."),
                S("ErrorCode", "Error code (empty when none)."),
                S("Error", "Scrubbed error text.", Text("Error")),
                I("DurationMs", "Test duration in milliseconds, empty when Azure DevOps reports none.", e => Param(e, "Duration")?.NumericValues.FirstOrDefault() is { } v ? (long)Math.Round(v * 1000) : null),
                D("FailingSince", "Time the test started failing (UTC).", Time("FailingFor", 0)),
                D("FailingUntil", "Time of the run (UTC).", Time("FailingFor", 1)),
            },
        };

        public static readonly EventSchema PullRequest = new()
        {
            Name = "PullRequest",
            Description = "One pull request, latest state. Key: Organization, Project, PullRequestId.",
            Areas = new[] { PipelineRunsMapper.AreaPullRequest },
            EntityTime = e => StampOf(e, "Created"),
            Columns = new[]
            {
                Key("Organization", ColumnType.String, "Azure DevOps organization.", 1, Isa(e => e.Enterprise?.Name)),
                Key("Project", ColumnType.String, "Azure DevOps project.", 2, Isa(e => e.Site?.Name)),
                Key("PullRequestId", ColumnType.Integer, "Pull request id.", 3, TagInt("PullRequestId")),
                S("Repository", "Repository.", Isa(e => e.Facility?.Name)),
                S("Title", "Pull request title.", Text("Title")),
                S("Status", "active, completed or abandoned."),
                S("MergeStatus", "Merge status."),
                S("IsDraft", "true or false."),
                S("SourceBranch", "Source branch ref."),
                S("TargetBranch", "Target branch ref."),
                S("MergeCommit", "Merge commit id."),
                S("Author", "Author (per the identity mode)."),
                S("WorkItemIds", "Linked work item ids, separated by semicolons.", Joined("WorkItemId")),
                S("WorkItemTypes", "Linked work item types, separated by semicolons.", Joined("WorkItemType")),
                S("AreaPaths", "Linked work item area paths, separated by semicolons.", Joined("AreaPath")),
                D("Created", "Created (UTC).", Stamp("Created")),
                D("Closed", "Closed (UTC).", Stamp("Closed")),
                D("Merged", "Merged (UTC), completed pull requests only.", Stamp("Merged")),
                D("FirstReview", "First review comment by someone else (UTC).", Stamp("FirstReview")),
                D("FirstApproval", "First approval vote (UTC).", Stamp("FirstApproval")),
                D("LastApproval", "Last approval vote (UTC).", Stamp("LastApproval")),
                I("OpenSeconds", "Seconds from creation to closing.", CountN("Open")),
                I("TimeToFirstReviewSeconds", "Seconds from creation to the first review.", CountN("TimeToFirstReview")),
                I("TimeToFirstApprovalSeconds", "Seconds from creation to the first approval.", CountN("TimeToFirstApproval")),
                I("ApprovalToMergeSeconds", "Seconds from the first approval to the merge.", CountN("ApprovalToMerge")),
                I("Approvers", "Distinct approvers.", CountN("Approvers")),
                I("Pushes", "Pushes after the first iteration.", CountN("Pushes")),
            },
        };

        private static IEnumerable<EventColumn> WorkItemColumns() => new[]
        {
                Key("Organization", ColumnType.String, "Azure DevOps organization.", 1, Isa(e => e.Enterprise?.Name)),
                Key("Project", ColumnType.String, "Azure DevOps project.", 2, Isa(e => e.Site?.Name)),
            Key("PullRequestId", ColumnType.Integer, "Pull request id.", 3, TagInt("PullRequestId")),
            Key("WorkItemId", ColumnType.Integer, "Work item id.", 4, TagInt("WorkItemId")),
            S("Repository", "Repository of the pull request.", Isa(e => e.Facility?.Name)),
            S("WorkItemType", "Work item type."),
            S("State", "Work item state."),
            S("AreaPath", "Area path."),
            S("IterationPath", "Iteration path."),
            S("Team", "Team (from the iteration path)."),
            S("Sprint", "Sprint (from the iteration path)."),
            S("TargetBranch", "Pull request target branch ref."),
            S("TargetPackageVersion", "Value of the TargetPackageVersion work item field alias (empty when not collected)."),
            I("VersionMismatch", "1 when the target version does not match the branch version.", CountN("VersionMismatch")),
            I("DoneWithoutVersion", "1 when the work item is done without a target version.", CountN("DoneWithoutVersion")),
            D("PullRequestCreated", "Pull request created (UTC).", Stamp("Created")),
            D("PullRequestMerged", "Pull request merged (UTC).", Stamp("Merged")),
        };

        public static readonly EventSchema PullRequestWorkItem = new()
        {
            Name = "PullRequestWorkItem",
            Description = "One row per pull request and linked work item. Key: Organization, Project, PullRequestId, WorkItemId.",
            Areas = new[] { PipelineRunsMapper.AreaPullRequestWorkItem },
            EntityTime = e => StampOf(e, "Created"),
            Columns = WorkItemColumns().ToArray(),
        };

        public static readonly EventSchema WorkItemFlow = new()
        {
            Name = "WorkItemFlow",
            Description = "State transitions and carry-over of the work item of a pull request. Key: Organization, Project, PullRequestId, WorkItemId.",
            Areas = new[] { PipelineRunsMapper.AreaWorkItemFlow },
            EntityTime = e => StampOf(e, "Created"),
            Columns = WorkItemColumns().Concat(new[]
            {
                S("InitialIterationPath", "Iteration path when the pull request was created."),
                S("Resolver", "Who resolved the work item (per the identity mode)."),
                I("StoryPoints", "Story points of the work item (as estimated when resolved, else current); NULL when not estimated. Repeats on every PR row of the same work item, so count it once per work item (see StoryPointsCounted in the data set). Whole points only.", CountN("StoryPoints")),
                I("IterationChanges", "Iteration changes since the pull request was created.", CountN("IterationChanges")),
                I("CarriedOver", "1 when the work item moved to another iteration.", CountN("CarriedOver")),
                I("HistoryAvailable", "1 when the revision history was read.", Count("HistoryAvailable")),
                I("MergedNotDone", "1 when merged but not done after the grace period.", Count("MergedNotDone")),
                D("StateChanged", "Last state change (UTC).", Stamp("StateChanged")),
                D("Activated", "Activated (UTC).", Stamp("Activated")),
                D("Resolved", "Resolved (UTC).", Stamp("Resolved")),
                I("ActiveToResolvedSeconds", "Seconds from activation to resolution.", CountN("ActiveToResolved")),
                I("MergeToResolvedSeconds", "Seconds from merge to resolution.", CountN("MergeToResolved")),
            }).ToArray(),
        };

        public static readonly EventSchema WorkItemHistory = new()
        {
            Name = "WorkItemHistory",
            Description = "One row per work item revision that created the item or changed its state or iteration (the state history). Key: Organization, Project, WorkItemId, Rev.",
            Areas = new[] { PipelineRunsMapper.AreaWorkItemHistory },
            EntityTime = e => StampOf(e, "ChangedAt"),
            Columns = new[]
            {
                Key("Organization", ColumnType.String, "Azure DevOps organization.", 1, Isa(e => e.Enterprise?.Name)),
                Key("Project", ColumnType.String, "Azure DevOps project.", 2, Isa(e => e.Site?.Name)),
                Key("WorkItemId", ColumnType.Integer, "Work item id.", 3, TagInt("WorkItemId")),
                Key("Rev", ColumnType.Integer, "Work item revision number.", 4, TagInt("Rev")),
                S("WorkItemType", "Work item type."),
                S("ChangeKind", "Created, State, Iteration or StateAndIteration."),
                S("State", "State after the change."),
                S("PreviousState", "State before the change (empty on creation)."),
                S("IterationPath", "Iteration path after the change."),
                S("PreviousIterationPath", "Iteration path before the change (empty on creation)."),
                S("AreaPath", "Area path."),
                S("Team", "Team (from the iteration path)."),
                S("Sprint", "Sprint (from the iteration path)."),
                S("Reason", "Reason given for the change."),
                S("ChangedBy", "Who changed it (per the identity mode)."),
                D("ChangedAt", "Time of the change (UTC).", Stamp("ChangedAt")),
                I("InPreviousStateSeconds", "Seconds the work item spent in the previous state.", CountN("InPreviousState")),
            },
        };

        public static readonly EventSchema WorkItemScope = new()
        {
            Name = "WorkItemScope",
            Description = "One row per work item of the scope types (User Story and Bug by default): current state, team, estimate and the Feature and Epic above it, to forecast the remaining scope and the throughput of an Epic. Open items and items changed since the run's --since are posted; re-posting updates the row. Key: Organization, Project, WorkItemId.",
            Areas = new[] { PipelineRunsMapper.AreaWorkItemScope },
            EntityTime = e => StampOf(e, "CreatedAt"),
            Columns = new[]
            {
                Key("Organization", ColumnType.String, "Azure DevOps organization.", 1, Isa(e => e.Enterprise?.Name)),
                Key("Project", ColumnType.String, "Azure DevOps project.", 2, Isa(e => e.Site?.Name)),
                Key("WorkItemId", ColumnType.Integer, "Work item id.", 3, TagInt("WorkItemId")),
                S("WorkItemType", "Work item type."),
                S("State", "Current state."),
                S("Title", "Title."),
                S("AreaPath", "Area path."),
                S("IterationPath", "Iteration path."),
                S("Team", "Team (from the iteration path); empty when the item is not in a team iteration yet."),
                S("Sprint", "Sprint (from the iteration path)."),
                S("Milestone", "Milestone field of the item (for example Alpha); empty when not set. Can hold several values."),
                I("ParentId", "Parent work item id, 0 when none."),
                I("FeatureId", "Nearest Feature above the item, 0 when none."),
                S("FeatureTitle", "Title of that Feature."),
                I("EpicId", "Topmost Epic above the item (the root of nested Epics), 0 when none."),
                S("EpicTitle", "Title of that Epic."),
                I("Done", "1 when the item is done (a done state, or Resolved/Closed Date set); a Removed item is never done.", Count("Done")),
                I("Removed", "1 when the state is Removed (out of scope).", Count("Removed")),
                I("StoryPoints", "Story points; NULL when not estimated. Whole points only.", CountN("StoryPoints")),
                D("CreatedAt", "Created (UTC).", Stamp("CreatedAt")),
                D("ResolvedAt", "Resolved Date (UTC); empty when not resolved.", Stamp("ResolvedAt")),
                D("ClosedAt", "Closed Date (UTC); empty when not closed.", Stamp("ClosedAt")),
                D("DoneAt", "When the item became done (Resolved Date, else Closed Date, else the last state change); empty when not done.", Stamp("DoneAt")),
                D("ChangedAt", "Last change (UTC).", Stamp("ChangedAt")),
            },
        };

        public static readonly EventSchema ReleaseSchedule = new()
        {
            Name = "ReleaseSchedule",
            Description = "One row per revision of a release Feature that set or changed its Start Date (code freeze) or Target Date (planned release), so every rescheduling is kept. Key: Organization, Project, WorkItemId, Rev.",
            Areas = new[] { PipelineRunsMapper.AreaReleaseSchedule },
            EntityTime = e => StampOf(e, "ChangedAt"),
            Columns = new[]
            {
                Key("Organization", ColumnType.String, "Azure DevOps organization.", 1, Isa(e => e.Enterprise?.Name)),
                Key("Project", ColumnType.String, "Azure DevOps project.", 2, Isa(e => e.Site?.Name)),
                Key("WorkItemId", ColumnType.Integer, "Release Feature work item id.", 3, TagInt("WorkItemId")),
                Key("Rev", ColumnType.Integer, "Work item revision number.", 4, TagInt("Rev")),
                S("Title", "Work item title at the revision."),
                S("State", "Work item state at the revision."),
                S("Version", "Package version of the release, from the latest title (for example 1.2.3)."),
                S("Line", "Branch that carries the release: from --branchVersions, otherwise development for x.y.0 and release/x.y for patch releases."),
                S("ReleaseKind", "Roadmap or GoLive, from the latest title."),
                I("ParentId", "Parent (Epic) work item id, 0 when none."),
                S("ChangeKind", "Set (first dates), Start, Target, StartAndTarget, Cleared or State (the latest revision when only the state changed; the dates repeat the current ones)."),
                D("StartDate", "Start Date after the change = code freeze (UTC); empty when cleared.", Stamp("StartDate")),
                D("TargetDate", "Target Date after the change = planned release (UTC); empty when cleared.", Stamp("TargetDate")),
                D("PreviousStartDate", "Start Date before the change; empty on the first row.", Stamp("PreviousStartDate")),
                D("PreviousTargetDate", "Target Date before the change; empty on the first row.", Stamp("PreviousTargetDate")),
                I("StartShiftDays", "Days the Start Date moved (negative = earlier); empty when not comparable.", CountN("StartShift")),
                I("TargetShiftDays", "Days the Target Date moved (negative = earlier); empty when not comparable.", CountN("TargetShift")),
                S("ChangedBy", "Who changed it (per the identity mode)."),
                D("ChangedAt", "Time of the change (UTC).", Stamp("ChangedAt")),
            },
        };

        public static readonly EventSchema ReleaseTag = new()
        {
            Name = "ReleaseTag",
            Description = "One row per version tag of a repository, i.e. one delivered package. ReleasedAt is the release day (the date in the tag name, else the annotated tag date, else the push date, else the commit date). Key: Organization, Project, Repository, TagName.",
            Areas = new[] { PipelineRunsMapper.AreaReleaseTag },
            EntityTime = e => StampOf(e, "ReleasedAt"),
            Columns = new[]
            {
                Key("Organization", ColumnType.String, "Azure DevOps organization.", 1, Isa(e => e.Enterprise?.Name)),
                Key("Project", ColumnType.String, "Azure DevOps project.", 2, Isa(e => e.Site?.Name)),
                Key("Repository", ColumnType.String, "Repository holding the tag.", 3, Isa(e => e.Facility?.Name)),
                Key("TagName", ColumnType.String, "Tag name (for example Release-20260506-2.47.0).", 4, Tag("TagName")),
                S("Version", "Package version taken from the tag name (for example 2.47.0)."),
                S("Line", "Branch that carries the release: from --branchVersions, otherwise development for x.y.0 and release/x.y for patch releases."),
                S("TagKind", "Annotated or Lightweight."),
                S("CommitId", "Commit the tag points to."),
                D("ReleasedAt", "Release day (UTC): date in the tag name, else annotated tag date, else push date, else commit date.", Stamp("ReleasedAt")),
                D("TaggedAt", "When the annotated tag was created (UTC); empty for lightweight tags.", Stamp("TaggedAt")),
                D("PushedAt", "When the tag was pushed to the server, i.e. created in Azure DevOps (UTC); the latest push of the tag; empty when unknown.", Stamp("PushedAt")),
                D("CommitAt", "Commit date of the tagged commit (UTC).", Stamp("CommitAt")),
            },
        };

        public static readonly EventSchema PullRequestFiles = new()
        {
            Name = "PullRequestFiles",
            Description = "One row per completed pull request: how many files it changed (against the first parent of its merge commit) and how many of them are test files and which kind (unit, business, iot, ui, e2e, other), and how many belong to Business logic (services, DEEs), IoT, MasterData, UI or other code. The path rules decide (see --pathRules). TestOnly is 1 when every changed file is a test file. Key: Organization, Project, PullRequestId.",
            Areas = new[] { PipelineRunsMapper.AreaPullRequestFiles },
            EntityTime = e => StampOf(e, "Closed"),
            Columns = new[]
            {
                Key("Organization", ColumnType.String, "Azure DevOps organization.", 1, Isa(e => e.Enterprise?.Name)),
                Key("Project", ColumnType.String, "Azure DevOps project.", 2, Isa(e => e.Site?.Name)),
                Key("PullRequestId", ColumnType.Integer, "Pull request id.", 3, TagInt("PullRequestId")),
                S("Repository", "Repository of the pull request.", Isa(e => e.Facility?.Name)),
                S("MergeCommit", "Merge (or squash) commit the changes were read from."),
                S("TargetBranch", "Target branch of the pull request."),
                D("Closed", "When the pull request was completed (UTC).", Stamp("Closed")),
                I("Files", "Files changed (folders not counted).", Count("Files")),
                I("TestFiles", "Changed test files (all kinds).", Count("TestFiles")),
                I("UnitTestFiles", "Changed unit test files.", Count("UnitTestFiles")),
                I("BusinessTestFiles", "Changed business (integration) test files.", Count("BusinessTestFiles")),
                I("IoTTestFiles", "Changed IoT test files.", Count("IoTTestFiles")),
                I("UiTestFiles", "Changed UI (GUI, page objects) test files.", Count("UiTestFiles")),
                I("E2ETestFiles", "Changed end-to-end (full scenario) test files.", Count("E2ETestFiles")),
                I("OtherTestFiles", "Changed test files of no specific kind.", Count("OtherTestFiles")),
                I("BusinessFiles", "Changed business logic files (services, orchestration, DEEs).", Count("BusinessFiles")),
                I("IoTFiles", "Changed IoT files.", Count("IoTFiles")),
                I("MasterDataFiles", "Changed MasterData files (data package, database scripts).", Count("MasterDataFiles")),
                I("UiFiles", "Changed UI (HTML) files.", Count("UiFiles")),
                I("OtherFiles", "Changed files that match no rule (pipelines, config, docs).", Count("OtherFiles")),
                I("TestOnly", "1 when the pull request changed files and all of them are test files, else 0.", Count("TestOnly")),
            },
        };

        public static readonly EventSchema Sprint = new()
        {
            Name = "Sprint",
            Description = "One dated iteration node. Key: Organization, Project, IterationPath.",
            Areas = new[] { PipelineRunsMapper.AreaSprint },
            EntityTime = e => StampOf(e, "Start"),
            Columns = new[]
            {
                Key("Organization", ColumnType.String, "Azure DevOps organization.", 1, Isa(e => e.Enterprise?.Name)),
                Key("Project", ColumnType.String, "Azure DevOps project.", 2, Isa(e => e.Site?.Name)),
                Key("IterationPath", ColumnType.String, "Iteration path.", 3, Tag("IterationPath")),
                S("Team", "Team (from the iteration path)."),
                S("Sprint", "Sprint (from the iteration path)."),
                D("Start", "Sprint start (UTC).", Stamp("Start")),
                D("Finish", "Sprint finish, exclusive (UTC).", Stamp("Finish")),
            },
        };

        public static readonly EventSchema AgentJob = new()
        {
            Name = "AgentJob",
            Description = "One finished job request of an agent pool. Key: Organization, Project, RequestId, Pool.",
            Areas = new[] { PipelineRunsMapper.AreaAgentJob },
            EntityTime = e => TimeOf(e, "Wait", 0),
            Columns = new[]
            {
                Key("Organization", ColumnType.String, "Azure DevOps organization.", 1, Isa(e => e.Enterprise?.Name)),
                Key("Project", ColumnType.String, "Azure DevOps project.", 2, Isa(e => e.Site?.Name)),
                Key("RequestId", ColumnType.Integer, "Job request id (monotonic: a lower bound on it prunes granules, like BuildId).", 3, TagInt("RequestId")),
                Key("Pool", ColumnType.String, "Agent pool.", 4, Tag("Pool")),
                S("Agent", "Agent that ran the job (unassigned when none).", Isa(e => e.Resource?.Name)),
                S("Pipeline", "Pipeline (definition) name.", Isa(e => e.Flow?.Name)),
                I("BuildId", "Owner build id."),
                S("PlanType", "Plan type (Build, Release, ...)."),
                S("Result", "Job result."),
                D("Queued", "Queued (UTC).", Time("Wait", 0)),
                D("Assigned", "Assigned to the agent (UTC).", Time("Wait", 1)),
                D("Finished", "Finished (UTC).", Time("Busy", 1)),
                I("WaitSeconds", "Seconds waiting for an agent.", Count("Wait")),
                I("BusySeconds", "Seconds running on the agent.", Count("Busy")),
            },
        };

        public static readonly IReadOnlyList<EventSchema> Schemas = new[]
        {
            PipelineRun, PipelineStep, Approval, RunChange, TestRun, TestFailure, PullRequest, PullRequestWorkItem, WorkItemFlow, WorkItemHistory, WorkItemScope, ReleaseSchedule, ReleaseTag, PullRequestFiles, Sprint, AgentJob,
        };

        /// <summary>Converts the collector events of the supported areas; other areas are left to PostTelemetry.</summary>
        public static List<TypedEvent> Convert(IEnumerable<PostTelemetry> events)
        {
            var list = events.ToList();
            // Entity time of each run, for records that have no time of their own (pending steps)
            var runTime = new Dictionary<string, DateTime>();
            foreach (var e in list.Where(e => e.Area?.Name == PipelineRunsMapper.AreaRun))
            {
                if (PipelineRun.EntityTime(e) is { } t && TagOf(e, "BuildId") is { } id) runTime[id] = t;
            }

            var result = new List<TypedEvent>();
            foreach (var e in list)
            {
                var schema = Schemas.FirstOrDefault(s => s.Areas.Contains(e.Area?.Name ?? ""));
                if (schema == null) continue;
                var time = schema.EntityTime(e)
                    ?? (TagOf(e, "BuildId") is { } id && runTime.TryGetValue(id, out var rt) ? rt : DateTime.UtcNow.Date);
                var data = new Dictionary<string, object?>();
                foreach (var column in schema.Columns)
                {
                    var value = column.Value!(e);
                    if (value == null) continue; // nullable column left out
                    data[column.Name] = column.Type switch
                    {
                        ColumnType.DateTime => DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc),
                        ColumnType.Integer => System.Convert.ToInt64(value, CultureInfo.InvariantCulture),
                        _ => value.ToString() ?? "",
                    };
                }
                result.Add(new TypedEvent(schema.Path, DateTime.SpecifyKind(time, DateTimeKind.Utc), data));
            }
            return result;
        }


        #region MES export XML (Import button of the IoT Event Definition list)

        // Assembly versions as in the Product repo (Platform/DataPlatform/Implementation/Assets/EventDefinition): major.minor.0.0 of the MES version
        private const string Mes = "Cmf.Foundation.BusinessObjects";
        public const string DefaultMesVersion = "11.2";
        [ThreadStatic] private static string? mesVersion;
        private static string MesVersion => $"Version={mesVersion ?? DefaultMesVersion}.0.0, Culture=neutral, PublicKeyToken=6bbf07329f6aa8df";
        private const string Core = "System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e";
        private const string TString = "System.String, " + Core;
        private const string TBool = "System.Boolean, " + Core;
        private const string TInt = "System.Int32, " + Core;
        private const string TDecimal = "System.Decimal, " + Core;
        private static string MesType(string name) => $"{Mes}.{name}, {Mes}, {MesVersion}";

        private const string DateTimePattern = "^(?<year>-?(?:[1-9][0-9]*)?[0-9]{4})-(?<month>1[0-2]|0[1-9])-(?<day>3[01]|0[1-9]|[12][0-9])T(?<hour>2[0-3]|[01][0-9]):(?<minute>[0-5][0-9]):(?<second>[0-5][0-9])(?<ms>\\.[0-9]+)?(?<timezone>Z|[+-](?:[0-9][0-9]):[0-9][0-9])$";

        private static XElement V(string name, string type, string value) => new(name, new XAttribute("value", value), new XAttribute("type", type));
        private static XElement Null(string name) => new(name, new XAttribute("isNull", "True"));
        private static XElement B(string name, bool value) => V(name, TBool, value ? "True" : "False");
        private static XElement Attributes() => new("Attributes", new XAttribute("type", "Collection"), new XAttribute("actualtype", MesType("AttributeCollection")));

        private static IEnumerable<XElement> Tail(string name, string description) => new[]
        {
            Null("CurrentMainState"), Null("CurrentStates"), B("IsTemplate", false), Null("RelationCollection"), Attributes(),
            V("Description", TString, description), Null("DocumentationURL"), Null("Image"), Null("EntityPicture"), V("Name", TString, name), Null("DataGroupName"),
        };

        private static XElement PropertyItem(EventColumn c, int order) => new("Item", new XAttribute("type", "Object"),
            new XElement("Object", new XAttribute("type", MesType("IoTSchemaProperty")),
                V("DataType", MesType("DataPlatform.DataType"), c.Type.ToString()),
                Null("ExclusiveMaximum"), Null("ExclusiveMinimum"), Null("FileContentType"),
                V("FriendlyName", TString, c.Name),
                V("IndexOrder", TInt, c.KeyOrder.ToString(CultureInfo.InvariantCulture)),
                B("IsArray", false),
                B("IsIndexed", c.KeyOrder > 0),
                B("IsRequired", c.KeyOrder > 0),
                Null("MaxFileSize"), Null("MaxFileSizeScale"), Null("Maximum"), Null("MaxLength"), Null("EntityTypeReference"), Null("Minimum"), Null("MinLength"), Null("IoTSchemaReference"),
                V("Order", TInt, order.ToString(CultureInfo.InvariantCulture)),
                Null("Pattern"), Null("DefaultValue"),
                Tail(c.Name, c.Description)));

        private static string SchemaJson(EventSchema schema)
        {
            var properties = new JsonObject();
            foreach (var c in schema.Columns)
            {
                properties[c.Name] = c.Type switch
                {
                    ColumnType.Integer => new JsonObject { ["type"] = "integer" },
                    ColumnType.DateTime => new JsonObject { ["type"] = "string", ["format"] = "date-time", ["pattern"] = DateTimePattern },
                    _ => new JsonObject { ["type"] = "string" },
                };
            }
            var root = new JsonObject
            {
                ["$schema"] = "http://json-schema.org/draft-04/schema#",
                ["type"] = "object",
                ["required"] = new JsonArray(schema.Columns.Where(c => c.KeyOrder > 0).OrderBy(c => c.KeyOrder).Select(c => (JsonNode)c.Name).ToArray()),
                ["properties"] = properties,
            };
            return root.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }

        /// <summary>The event definition as a MES export file, importable with the Import button of the IoT Event Definition list. The folder must exist first.</summary>
        public static string ExportXml(EventSchema schema, string? version = null)
        {
            mesVersion = string.Join(".", (version ?? DefaultMesVersion).Split('.').Take(2)); // 11.2.3 -> 11.2
            var folderPath = $"\\IoTEventDefinitions\\{Folder}";
            var iotSchema = new XElement("Object", new XAttribute("type", MesType("IoTSchema")),
                B("IsShared", false),
                V("Schema", TString, SchemaJson(schema)),
                new XElement("SchemaProperties", new XAttribute("type", "Collection"), new XAttribute("actualtype", MesType("IoTSchemaPropertyCollection")),
                    schema.Columns.Select((c, i) => PropertyItem(c, i + 1))),
                Tail(schema.Path, schema.Name));

            var definition = new XElement("Object", new XAttribute("type", MesType("IoTEventDefinition")),
                B("CreateDataSet", true),
                V("DataSetRetentionTime", TString, schema.RetentionDays > 0 ? $"[{{\"OlderThan\": {schema.RetentionDays}, \"Location\": \"Delete\"}}]" : "[]"),
                V("DuplicatesStrategy", $"Cmf.DataPlatform.Shared.IoTEventDefinitionDuplicatesStrategy, Cmf.DataPlatform.Shared, {MesVersion}", "Merge"),
                V("OwnerType", MesType("DataPlatform.IoTEventDefinitionOwnerType"), "System"),
                V("TimeToLive", TInt, schema.TimeToLiveDays.ToString(CultureInfo.InvariantCulture)),
                V("Type", TString, "Any"),
                B("IsEnabled", true),
                new XElement("IoTSchema", new XAttribute("type", "Object"), iotSchema),
                V("Scope", MesType("DataPlatform.IoTEventDefinitionScope"), "DataPlatform"),
                V("Domain", MesType("IoTEventDefinitionDomain"), "None"),
                new XElement("Folder", new XAttribute("type", "Object"),
                    new XElement("ObjectRef", new XAttribute("type", MesType("Folder")), new XAttribute("RefName", folderPath))),
                Tail(schema.Name, schema.Description));

            return new XElement("CMF.ExportFile", new XAttribute("version", "1.0"), definition).ToString();
        }

        #endregion

        /// <summary>MasterData JSON for the event definitions (same layout as the standard MasterData files: IoTEventDefinition and IoTSchemaProperties).</summary>
        public static string MasterDataJson()
        {
            var definitions = new JsonObject();
            var properties = new JsonObject();
            var d = 0;
            var p = 0;
            foreach (var schema in Schemas)
            {
                definitions[(++d).ToString(CultureInfo.InvariantCulture)] = new JsonObject
                {
                    ["Name"] = schema.Name,
                    ["Type"] = "DataPlatform",
                    ["TimeToLive"] = schema.TimeToLiveDays.ToString(CultureInfo.InvariantCulture),
                    ["IsEnabled"] = "Yes",
                    ["IsTemplate"] = "No",
                    ["Scope"] = "DataPlatform",
                    ["CreateDataSet"] = "Yes",
                    ["OwnerRole"] = "Administrators",
                    ["OwnerType"] = "Role",
                    ["Domain"] = "None",
                    ["DuplicatesStrategy"] = "Merge",
                    ["Folder"] = Folder,
                    ["DataSetRetentionTime"] = schema.RetentionDays > 0 ? new JsonArray(new JsonObject { ["OlderThan"] = schema.RetentionDays, ["Location"] = "Delete" }) : new JsonArray(),
                    ["HasLightVariant"] = "No",
                };
                var order = 0;
                foreach (var c in schema.Columns)
                {
                    order++;
                    properties[(++p).ToString(CultureInfo.InvariantCulture)] = new JsonObject
                    {
                        ["IoTEventDefinitionName"] = schema.Name,
                        ["IoTSchemaName"] = schema.Name + "Schema",
                        ["IoTSchemaIsShared"] = "No",
                        ["Name"] = c.Name,
                        ["FriendlyName"] = c.Name,
                        ["Description"] = c.Description,
                        ["IsIndexed"] = c.KeyOrder > 0 ? "Yes" : "No",
                        ["IsArray"] = "No",
                        ["IsRequired"] = c.KeyOrder > 0 ? "Yes" : "No",
                        ["DataType"] = c.Type.ToString(),
                        ["IsTemplate"] = "No",
                        ["Order"] = order.ToString(CultureInfo.InvariantCulture),
                        ["IndexOrder"] = c.KeyOrder.ToString(CultureInfo.InvariantCulture),
                        ["IsUserIdentifier"] = "No",
                        ["IsShared"] = "No",
                    };
                }
            }
            var root = new JsonObject { ["<DM>IoTEventDefinition"] = definitions, ["IoTSchemaProperties"] = properties };
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }
    }
}
