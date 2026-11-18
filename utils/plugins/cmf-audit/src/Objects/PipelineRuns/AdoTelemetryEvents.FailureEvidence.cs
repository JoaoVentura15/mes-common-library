using audit.Services;

namespace audit.Objects.PipelineRuns;

public static partial class AdoTelemetryEvents
{
    private static EventColumn[] EvidenceKeys(string id, bool integer = true) => new[]
    {
        Key("Organization", ColumnType.String, "Azure DevOps organization.", 1, Isa(e => e.Enterprise?.Name)),
        Key("Project", ColumnType.String, "Azure DevOps project.", 2, Isa(e => e.Site?.Name)),
        Key(id, integer ? ColumnType.Integer : ColumnType.String, "Entity identifier.", 3, integer ? TagInt(id) : Tag(id)),
    };

    public static readonly EventSchema PipelineFailure = new()
    {
        Name = "PipelineFailure",
        Description = "Redacted evidence from one failed task. Category describes the observed failure mode, not proven root cause.",
        Areas = new[] { PipelineRunsMapper.AreaFailure },
        EntityTime = e => StampOf(e, "StartedAt"),
        Columns = EvidenceKeys("BuildId").Concat(new[]
        {
            Key("RecordId", ColumnType.String, "Failed timeline task id.", 4, Tag("RecordId")),
            S("Repository", "Repository.", Isa(e => e.Facility?.Name)),
            S("Pipeline", "Pipeline name.", Isa(e => e.Flow?.Name)),
            S("Task", "Failed task.", Isa(e => e.Step?.Name)),
            I("DefinitionId", "Pipeline definition id."), I("PullRequestId", "Associated PR; zero if unknown."),
            I("ParentBuildId", "Associated parent build; zero if unknown."),
            S("SourceVersion", "Run commit."), I("Attempt", "Task attempt."), S("RunResult", "Final run result; a failed task may be recovered."),
            S("Category", "Observed error category."), S("Ecosystem", "Observed package ecosystem."),
            S("ErrorCode", "Normalized error code."), S("ErrorMessage", "Bounded redacted excerpt.", Text("ErrorMessage")),
            S("Fingerprint", "SHA256 of redacted grouping inputs."), S("EvidenceSource", "TaskLog or Timeline."),
            S("ScanStatus", "Complete, Truncated, Unavailable, AccessDenied, NoLogReference, Disabled or Unsupported."),
            I("LogId", "Task log id."), I("LogLine", "One-based excerpt line."), I("LinesScanned", "Lines inspected."),
            D("StartedAt", "Task start UTC.", Stamp("StartedAt")), D("FinishedAt", "Task finish UTC.", Stamp("FinishedAt")),
        }).ToArray(),
    };

    public static readonly EventSchema PullRequestPolicy = new()
    {
        Name = "PullRequestPolicy",
        Description = "Latest policy evaluation per PR/configuration; PolicyId 0 is the snapshot marker. Match marker ObservedAt before using rows.",
        Areas = new[] { PipelineRunsMapper.AreaPolicy },
        EntityTime = e => StampOf(e, "CreatedAt"),
        Columns = EvidenceKeys("PullRequestId").Concat(new[]
        {
            Key("PolicyId", ColumnType.Integer, "Policy configuration id; zero marks the snapshot.", 4, TagInt("PolicyId")),
            S("Repository", "Repository.", Isa(e => e.Facility?.Name)),
            S("Title", "PR title.", Text("Title")), S("TargetBranch", "PR target branch."),
            S("PRStatus", "Current PR state."), S("IsDraft", "PR draft state."), S("HeadCommit", "Current source head commit."),
            S("SnapshotStatus", "Complete, AccessDenied, Unsupported or Unavailable."),
            S("EvaluationId", "Evaluation id."), S("PolicyType", "Policy type GUID."),
            I("DefinitionId", "Required build definition id."), I("BuildId", "Explicit context build id; zero when absent or expired."),
            S("Status", "Evaluation state."), I("IsRequired", "Enabled blocking policy."),
            I("IsBuildPolicy", "Build validation policy."), S("IsExpired", "Expired build context, when supplied."),
            I("BlocksMerge", "Active non-draft PR with required outstanding evaluation."),
            I("FailingBuildPolicy", "Required non-expired rejected build validation; does not assert a technical root cause."),
            D("CreatedAt", "PR creation UTC; stable entity time.", Stamp("CreatedAt")),
            D("ObservedAt", "Snapshot observation UTC.", Stamp("ObservedAt")),
            D("EvaluationStartedAt", "Evaluation start UTC.", Stamp("EvaluationStartedAt")),
            D("EvaluationCompletedAt", "Evaluation completion UTC; not continuous blocked duration.", Stamp("EvaluationCompletedAt")),
        }).ToArray(),
    };
}
