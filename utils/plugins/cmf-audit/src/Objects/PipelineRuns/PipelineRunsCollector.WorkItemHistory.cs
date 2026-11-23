using Cmf.CLI.Core;
using static audit.Objects.PipelineRuns.PipelineRunsMapper;

namespace audit.Objects.PipelineRuns;

public partial class PipelineRunsCollector
{
    private async Task CollectAllWorkItemHistoryAsync()
    {
        var ids = await ado.QueryWorkItemIdsAsync(s.Project,
            "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project ORDER BY [System.Id]");
        Log.Information($"Full work item history: reading {ids.Count} accessible item(s), all types/states, without date or PR filters");
        var completed = 0;
        await ForEachParallelAsync(ids.Distinct().ToList(), async id =>
        {
            var revisions = await workItemHistory.GetOrAdd(id,
                k => new Lazy<Task<List<System.Text.Json.JsonElement>>>(() => ado.GetWorkItemRevisionsAsync(s.Project, k))).Value;
            if (revisions.Count == 0) throw new InvalidOperationException($"Full history requested but no revisions returned for work item {id}");
            var ordered = revisions.OrderBy(r => Long(r, "rev")).ToList();
            if (ordered.Where((r, index) => Long(r, "rev") != index + 1 || Long(r, "id") != id).Any())
                throw new InvalidDataException($"Incomplete or mismatched revisions for work item {id}; refusing a partial full-history collection");
            // Revisions contain the item's fields and ID; no PR or current-state lookup is required.
            if (historyEmitted.TryAdd(id, true)) await AddRangeAsync(MapWorkItemHistory(ordered[^1], ordered, s));
            var done = Interlocked.Increment(ref completed);
            if (done % 100 == 0 || done == ids.Count) Log.Information($"Full work item history: {done}/{ids.Count} item(s) read");
        });
    }
}
