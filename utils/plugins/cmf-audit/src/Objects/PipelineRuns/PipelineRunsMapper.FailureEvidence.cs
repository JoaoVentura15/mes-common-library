using audit.Services;
using System.Text.Json;

namespace audit.Objects.PipelineRuns;

public static partial class PipelineRunsMapper
{
    public const string AreaFailure = "PipelineFailure";
    public const string AreaPolicy = "PullRequestPolicy";
    public const string BuildPolicyType = "0609b952-1397-4640-95ec-e00a01b2c241";

    public static int LinkedPolicyBuildId(JsonElement policy) =>
        Str(policy, "configuration.type.id").Equals(BuildPolicyType, StringComparison.OrdinalIgnoreCase)
        && Str(policy, "context.isExpired") != "true" ? (int)Math.Max(0, Long(policy, "context.buildId")) : 0;

    public static IEnumerable<PostTelemetry> MapFailureEvidence(JsonElement build, JsonElement? timeline,
        IReadOnlyDictionary<string, FailureEvidence> errors, PipelineRunsSettings s, int? parent = null)
    {
        var run = MapRun(build, timeline, s, parent);
        foreach (var record in Records(timeline))
        {
            if (!errors.TryGetValue(Str(record, "id"), out var error)) continue;
            var t = NewEvent(s, Str(build, "repository.name"), AreaFailure,
                Str(build, "id") + "/" + Str(record, "id"), Str(build, "definition.name"), Str(record, "name"));
            foreach (var key in new[] { "BuildId", "DefinitionId", "PullRequestId", "ParentBuildId", "SourceVersion" })
                Tag(t, key, run.Tags.FirstOrDefault(tag => tag.Key == key)?.Value);
            Tag(t, "RecordId", Str(record, "id"));
            Tag(t, "Attempt", Str(record, "attempt"));
            Tag(t, "RunResult", Str(build, "result"));
            Tag(t, "Category", error.Category);
            Tag(t, "Ecosystem", error.Ecosystem);
            Tag(t, "ErrorCode", error.Code);
            Text(t, "ErrorMessage", error.Message);
            Tag(t, "Fingerprint", error.Fingerprint);
            Tag(t, "EvidenceSource", error.Source);
            Tag(t, "ScanStatus", error.ScanStatus);
            Tag(t, "LogId", error.LogId.ToString());
            Tag(t, "LogLine", error.LogLine.ToString());
            Tag(t, "LinesScanned", error.LinesScanned.ToString());
            Moment(t, "StartedAt", Date(record, "startTime") ?? Date(build, "startTime"));
            Moment(t, "FinishedAt", Date(record, "finishTime") ?? Date(build, "finishTime"));
            yield return t;
        }
    }

    public static PostTelemetry MapPolicySnapshot(JsonElement pr, JsonElement? policy, PipelineRunsSettings s,
        DateTime observedAt, string snapshotStatus)
    {
        var p = policy ?? default;
        var id = policy == null ? 0 : Long(p, "configuration.id");
        var t = NewEvent(s, Str(pr, "repository.name"), AreaPolicy, Str(pr, "pullRequestId") + "/" + id);
        Tag(t, "PullRequestId", Str(pr, "pullRequestId"));
        Tag(t, "PolicyId", id.ToString());
        Tag(t, "SnapshotStatus", snapshotStatus);
        Tag(t, "PRStatus", Str(pr, "status"));
        Tag(t, "IsDraft", Str(pr, "isDraft"));
        Tag(t, "HeadCommit", Str(pr, "lastMergeSourceCommit.commitId"));
        Tag(t, "TargetBranch", Str(pr, "targetRefName"));
        Text(t, "Title", Str(pr, "title"));
        Moment(t, "CreatedAt", Date(pr, "creationDate"));
        Moment(t, "ObservedAt", observedAt);
        if (policy == null) return t;
        var buildPolicy = Str(p, "configuration.type.id").Equals(BuildPolicyType, StringComparison.OrdinalIgnoreCase);
        var required = Str(p, "configuration.isBlocking") == "true" && Str(p, "configuration.isEnabled") == "true"
            && Str(p, "configuration.isDeleted") != "true";
        var status = Str(p, "status");
        var active = Str(pr, "status") == "active" && Str(pr, "isDraft") == "false";
        Tag(t, "EvaluationId", Str(p, "evaluationId"));
        Tag(t, "PolicyType", Str(p, "configuration.type.id"));
        Tag(t, "DefinitionId", Str(p, "configuration.settings.buildDefinitionId"));
        Tag(t, "BuildId", LinkedPolicyBuildId(p).ToString());
        Tag(t, "Status", status);
        Tag(t, "IsRequired", required ? "1" : "0");
        Tag(t, "IsBuildPolicy", buildPolicy ? "1" : "0");
        Tag(t, "IsExpired", Str(p, "context.isExpired"));
        Tag(t, "BlocksMerge", active && required && (status is "queued" or "running" or "rejected" or "broken") ? "1" : "0");
        Tag(t, "FailingBuildPolicy", active && required && buildPolicy && status == "rejected"
            && Str(p, "context.isExpired") != "true" ? "1" : "0");
        Moment(t, "EvaluationStartedAt", Date(p, "startedDate"));
        Moment(t, "EvaluationCompletedAt", Date(p, "completedDate"));
        return t;
    }
}
