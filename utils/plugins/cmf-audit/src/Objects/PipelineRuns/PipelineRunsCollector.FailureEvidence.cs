using System.Collections.Concurrent;
using System.Text.Json;
using static audit.Objects.PipelineRuns.PipelineRunsMapper;

namespace audit.Objects.PipelineRuns;

public partial class PipelineRunsCollector
{
    private readonly ConcurrentDictionary<int, bool> collectedBuilds = new();
    private readonly DateTime policyObservedAt = DateTime.UtcNow;
    private readonly SemaphoreSlim projectIdGate = new(1);
    private string? policyProjectId;

    private async Task<Dictionary<string, FailureEvidence>> ReadFailureLogsAsync(int buildId, JsonElement? timeline)
    {
        var result = new Dictionary<string, FailureEvidence>();
        foreach (var record in Records(timeline).Where(r => Str(r, "type").Equals("Task", StringComparison.OrdinalIgnoreCase)
                     && Str(r, "result").Equals("failed", StringComparison.OrdinalIgnoreCase)))
        {
            var scanner = new FailureLogScanner();
            var logId = (int)Long(record, "log.id");
            var status = !s.LogErrors ? "Disabled" : logId <= 0 ? "NoLogReference" : "Truncated";
            if (s.LogErrors && logId > 0)
            {
                try
                {
                    for (var start = 0; start < s.LogMaxLines;)
                    {
                        var size = Math.Min(2000, s.LogMaxLines - start);
                        var lines = await ado.GetBuildLogLinesAsync(s.Project, buildId, logId, start, start + size - 1);
                        if (lines == null) { status = "Unavailable"; break; }
                        if (lines.Count > size) throw new InvalidDataException("Log page exceeded requested bounds.");
                        for (var i = 0; i < lines.Count; i++) scanner.Observe(lines[i], start + i + 1);
                        if (lines.Count < size) { status = "Complete"; break; }
                        start += lines.Count;
                    }
                }
                catch (UnauthorizedAccessException) { status = "AccessDenied"; }
                catch (NotSupportedException) { status = "Unsupported"; }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException)
                { status = "Unavailable"; }
            }
            var evidence = scanner.Finish(status, logId, FirstError(record).Message);
            result[Str(record, "id")] = evidence;
            if (s.LogErrors && evidence.ScanStatus != "Complete")
                WarnOrThrow($"Run {buildId}, task {Str(record, "id")}: log evidence {evidence.ScanStatus}.");
        }
        return result;
    }

    private async Task CollectPolicySnapshotAsync(JsonElement pr)
    {
        var policies = new List<JsonElement>();
        var status = "Complete";
        try
        {
            await projectIdGate.WaitAsync();
            try { policyProjectId ??= await ado.GetProjectIdAsync(s.Project); }
            finally { projectIdGate.Release(); }
            if (!Guid.TryParse(policyProjectId, out _)) throw new NotSupportedException();
            policies = await ado.GetPullRequestPoliciesAsync(s.Project, policyProjectId!, (int)Long(pr, "pullRequestId"));
        }
        catch (UnauthorizedAccessException) { status = "AccessDenied"; }
        catch (NotSupportedException) { status = "Unsupported"; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        { status = "Unavailable"; }
        Add(MapPolicySnapshot(pr, null, s, policyObservedAt, status));
        if (status != "Complete") { WarnOrThrow($"PR {Long(pr, "pullRequestId")}: policy evidence {status}."); return; }
        foreach (var policy in policies)
        {
            Add(MapPolicySnapshot(pr, policy, s, policyObservedAt, status));
            var buildId = LinkedPolicyBuildId(policy);
            if (buildId <= 0 || collectedBuilds.ContainsKey(buildId)) continue;
            try
            {
                var build = await ado.GetBuildAsync(s.Project, buildId);
                if (build != null && Date(build.Value, "finishTime") != null) await CollectBuildAsync(build.Value, null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UnauthorizedAccessException)
            { WarnOrThrow($"PR {Long(pr, "pullRequestId")}, linked build {buildId}: evidence unavailable."); }
        }
    }
}
