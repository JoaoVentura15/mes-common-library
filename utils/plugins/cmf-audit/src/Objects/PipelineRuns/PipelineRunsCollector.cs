using audit.Services;
using Cmf.CLI.Core;
using System.Text.Json;
using static audit.Objects.PipelineRuns.PipelineRunsMapper;

namespace audit.Objects.PipelineRuns
{
    /// <summary>
    /// Reads pipeline runs, timelines, approvals, changes, tests, pull requests and agent jobs from
    /// Azure DevOps and turns them into PostTelemetry events.
    /// </summary>
    public class PipelineRunsCollector
    {
        private readonly IAzureDevOpsRestClient ado;
        private readonly PipelineRunsSettings s;
        private readonly int parallelism;
        private readonly List<PostTelemetry> events = new();
        private readonly object gate = new();
        private bool testsAvailable = true;
        private bool approvalsAvailable = true;
        private bool packageFileAvailable = true;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Lazy<Task<List<JsonElement>>>> workItemHistory = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> historyEmitted = new();
        private Task<Dictionary<string, HashSet<int>>>? changedWorkItemPullRequests;
        private readonly Dictionary<string, Task<((string Id, string Version)? Package, bool Failed)>> installedByCommit = new();

        public PipelineRunsCollector(IAzureDevOpsRestClient ado, PipelineRunsSettings settings, int parallelism = 4)
        {
            this.ado = ado;
            this.s = settings;
            this.parallelism = Math.Max(1, parallelism);
        }

        private void Add(PostTelemetry? e) { if (e != null) lock (gate) events.Add(e); }
        private void AddRange(IEnumerable<PostTelemetry> e) { lock (gate) events.AddRange(e); }
        private void WarnOrThrow(string message, Exception? exception = null)
        {
            if (s.StrictCollection) throw new InvalidOperationException(message, exception);
            Log.Warning(message);
        }

        public async Task<List<PostTelemetry>> CollectAsync()
        {
            if ((s.Parts & ~PipelineRunsSettings.MetadataParts) == PipelineRunsParts.None)
            {
                if (s.Parts.HasFlag(PipelineRunsParts.ReleaseSchedule)) await CollectReleaseScheduleAsync();
                if (s.Parts.HasFlag(PipelineRunsParts.ReleaseTags)) await CollectReleaseTagsAsync(await ResolveRepositoriesAsync());
                if (s.Parts.HasFlag(PipelineRunsParts.PullRequestFiles)) await CollectPullRequestFilesAsync(await ResolveRepositoriesAsync());
                if (s.Parts.HasFlag(PipelineRunsParts.WorkItemScope)) await CollectWorkItemScopeAsync();
                return events;
            }

            var repositories = await ResolveRepositoriesAsync();

            var builds = new List<JsonElement>();
            if (repositories.Count == 0)
            {
                builds.AddRange(await ado.ListBuildsAsync(s.Project, s.Since, null));
            }
            else
            {
                foreach (var (_, id) in repositories)
                {
                    builds.AddRange(await ado.ListBuildsAsync(s.Project, s.Since, id));
                }
            }
            builds = builds.Where(b => Date(b, "finishTime") != null).GroupBy(b => Long(b, "id")).Select(g => g.First()).ToList();
            Log.Information($"Pipeline runs: {builds.Count} completed run(s) in {s.Organization}/{s.Project} since {s.Since:yyyy-MM-dd HH:mm} UTC");

            var parents = FindParents(builds, s);
            using (var throttle = new SemaphoreSlim(parallelism))
            {
                var done = 0;
                await Task.WhenAll(builds.Select(async b =>
                {
                    await throttle.WaitAsync();
                    try
                    {
                        await CollectBuildAsync(b, parents.TryGetValue(Long(b, "id"), out var parent) ? (int)parent : null);
                    }
                    catch (UnauthorizedAccessException) { throw; }
                    catch (Exception ex)
                    {
                        WarnOrThrow($"Run {Str(b, "id")}: {ex.Message}", ex);
                    }
                    finally
                    {
                        throttle.Release();
                        var n = Interlocked.Increment(ref done);
                        if (n % 50 == 0 || n == builds.Count) Log.Information($"Pipeline runs: {n}/{builds.Count} runs read");
                    }
                }));
            }

            if (s.Parts.HasFlag(PipelineRunsParts.PullRequests))
            {
                var iterations = await ado.GetIterationsAsync(s.Project);
                if (iterations != null) AddRange(MapSprints(iterations.Value, s));
                var repos = repositories.Count > 0
                    ? repositories
                    : s.RefreshOpenPullRequests
                        ? (await ado.ListRepositoriesAsync(s.Project)).Select(repo => (Str(repo, "name"), Str(repo, "id"))).Where(repo => repo.Item2.Length > 0).Distinct().ToList()
                        : builds.Where(b => Str(b, "repository.type").Equals("TfsGit", StringComparison.OrdinalIgnoreCase))
                            .Select(b => (Str(b, "repository.name"), Str(b, "repository.id"))).Where(repo => repo.Item2.Length > 0).Distinct().ToList();
                foreach (var (name, id) in repos)
                {
                    await CollectPullRequestsAsync(name, id);
                }
            }

            if (s.Parts.HasFlag(PipelineRunsParts.AgentJobs))
            {
                if (s.Pools.Count == 0)
                    WarnOrThrow("Agent jobs requested without pools; pass --pools or exclude jobs from --include.");
                else
                    await CollectAgentJobsAsync();
            }

            if (s.Parts.HasFlag(PipelineRunsParts.ReleaseSchedule))
            {
                await CollectReleaseScheduleAsync();
            }

            if (s.Parts.HasFlag(PipelineRunsParts.ReleaseTags))
            {
                await CollectReleaseTagsAsync(repositories);
            }

            if (s.Parts.HasFlag(PipelineRunsParts.PullRequestFiles))
            {
                await CollectPullRequestFilesAsync(repositories);
            }

            if (s.Parts.HasFlag(PipelineRunsParts.WorkItemScope))
            {
                await CollectWorkItemScopeAsync();
            }

            return events;
        }

        /// <summary>
        /// Reads the revisions of every release Feature (title contains the configured text) and emits one ReleaseSchedule event per Start/Target Date change.
        /// The whole history is rebuilt on every run, independent of --days/--since, so re-posting never loses or duplicates a change.
        /// </summary>
        private async Task CollectReleaseScheduleAsync()
        {
            if (string.IsNullOrWhiteSpace(s.ReleaseTitleContains))
            {
                WarnOrThrow("Release schedule skipped: set --releaseTitleContains to the text that identifies a release Feature.");
                return;
            }
            var wiql = $"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.WorkItemType] = 'Feature' AND [System.Title] CONTAINS '{s.ReleaseTitleContains.Replace("'", "''")}' ORDER BY [System.Id]";
            var ids = await ado.QueryWorkItemIdsAsync(s.Project, wiql);
            Log.Information($"Release schedule: reading the revisions of {ids.Count} Feature(s) with '{s.ReleaseTitleContains}' in the title");
            int releases = 0, changes = 0;
            await ForEachParallelAsync(ids, async id =>
            {
                var revisions = await ado.GetWorkItemRevisionsAsync(s.Project, id);
                var mapped = MapReleaseSchedule(id, revisions, s);
                if (mapped.Count == 0) return;
                AddRange(mapped);
                Interlocked.Increment(ref releases);
                Interlocked.Add(ref changes, mapped.Count);
            });
            Log.Information($"Release schedule: {changes} date change(s) of {releases} release Feature(s)");
        }

        private string[] ScopeFields => new[] { "System.Title", "System.Parent", "Microsoft.VSTS.Common.StateChangeDate", s.MilestoneField, s.StoryPointsField }.Where(f => f.Length > 0).ToArray();

        /// <summary>
        /// Reads the open work items of the scope types and those changed since --since (a Removed item is posted too, so a row that left the scope is updated),
        /// walks System.Parent up to the Feature and Epic of each, and emits one WorkItemScope event per item. The state of an item is its current one, so
        /// re-posting updates the rows. WIQL answers at most 20000 ids.
        /// </summary>
        private async Task CollectWorkItemScopeAsync()
        {
            if (s.ScopeTypes.Count == 0)
            {
                WarnOrThrow("Work item scope: no work item type to read; pass --scopeTypes");
                return;
            }
            static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
            var settled = s.DoneStates.Concat(s.RemovedStates).Distinct(StringComparer.OrdinalIgnoreCase).Select(Quote);
            var wiql = $"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.WorkItemType] IN ({string.Join(", ", s.ScopeTypes.Select(Quote))}) " +
                       $"AND ([System.ChangedDate] >= '{s.Since:yyyy-MM-dd}' OR [System.State] NOT IN ({string.Join(", ", settled)})) ORDER BY [System.Id]";
            var ids = await ado.QueryWorkItemIdsAsync(s.Project, wiql);
            if (ids.Count >= 20000) WarnOrThrow("Work item scope: WIQL answers at most 20000 ids and this query reached it; narrow it with --since or --scopeTypes");
            Log.Information($"Work item scope: reading {ids.Count} {string.Join("/", s.ScopeTypes)} work item(s) open or changed since {s.Since:yyyy-MM-dd}");

            var all = new Dictionary<int, JsonElement>();
            var scope = new List<int>();
            async Task ReadAsync(IEnumerable<int> wanted)
            {
                await ForEachParallelAsync(wanted.Distinct().Chunk(200).ToList(), async chunk =>
                {
                    var read = await ado.GetWorkItemsAsync(s.Project, chunk, ScopeFields);
                    lock (gate) foreach (var item in read) all[(int)Long(item, "id")] = item;
                });
            }
            await ReadAsync(ids);
            scope.AddRange(ids.Where(all.ContainsKey));

            // Feature and Epic above the items: read the parents level by level (at most 6 levels), each work item once
            var frontier = scope.Select(i => ParentId(all[i])).Where(p => p > 0 && !all.ContainsKey(p)).Distinct().ToList();
            for (var depth = 0; depth < 6 && frontier.Count > 0; depth++)
            {
                await ReadAsync(frontier);
                frontier = frontier.Where(all.ContainsKey).Select(p => ParentId(all[p])).Where(p => p > 0 && !all.ContainsKey(p)).Distinct().ToList();
            }

            // topmost: the highest ancestor of the type (an Epic can sit under another Epic, and the forecast is of the root one)
            JsonElement? Ancestor(JsonElement item, string type, bool topmost)
            {
                var current = item;
                JsonElement? result = null;
                for (var level = 0; level < 8; level++)
                {
                    var parent = ParentId(current);
                    if (parent == 0 || !all.TryGetValue(parent, out var found)) break;
                    if (Field(found, "System.WorkItemType").Equals(type, StringComparison.OrdinalIgnoreCase))
                    {
                        result = found;
                        if (!topmost) break;
                    }
                    current = found;
                }
                return result;
            }

            int emitted = 0, withEpic = 0, done = 0;
            foreach (var id in scope.OrderBy(i => i))
            {
                var item = all[id];
                var epic = Ancestor(item, s.EpicType, topmost: true);
                var mapped = MapWorkItemScope(item, Ancestor(item, s.FeatureType, topmost: false), epic, s);
                if (mapped == null) continue;
                Add(mapped);
                emitted++;
                if (epic != null) withEpic++;
                if (mapped.Parameters.Any(p => p.Name == "Done" && p.NumericValues.FirstOrDefault() == 1)) done++;
            }
            Log.Information($"Work item scope: {emitted} work item(s), {done} done, {withEpic} under an Epic");
            if (s.MilestoneField.Length > 0)
            {
                var milestones = scope.Select(i => PipelineRunsMapper.Field(all[i], s.MilestoneField)).Where(m => m.Length > 0).GroupBy(m => m).OrderByDescending(g => g.Count()).Take(5).Select(g => $"{g.Key} x{g.Count()}").ToList();
                Log.Information($"Work item scope: {scope.Count(i => PipelineRunsMapper.Field(all[i], s.MilestoneField).Length > 0)} work item(s) with a Milestone" + (milestones.Count > 0 ? " (" + string.Join(", ", milestones) + ")" : ""));
            }
        }

        /// <summary>Reads the version tags of the repositories and emits one ReleaseTag event per tag whose name matches the tag pattern.</summary>
        private async Task CollectReleaseTagsAsync(List<(string name, string id)> repositories)
        {
            if (repositories.Count == 0)
            {
                WarnOrThrow("Release tags: no repository to read; pass --repositories");
                return;
            }
            foreach (var (name, id) in repositories)
            {
                var tags = (await ado.ListTagsAsync(s.Project, id))
                    .Where(r => s.ReleaseTag.IsMatch(Str(r, "name").Replace("refs/tags/", "")))
                    .ToList();
                Log.Information($"Release tags: {tags.Count} version tag(s) in {name}");
                var emitted = 0;
                await ForEachParallelAsync(tags, async tag =>
                {
                    var objectId = Str(tag, "objectId");
                    var peeled = Str(tag, "peeledObjectId");
                    JsonElement? annotated = peeled.Length > 0 ? await ado.GetAnnotatedTagAsync(s.Project, id, objectId) : null;
                    var commit = await ado.GetCommitAsync(s.Project, id, peeled.Length > 0 ? peeled : objectId);
                    var pushes = await ado.ListRefPushesAsync(s.Project, id, Str(tag, "name"));
                    var pushedAt = pushes.Select(p => Date(p, "date")).Where(x => x != null).OrderByDescending(x => x).FirstOrDefault();
                    var mapped = MapReleaseTag(name, tag, annotated, commit, s, pushedAt);
                    if (mapped == null) return;
                    Add(mapped);
                    Interlocked.Increment(ref emitted);
                });
                Log.Information($"Release tags: {emitted} tag event(s) from {name}");
            }
        }

        /// <summary>Reads the files changed by each completed pull request (the changes of its merge commit) and emits one PullRequestFiles event per pull request.</summary>
        private async Task CollectPullRequestFilesAsync(List<(string name, string id)> repositories)
        {
            if (repositories.Count == 0)
            {
                WarnOrThrow("Pull request files: no repository to read; pass --repositories");
                return;
            }
            foreach (var (name, id) in repositories)
            {
                var prs = (await ado.ListPullRequestsAsync(s.Project, id, s.Since, "closed"))
                    .Where(p => Str(p, "status").Equals("completed", StringComparison.OrdinalIgnoreCase) && Str(p, "lastMergeCommit.commitId").Length > 0)
                    .GroupBy(p => Long(p, "pullRequestId")).Select(g => g.First()).ToList();
                Log.Information($"Pull request files: {prs.Count} completed pull request(s) in {name} since {s.Since:yyyy-MM-dd}");
                var emitted = 0;
                var done = 0;
                await ForEachParallelAsync(prs, async pr =>
                {
                    var changes = await ado.GetCommitChangesAsync(s.Project, id, Str(pr, "lastMergeCommit.commitId"));
                    var mapped = MapPullRequestFiles(pr, changes, s);
                    if (mapped != null)
                    {
                        Add(mapped);
                        Interlocked.Increment(ref emitted);
                    }
                    var count = Interlocked.Increment(ref done);
                    if (count % 50 == 0 || count == prs.Count) Log.Information($"Pull request files: {count}/{prs.Count} read");
                });
                Log.Information($"Pull request files: {emitted} event(s) from {name}");
            }
        }

        private async Task<List<(string name, string id)>> ResolveRepositoriesAsync()
        {
            var list = new List<(string, string)>();
            foreach (var name in s.Repositories)
            {
                var repo = await ado.GetRepositoryAsync(s.Project, name);
                if (repo == null)
                {
                    throw new InvalidOperationException($"Repository '{name}' not found in {s.Project}.");
                }
                list.Add((Str(repo.Value, "name"), Str(repo.Value, "id")));
            }
            return list;
        }

        /// <summary>
        /// Links runs queued by another run (PR-Publish by PR-Validate, CI-Publish by CI-Validate): same branch,
        /// same role, different pipeline, queued while the other run was running.
        /// </summary>
        public static Dictionary<long, long> FindParents(List<JsonElement> builds, PipelineRunsSettings s)
        {
            var result = new Dictionary<long, long>();
            var byBranch = builds.GroupBy(b => Str(b, "sourceBranch")).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var b in builds)
            {
                var queued = Date(b, "queueTime");
                if (queued == null) continue;
                var role = ResolveRole(Str(b, "definition.name"), s);
                var candidates = byBranch[Str(b, "sourceBranch")]
                    .Where(c => Long(c, "id") != Long(b, "id") && Long(c, "definition.id") != Long(b, "definition.id"))
                    .Where(c => ResolveRole(Str(c, "definition.name"), s) == role)
                    .Where(c => Date(c, "startTime") <= queued && queued <= Date(c, "finishTime"))
                    .OrderByDescending(c => Str(c, "definition.name").EndsWith("-Changes", StringComparison.OrdinalIgnoreCase))
                    .ThenBy(c => Date(c, "startTime"))
                    .ToList();
                if (candidates.Count > 0) result[Long(b, "id")] = Long(candidates[0], "id");
            }
            return result;
        }

        /// <summary>
        /// Package installed by an env/* branch: root package of /cmfpackage.json at the run's commit, read once per commit.
        /// A clean miss (no file, invalid JSON) is cached; a failed read is not, so another run on the commit can retry.
        /// </summary>
        private async Task<(string Id, string Version)?> GetInstalledPackageAsync(JsonElement b)
        {
            var commit = Str(b, "sourceVersion");
            var repositoryId = Str(b, "repository.id");
            if (commit.Length == 0 || repositoryId.Length == 0 || !packageFileAvailable) return null;
            Task<((string Id, string Version)? Package, bool Failed)> read;
            lock (gate)
            {
                if (!installedByCommit.TryGetValue(commit, out read!))
                {
                    installedByCommit[commit] = read = ReadInstalledPackageAsync(repositoryId, commit);
                }
            }
            var (package, failed) = await read;
            if (failed)
            {
                lock (gate)
                {
                    if (installedByCommit.TryGetValue(commit, out var cached) && cached == read) installedByCommit.Remove(commit);
                }
            }
            return package;
        }

        private async Task<((string Id, string Version)? Package, bool Failed)> ReadInstalledPackageAsync(string repositoryId, string commit)
        {
            try
            {
                var file = await ado.GetJsonFileAsync(s.Project, repositoryId, "/cmfpackage.json", commit);
                var id = file == null ? "" : Str(file.Value, "packageId");
                var version = file == null ? "" : Str(file.Value, "version");
                return (id.Length > 0 && version.Length > 0 ? (id, version) : null, false);
            }
            catch (UnauthorizedAccessException ex)
            {
                packageFileAvailable = false;
                WarnOrThrow($"/cmfpackage.json reads skipped from now on: {ex.Message} (the token needs Code: Read)", ex);
                return (null, true);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                WarnOrThrow($"/cmfpackage.json at {commit} not read: {ex.Message}", ex);
                return (null, true);
            }
        }

        private async Task CollectBuildAsync(JsonElement b, int? parent)
        {
            var id = (int)Long(b, "id");
            var timeline = await ado.GetTimelineAsync(s.Project, id);

            int? failed = null; // stays null when the build has no test run (or tests are skipped), so FailedTests is NULL rather than 0
            if (s.Parts.HasFlag(PipelineRunsParts.Tests) && testsAvailable)
            {
                try
                {
                    var testRuns = await ado.GetTestRunsAsync(s.Project, Str(b, "uri"));
                    var resultsByRun = await Task.WhenAll(testRuns.Select(run => ado.GetFailedTestResultsAsync(s.Project, (int)Long(run, "id"))));
                    for (var i = 0; i < testRuns.Count; i++)
                    {
                        var run = testRuns[i];
                        var results = resultsByRun[i];
                        failed = (failed ?? 0) + results.Count;
                        Add(MapTestRun(b, run, results.Count, s));
                        AddRange(results.Select(r => MapTestFailure(b, run, r, s)));
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    testsAvailable = false;
                    WarnOrThrow($"Test results skipped from now on: {ex.Message} (the token needs Test Management: Read)", ex);
                }
            }

            (string Id, string Version)? installed = null;
            if (ResolveRole(Str(b, "definition.name"), s) == "CD" && Str(b, "sourceBranch").StartsWith("refs/heads/env/", StringComparison.OrdinalIgnoreCase))
            {
                installed = await GetInstalledPackageAsync(b);
            }

            Add(MapRun(b, timeline, s, parent, failed, installed));

            if (s.Parts.HasFlag(PipelineRunsParts.Steps))
            {
                AddRange(MapTimeline(b, timeline, s));
            }

            if (s.Parts.HasFlag(PipelineRunsParts.Approvals) && approvalsAvailable)
            {
                var stages = ApprovalStagesById(timeline);
                if (stages.Count > 0)
                {
                    try
                    {
                        foreach (var approval in await ado.GetApprovalsAsync(s.Project, stages.Keys))
                        {
                            Add(MapApproval(b, approval, stages.GetValueOrDefault(Str(approval, "id"), ""), s));
                        }
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        approvalsAvailable = false;
                        WarnOrThrow($"Approvals skipped from now on: {ex.Message}", ex);
                    }
                }
            }

            if (s.Parts.HasFlag(PipelineRunsParts.Changes))
            {
                Add(MapRunChanges(b, await ado.GetBuildChangesAsync(s.Project, id), s));
            }
        }

        private async Task CollectPullRequestsAsync(string repositoryName, string repositoryId)
        {
            var recent = (await ado.ListPullRequestsAsync(s.Project, repositoryId, s.Since, "created"))
                .Concat(await ado.ListPullRequestsAsync(s.Project, repositoryId, s.Since, "closed"));
            var prs = (s.RefreshOpenPullRequests ? recent.Concat(await ado.ListActivePullRequestsAsync(s.Project, repositoryId)) : recent)
                .GroupBy(p => Long(p, "pullRequestId")).Select(g => g.First()).ToList();

            var inWindow = prs.Count;
            if (s.RefreshChangedWorkItems)
            {
                prs.AddRange(await ReadPullRequestsOfChangedWorkItemsAsync(repositoryName, repositoryId, prs.Select(p => (int)Long(p, "pullRequestId")).ToHashSet()));
            }

            Log.Information($"Pull requests: {inWindow} created, closed or open in {repositoryName} since {s.Since:yyyy-MM-dd}{(prs.Count > inWindow ? $" plus {prs.Count - inWindow} older through changed work items" : "")}, reading their work items, comments and iterations");
            var refs = new System.Collections.Concurrent.ConcurrentDictionary<long, List<int>>();
            var linkedPrs = 0;
            await ForEachParallelAsync(prs, async pr =>
            {
                var items = await ado.GetPullRequestWorkItemRefsAsync(s.Project, repositoryId, (int)Long(pr, "pullRequestId"));
                refs[Long(pr, "pullRequestId")] = items.Select(i => (int)Long(i, "id")).Where(i => i > 0).ToList();
                var n = Interlocked.Increment(ref linkedPrs);
                if (n % 25 == 0 || n == prs.Count) Log.Information($"Pull requests: {n}/{prs.Count} work item links read");
            });
            Log.Information($"Work items: reading {refs.Values.SelectMany(v => v).Distinct().Count()} linked work item(s)");
            var workItems = (await ado.GetWorkItemsAsync(s.Project, refs.Values.SelectMany(v => v), s.WorkItemFields.Values.Append(s.StoryPointsField)))
                .GroupBy(w => Long(w, "id")).ToDictionary(g => g.Key, g => g.First());

            var mappedPrs = 0;
            await ForEachParallelAsync(prs, async pr =>
            {
                var linked = refs[Long(pr, "pullRequestId")].Where(i => workItems.ContainsKey(i)).Select(i => workItems[i]).ToList();
                var threadsTask = ado.GetPullRequestThreadsAsync(s.Project, repositoryId, (int)Long(pr, "pullRequestId"));
                var iterationsTask = ado.GetPullRequestIterationsAsync(s.Project, repositoryId, (int)Long(pr, "pullRequestId"));
                var threads = await threadsTask;
                var iterations = await iterationsTask;
                Add(MapPullRequest(pr, linked, s, threads, iterations));
                foreach (var item in linked)
                {
                    var id = (int)Long(item, "id");
                    List<JsonElement>? history = null;
                    if (s.WorkItemHistory)
                    {
                        history = await workItemHistory.GetOrAdd(id, k => new Lazy<Task<List<JsonElement>>>(() => ado.GetWorkItemRevisionsAsync(s.Project, k))).Value;
                    }
                    Add(MapPullRequestWorkItem(pr, item, s));
                    Add(MapWorkItemFlow(pr, item, history, s));
                    if (history != null && historyEmitted.TryAdd(id, true)) AddRange(MapWorkItemHistory(item, history, s));
                }
                var n = Interlocked.Increment(ref mappedPrs);
                if (n % 25 == 0 || n == prs.Count)
                    Log.Information($"Pull requests: {n}/{prs.Count} read{(s.WorkItemHistory ? $" ({workItemHistory.Count} work item histories)" : "")}");
            });
            Log.Information($"Pull requests: {prs.Count} read in {repositoryName}");
        }

        /// <summary>
        /// A work item that changes after its pull request left the lookback window would keep its old state, iteration and team in the flow rows.
        /// Finds the work items changed since --since (once per run), follows their links back to pull requests of this repository and reads those pull requests.
        /// </summary>
        private async Task<List<JsonElement>> ReadPullRequestsOfChangedWorkItemsAsync(string repositoryName, string repositoryId, HashSet<int> alreadyRead)
        {
            var byRepository = await (changedWorkItemPullRequests ??= FindPullRequestsOfChangedWorkItemsAsync());
            var missing = byRepository.TryGetValue(repositoryId, out var linked) ? linked.Where(id => !alreadyRead.Contains(id)).OrderBy(id => id).ToList() : new List<int>();
            var found = new System.Collections.Concurrent.ConcurrentBag<JsonElement>();
            await ForEachParallelAsync(missing, async id =>
            {
                var pr = await ado.GetPullRequestAsync(s.Project, repositoryId, id);
                if (pr != null) found.Add(pr.Value);
            });
            Log.Information($"Pull requests: {found.Count} older pull request(s) of {repositoryName} linked to work items changed since {s.Since:yyyy-MM-dd} added to the refresh");
            return found.OrderBy(p => Long(p, "pullRequestId")).ToList();
        }

        /// <summary>Pull request ids per repository id (lower case) linked to the work items changed since --since.</summary>
        private async Task<Dictionary<string, HashSet<int>>> FindPullRequestsOfChangedWorkItemsAsync()
        {
            var result = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            var wiql = $"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.ChangedDate] >= '{s.Since:yyyy-MM-dd}' ORDER BY [System.Id]";
            var ids = await ado.QueryWorkItemIdsAsync(s.Project, wiql);
            if (ids.Count >= 20000) WarnOrThrow("Changed work items: WIQL answers at most 20000 ids and this query reached it; narrow it with --since");
            Log.Information($"Changed work items: reading the links of {ids.Count} work item(s) changed since {s.Since:yyyy-MM-dd}");
            var linked = new List<(int Id, List<(string Repository, int PullRequest)> PullRequests)>();
            foreach (var item in await ado.GetWorkItemRelationsAsync(s.Project, ids))
            {
                if (!item.TryGetProperty("relations", out var relations) || relations.ValueKind != JsonValueKind.Array) continue;
                var itemPullRequests = new List<(string, int)>();
                foreach (var relation in relations.EnumerateArray())
                {
                    var url = relation.ValueKind == JsonValueKind.Object && relation.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() ?? "" : "";
                    const string prefix = "vstfs:///Git/PullRequestId/";
                    if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    // the artifact id is <project id>/<repository id>/<pull request id>, with the slashes URL-encoded
                    var parts = Uri.UnescapeDataString(url.Substring(prefix.Length)).Split('/');
                    if (parts.Length != 3 || !int.TryParse(parts[2], out var prId)) continue;
                    itemPullRequests.Add((parts[1], prId));
                }
                if (itemPullRequests.Count > 0) linked.Add(((int)Long(item, "id"), itemPullRequests));
            }

            foreach (var (_, itemPullRequests) in linked)
                foreach (var (repository, prId) in itemPullRequests)
                {
                    if (!result.TryGetValue(repository, out var set)) result[repository] = set = new HashSet<int>();
                    set.Add(prId);
                }
            return result;
        }

        private async Task ForEachParallelAsync<T>(IEnumerable<T> items, Func<T, Task> body)
        {
            using var throttle = new SemaphoreSlim(parallelism);
            await Task.WhenAll(items.Select(async item =>
            {
                await throttle.WaitAsync();
                try { await body(item); }
                finally { throttle.Release(); }
            }));
        }

        private async Task CollectAgentJobsAsync()
        {
            List<JsonElement> pools;
            try
            {
                pools = await ado.ListPoolsAsync();
            }
            catch (UnauthorizedAccessException ex)
            {
                WarnOrThrow($"Agent jobs skipped: {ex.Message} (the token needs Agent Pools: Read)", ex);
                return;
            }
            var wanted = s.Pools.Any(p => p.Equals("all", StringComparison.OrdinalIgnoreCase))
                ? pools.Where(p => Str(p, "isHosted") != "true").ToList()
                : pools.Where(p => s.Pools.Any(w => w.Equals(Str(p, "name"), StringComparison.OrdinalIgnoreCase) || w == Str(p, "id"))).ToList();
            foreach (var requested in s.Pools.Where(name => !name.Equals("all", StringComparison.OrdinalIgnoreCase)))
            {
                if (!wanted.Any(pool => requested.Equals(Str(pool, "name"), StringComparison.OrdinalIgnoreCase) || requested == Str(pool, "id")))
                    WarnOrThrow($"Agent pool '{requested}' was not found.");
            }
            var poolNumber = 0;
            foreach (var pool in wanted)
            {
                Log.Information($"Agent jobs: reading pool {Str(pool, "name")} ({++poolNumber}/{wanted.Count})");
                var requests = await ado.ListJobRequestsAsync((int)Long(pool, "id"));
                if (requests.Count >= 2000) WarnOrThrow($"Agent jobs in pool {Str(pool, "name")} may be truncated at 2000 requests.");
                var jobs = requests
                    .Where(j => Date(j, "finishTime") is DateTime f && f >= s.Since).ToList();
                AddRange(jobs.Select(j => MapAgentJob(j, Str(pool, "name"), s)));
                Log.Information($"Agent jobs: {jobs.Count} finished in pool {Str(pool, "name")} since {s.Since:yyyy-MM-dd}");
            }
        }
    }
}
