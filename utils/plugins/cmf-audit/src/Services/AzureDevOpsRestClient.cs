using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace audit.Services
{
    /// <summary>
    /// Read-only Azure DevOps REST client for the pipeline-runs auditor. Returns raw JSON so the
    /// mapping does not depend on SDK model versions, and handles continuation tokens and retries.
    /// </summary>
    public interface IAzureDevOpsRestClient
    {
        Task<List<JsonElement>> ListBuildsAsync(string project, DateTime since, string? repositoryId);
        Task<JsonElement?> GetTimelineAsync(string project, int buildId);
        Task<IReadOnlyList<string>?> GetBuildLogLinesAsync(string project, int buildId, int logId, int startLine, int endLine) => throw new NotSupportedException();
        Task<JsonElement?> GetBuildAsync(string project, int buildId) => Task.FromResult<JsonElement?>(null);
        Task<string?> GetProjectIdAsync(string project) => Task.FromResult<string?>(null);
        Task<List<JsonElement>> GetPullRequestPoliciesAsync(string project, string projectId, int pullRequestId) => throw new NotSupportedException();
        Task<List<JsonElement>> GetBuildChangesAsync(string project, int buildId);
        Task<List<JsonElement>> GetTestRunsAsync(string project, string buildUri);
        Task<List<JsonElement>> GetFailedTestResultsAsync(string project, int testRunId);
        Task<List<JsonElement>> GetApprovalsAsync(string project, IEnumerable<string> approvalIds);
        Task<JsonElement?> GetRepositoryAsync(string project, string nameOrId);
        Task<List<JsonElement>> ListRepositoriesAsync(string project);
        Task<JsonElement?> GetJsonFileAsync(string project, string repositoryId, string path, string commitId);
        Task<List<JsonElement>> ListPullRequestsAsync(string project, string repositoryId, DateTime since, string timeRangeType);
        Task<List<JsonElement>> ListActivePullRequestsAsync(string project, string repositoryId);
        Task<List<JsonElement>> GetPullRequestWorkItemRefsAsync(string project, string repositoryId, int pullRequestId);
        Task<List<JsonElement>> GetWorkItemsAsync(string project, IEnumerable<int> ids, IEnumerable<string>? fields = null);
        /// <summary>Work items with their relations (links), used to find the pull requests a work item is linked to.</summary>
        Task<List<JsonElement>> GetWorkItemRelationsAsync(string project, IEnumerable<int> ids);
        Task<JsonElement?> GetPullRequestAsync(string project, string repositoryId, int pullRequestId);
        Task<List<JsonElement>> GetPullRequestThreadsAsync(string project, string repositoryId, int pullRequestId);
        Task<List<JsonElement>> GetPullRequestIterationsAsync(string project, string repositoryId, int pullRequestId);
        Task<List<JsonElement>> GetWorkItemRevisionsAsync(string project, int workItemId);
        Task<List<int>> QueryWorkItemIdsAsync(string project, string wiql);
        Task<List<JsonElement>> ListTagsAsync(string project, string repositoryId);
        Task<JsonElement?> GetAnnotatedTagAsync(string project, string repositoryId, string objectId);
        Task<JsonElement?> GetCommitAsync(string project, string repositoryId, string commitId);
        Task<List<JsonElement>> ListRefPushesAsync(string project, string repositoryId, string refName);
        Task<List<JsonElement>> GetCommitChangesAsync(string project, string repositoryId, string commitId);
        Task<JsonElement?> GetIterationsAsync(string project);
        Task<List<JsonElement>> ListPoolsAsync();
        Task<List<JsonElement>> ListJobRequestsAsync(int poolId);
    }

    public class AzureDevOpsRestClient : IAzureDevOpsRestClient, IDisposable
    {
        private const string ApiVersion = "7.1";
        private readonly HttpClient http;
        private readonly string orgUrl;
        private readonly PipelineCheckpoint? checkpoint;
        private readonly SemaphoreSlim requests;
        private readonly CancellationToken cancellationToken;

        public AzureDevOpsRestClient(string organizationUrl, string token, HttpMessageHandler? handler = null, bool useBearer = false, PipelineCheckpoint? checkpoint = null, int parallelism = 8, CancellationToken cancellationToken = default)
        {
            this.checkpoint = checkpoint;
            requests = new SemaphoreSlim(Math.Max(1, parallelism));
            this.cancellationToken = cancellationToken;
            orgUrl = organizationUrl.TrimEnd('/') + "/";
            http = handler == null ? new HttpClient() : new HttpClient(handler);
            http.Timeout = TimeSpan.FromSeconds(100);
            var credential = useBearer ? token : Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + token));
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(useBearer ? "Bearer" : "Basic", credential);
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public void Dispose() { http.Dispose(); requests.Dispose(); }

        private sealed class RequestLease(SemaphoreSlim semaphore) : IDisposable
        {
            private SemaphoreSlim? owner = semaphore;
            public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release();
        }
        private async Task<IDisposable> EnterRequestAsync()
        {
            await requests.WaitAsync(cancellationToken);
            return new RequestLease(requests);
        }

        private static string Q(string value) => Uri.EscapeDataString(value);
        private static string Iso(DateTime value) => value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        /// <summary>GET with up to 3 retries on throttling or transient server errors.</summary>
        private async Task<(JsonElement body, string? continuation)> GetAsync(string url, bool requireFound = false)
        {
            if (checkpoint == null) return await GetUncachedAsync(url, requireFound);
            var saved = await checkpoint.GetOrCreateAsync("GET:" + requireFound + ":" + url, async () =>
            {
                var (body, continuation) = await GetUncachedAsync(url, requireFound);
                return new CheckpointResponse(body.ValueKind == JsonValueKind.Undefined ? JsonSerializer.SerializeToElement<object?>(null) : body, continuation);
            });
            return (saved.Body, saved.Continuation);
        }

        private record CheckpointResponse(JsonElement Body, string? Continuation);

        private async Task<(JsonElement body, string? continuation)> GetUncachedAsync(string url, bool requireFound = false)
        {
            for (var attempt = 1; ; attempt++)
            {
                using var requestLease = await EnterRequestAsync();
                using var response = await http.GetAsync(url, cancellationToken);
                if ((response.StatusCode == (HttpStatusCode)429 || (int)response.StatusCode >= 500) && attempt < 4)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5 * attempt);
                    requestLease.Dispose();
                    await Task.Delay(wait, cancellationToken);
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    if (requireFound)
                    {
                        throw new HttpRequestException($"Azure DevOps answered 404 (not found) for {url.Split('?')[0]}. Check the organization, project and repository names.", null, HttpStatusCode.NotFound);
                    }
                    return (default, null);
                }
                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.NonAuthoritativeInformation)
                {
                    throw new UnauthorizedAccessException($"Azure DevOps denied access ({(int)response.StatusCode}) to {url.Split('?')[0]}. Check the token and its scopes.");
                }
                response.EnsureSuccessStatusCode();
                var text = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(text))
                {
                    // some endpoints (e.g. the timeline of a run that never started) answer 200/204 with no body
                    return (default, null);
                }
                using var doc = JsonDocument.Parse(text);
                string? continuation = response.Headers.TryGetValues("x-ms-continuationtoken", out var values) ? values.FirstOrDefault() : null;
                return (doc.RootElement.Clone(), continuation);
            }
        }

        private static List<JsonElement> Values(JsonElement body) =>
            body.ValueKind == JsonValueKind.Object && body.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Select(e => e.Clone()).ToList()
                : new List<JsonElement>();

        /// <summary>Follows x-ms-continuationtoken until the last page.</summary>
        private async Task<List<JsonElement>> GetAllAsync(string url, bool requireFound = false)
        {
            var all = new List<JsonElement>();
            string? continuation = null;
            do
            {
                var page = continuation == null ? url : $"{url}&continuationToken={Q(continuation)}";
                var (body, next) = await GetAsync(page, requireFound);
                all.AddRange(Values(body));
                continuation = next;
            } while (!string.IsNullOrEmpty(continuation));
            return all;
        }

        public Task<List<JsonElement>> ListBuildsAsync(string project, DateTime since, string? repositoryId)
        {
            var url = $"{orgUrl}{Q(project)}/_apis/build/builds?minTime={Q(Iso(since))}&queryOrder=finishTimeAscending&statusFilter=completed&$top=500&api-version={ApiVersion}";
            if (!string.IsNullOrEmpty(repositoryId))
            {
                url += $"&repositoryId={Q(repositoryId)}&repositoryType=TfsGit";
            }
            return GetAllAsync(url, requireFound: true);
        }

        public async Task<JsonElement?> GetTimelineAsync(string project, int buildId)
        {
            var (body, _) = await GetAsync($"{orgUrl}{Q(project)}/_apis/build/builds/{buildId}/timeline?api-version={ApiVersion}");
            return body.ValueKind == JsonValueKind.Object ? body : null;
        }

        /// <summary>Reads only the trusted build/log route, never a URL supplied by a log. Pages are bounded to 2 MiB of text.</summary>
        public async Task<IReadOnlyList<string>?> GetBuildLogLinesAsync(string project, int buildId, int logId, int startLine, int endLine)
        {
            if (buildId <= 0 || logId <= 0 || startLine < 0 || endLine < startLine || endLine - startLine >= 2000)
                throw new ArgumentOutOfRangeException(nameof(startLine));
            var url = $"{orgUrl}{Q(project)}/_apis/build/builds/{buildId}/logs/{logId}?startLine={startLine}&endLine={endLine}&api-version={ApiVersion}";
            for (var attempt = 1; ; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
                using var requestLease = await EnterRequestAsync();
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if ((response.StatusCode == (HttpStatusCode)429 || (int)response.StatusCode >= 500) && attempt < 4)
                {
                    requestLease.Dispose();
                    await Task.Delay(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5 * attempt), cancellationToken);
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.NotFound) return null;
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NonAuthoritativeInformation)
                    throw new UnauthorizedAccessException("Azure DevOps denied access to a build log; check Build: Read.");
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentType?.MediaType is "text/html" or "application/zip")
                    throw new InvalidDataException("Unexpected build log response format.");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(http.Timeout);
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
                var buffer = new char[8192];
                var text = new StringBuilder();
                int read;
                while ((read = await reader.ReadAsync(buffer.AsMemory(), timeout.Token)) > 0)
                {
                    if (text.Length + read > 2 * 1024 * 1024) throw new InvalidDataException("Build log page exceeds the safe text limit.");
                    text.Append(buffer, 0, read);
                }
                var content = text.ToString();
                if (content.TrimStart().StartsWith("<html", StringComparison.OrdinalIgnoreCase) || content.TrimStart().StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Unexpected login page instead of a build log.");
                if (response.Content.Headers.ContentType?.MediaType == "application/json")
                {
                    using var doc = JsonDocument.Parse(content);
                    if (doc.RootElement.ValueKind == JsonValueKind.String) content = doc.RootElement.GetString() ?? "";
                    else
                    {
                        var array = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
                            : doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("value", out var value) ? value : default;
                        if (array.ValueKind != JsonValueKind.Array || array.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
                            throw new InvalidDataException("Unexpected JSON build log response.");
                        return array.EnumerateArray().Select(v => v.GetString() ?? "").ToList();
                    }
                }
                if (content.Length == 0) return Array.Empty<string>();
                var lines = content.Replace("\r\n", "\n").Split('\n').ToList();
                if (lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
                return lines;
            }
        }

        public async Task<JsonElement?> GetBuildAsync(string project, int buildId)
        {
            var (body, _) = await GetAsync($"{orgUrl}{Q(project)}/_apis/build/builds/{buildId}?api-version={ApiVersion}");
            return body.ValueKind == JsonValueKind.Object ? body : null;
        }

        public async Task<string?> GetProjectIdAsync(string project)
        {
            var (body, _) = await GetAsync($"{orgUrl}_apis/projects/{Q(project)}?api-version={ApiVersion}", requireFound: true);
            return body.ValueKind == JsonValueKind.Object && body.TryGetProperty("id", out var id) ? id.GetString() : null;
        }

        // https://learn.microsoft.com/rest/api/azure/devops/policy/evaluations/list
        public async Task<List<JsonElement>> GetPullRequestPoliciesAsync(string project, string projectId, int pullRequestId)
        {
            if (!Guid.TryParse(projectId, out _) || pullRequestId <= 0) throw new ArgumentException("A project GUID and PR id are required.");
            var artifact = $"vstfs:///CodeReview/CodeReviewId/{projectId}/{pullRequestId}";
            var all = new List<JsonElement>();
            const int top = 100;
            for (var skip = 0; ; skip += top)
            {
                var (body, _) = await GetAsync($"{orgUrl}{Q(project)}/_apis/policy/evaluations?artifactId={Q(artifact)}&includeNotApplicable=true&$top={top}&$skip={skip}&api-version=7.1-preview.1", requireFound: true);
                if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("value", out var entries) || entries.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Policy response was incomplete.");
                var page = Values(body);
                if (skip >= 10000) throw new InvalidDataException("Policy pagination exceeded the safety bound.");
                all.AddRange(page);
                if (page.Count < top) return all;
            }
        }

        public async Task<List<JsonElement>> GetBuildChangesAsync(string project, int buildId)
        {
            var (body, _) = await GetAsync($"{orgUrl}{Q(project)}/_apis/build/builds/{buildId}/changes?$top=200&api-version={ApiVersion}");
            return Values(body);
        }

        public Task<List<JsonElement>> GetTestRunsAsync(string project, string buildUri) =>
            GetAllAsync($"{orgUrl}{Q(project)}/_apis/test/runs?buildUri={Q(buildUri)}&includeRunDetails=true&api-version={ApiVersion}");

        public async Task<List<JsonElement>> GetFailedTestResultsAsync(string project, int testRunId)
        {
            var all = new List<JsonElement>();
            const int top = 1000;
            for (var skip = 0; ; skip += top)
            {
                var (body, _) = await GetAsync($"{orgUrl}{Q(project)}/_apis/test/Runs/{testRunId}/results?outcomes=Failed,Aborted,Timeout,Error&$top={top}&$skip={skip}&api-version={ApiVersion}");
                var page = Values(body);
                all.AddRange(page);
                if (page.Count < top)
                {
                    return all;
                }
            }
        }

        public async Task<List<JsonElement>> GetApprovalsAsync(string project, IEnumerable<string> approvalIds)
        {
            var ids = approvalIds.Distinct().ToList();
            if (ids.Count == 0)
            {
                return new List<JsonElement>();
            }
            var (body, _) = await GetAsync($"{orgUrl}{Q(project)}/_apis/pipelines/approvals?approvalIds={Q(string.Join(",", ids))}&$expand=steps&api-version={ApiVersion}");
            return Values(body);
        }

        public async Task<JsonElement?> GetRepositoryAsync(string project, string nameOrId)
        {
            var (body, _) = await GetAsync($"{orgUrl}{Q(project)}/_apis/git/repositories/{Q(nameOrId)}?api-version={ApiVersion}");
            return body.ValueKind == JsonValueKind.Object ? body : null;
        }

        public Task<List<JsonElement>> ListRepositoriesAsync(string project) =>
            GetAllAsync($"{orgUrl}{Q(project)}/_apis/git/repositories?api-version={ApiVersion}", requireFound: true);

        /// <summary>Parsed JSON content of a file at a commit; null when the file is missing there or is not valid JSON.</summary>
        public async Task<JsonElement?> GetJsonFileAsync(string project, string repositoryId, string path, string commitId)
        {
            var (body, _) = await GetAsync($"{orgUrl}{Q(project)}/_apis/git/repositories/{Q(repositoryId)}/items?path={Q(path)}&versionDescriptor.versionType=commit&versionDescriptor.version={Q(commitId)}&includeContent=true&api-version={ApiVersion}");
            if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            try
            {
                using var doc = JsonDocument.Parse((content.GetString() ?? "").TrimStart((char)0xFEFF));
                return doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public async Task<List<JsonElement>> ListPullRequestsAsync(string project, string repositoryId, DateTime since, string timeRangeType)
        {
            var all = new List<JsonElement>();
            const int top = 100;
            for (var skip = 0; ; skip += top)
            {
                var (body, _) = await GetAsync($"{orgUrl}{Q(project)}/_apis/git/repositories/{Q(repositoryId)}/pullrequests?searchCriteria.status=all&searchCriteria.minTime={Q(Iso(since))}&searchCriteria.queryTimeRangeType={timeRangeType}&$top={top}&$skip={skip}&api-version={ApiVersion}", requireFound: true);
                var page = Values(body);
                all.AddRange(page);
                if (page.Count < top)
                {
                    return all;
                }
            }
        }

        public async Task<List<JsonElement>> ListActivePullRequestsAsync(string project, string repositoryId)
        {
            var all = new List<JsonElement>();
            const int top = 100;
            for (var skip = 0; ; skip += top)
            {
                var (body, _) = await GetAsync($"{orgUrl}{Q(project)}/_apis/git/repositories/{Q(repositoryId)}/pullrequests?searchCriteria.status=active&$top={top}&$skip={skip}&api-version={ApiVersion}", requireFound: true);
                var page = Values(body);
                all.AddRange(page);
                if (page.Count < top) return all;
            }
        }

        public async Task<List<JsonElement>> GetPullRequestWorkItemRefsAsync(string project, string repositoryId, int pullRequestId)
        {
            var (body, _) = await GetAsync($"{orgUrl}{Q(project)}/_apis/git/repositories/{Q(repositoryId)}/pullRequests/{pullRequestId}/workitems?api-version={ApiVersion}");
            return Values(body);
        }

        public async Task<List<JsonElement>> GetWorkItemsAsync(string project, IEnumerable<int> ids, IEnumerable<string>? fields = null)
        {
            var all = new List<JsonElement>();
            var selected = new[] { "System.AreaPath", "System.WorkItemType", "System.IterationPath", "System.TeamProject", "System.State", "System.CreatedDate", "System.ChangedDate", "Microsoft.VSTS.Common.ActivatedDate", "Microsoft.VSTS.Common.ResolvedDate", "Microsoft.VSTS.Common.ClosedDate", "Microsoft.VSTS.Common.ResolvedBy", "Microsoft.VSTS.Scheduling.StoryPoints" }
                .Concat(fields ?? Enumerable.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var chunk in ids.Distinct().Chunk(200))
            {
                var (body, _) = await GetAsync($"{orgUrl}{Q(project)}/_apis/wit/workitems?ids={string.Join(",", chunk)}&fields={Q(string.Join(",", selected))}&errorPolicy=omit&api-version={ApiVersion}");
                all.AddRange(Values(body).Where(e => e.ValueKind == JsonValueKind.Object));
            }
            return all;
        }

        public async Task<List<JsonElement>> GetWorkItemRelationsAsync(string project, IEnumerable<int> ids)
        {
            var all = new List<JsonElement>();
            foreach (var chunk in ids.Distinct().Chunk(200))
            {
                var (body, _) = await GetAsync($"{orgUrl}{Q(project)}/_apis/wit/workitems?ids={string.Join(",", chunk)}&$expand=relations&errorPolicy=omit&api-version={ApiVersion}");
                all.AddRange(Values(body).Where(e => e.ValueKind == JsonValueKind.Object));
            }
            return all;
        }

        public async Task<JsonElement?> GetPullRequestAsync(string project, string repositoryId, int pullRequestId)
        {
            var (body, _) = await GetAsync($"{orgUrl}{Q(project)}/_apis/git/repositories/{Q(repositoryId)}/pullrequests/{pullRequestId}?api-version={ApiVersion}");
            return body.ValueKind == JsonValueKind.Object ? body : null;
        }

        public Task<List<JsonElement>> GetPullRequestThreadsAsync(string project, string repositoryId, int pullRequestId) =>
            GetAllAsync($"{orgUrl}{Q(project)}/_apis/git/repositories/{Q(repositoryId)}/pullRequests/{pullRequestId}/threads?api-version={ApiVersion}");

        public Task<List<JsonElement>> GetPullRequestIterationsAsync(string project, string repositoryId, int pullRequestId) =>
            GetAllAsync($"{orgUrl}{Q(project)}/_apis/git/repositories/{Q(repositoryId)}/pullRequests/{pullRequestId}/iterations?api-version={ApiVersion}");

        public async Task<List<JsonElement>> GetWorkItemRevisionsAsync(string project, int workItemId)
        {
            var all = new List<JsonElement>();
            const int top = 200;
            for (var skip = 0; ; skip += top)
            {
                var (body, _) = await GetAsync($"{orgUrl}{Q(project)}/_apis/wit/workitems/{workItemId}/revisions?$top={top}&$skip={skip}&api-version={ApiVersion}", requireFound: true);
                var page = Values(body);
                all.AddRange(page);
                if (page.Count < top) return all;
            }
        }

        /// <summary>Enumerates flat work-item queries in ID order with bounded pages, avoiding WIQL's 20000-result limit.</summary>
        public async Task<List<int>> QueryWorkItemIdsAsync(string project, string wiql)
        {
            var match = System.Text.RegularExpressions.Regex.Match(wiql,
                @"\A\s*SELECT\s+\[System\.Id\]\s+FROM\s+WorkItems\s+WHERE\s+(.+?)(?:\s+ORDER\s+BY\s+.+)?\s*\z",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
            if (!match.Success) throw new ArgumentException("Expected a flat SELECT [System.Id] FROM WorkItems WHERE query", nameof(wiql));
            var all = new List<int>();
            var cursor = 0;
            while (true)
            {
                var query = $"SELECT [System.Id] FROM WorkItems WHERE ({match.Groups[1].Value}) AND [System.Id] > {cursor} ORDER BY [System.Id] ASC";
                var page = await QueryWorkItemIdPageAsync(project, query);
                if (page.Count == 0) return all;
                if (page.Any(id => id <= cursor) || page.Distinct().Count() != page.Count)
                    throw new InvalidDataException("WIQL paging did not advance or returned duplicate IDs; refusing incomplete results");
                all.AddRange(page.OrderBy(id => id));
                cursor = page.Max();
            }
        }

        private async Task<List<int>> QueryWorkItemIdPageAsync(string project, string wiql)
        {
            if (checkpoint == null) return await QueryWorkItemIdPageUncachedAsync(project, wiql);
            return await checkpoint.GetOrCreateAsync("WIQL:" + project + ":" + wiql,
                () => QueryWorkItemIdPageUncachedAsync(project, wiql));
        }

        private async Task<List<int>> QueryWorkItemIdPageUncachedAsync(string project, string wiql)
        {
            var url = $"{orgUrl}{Q(project)}/_apis/wit/wiql?$top=10000&api-version={ApiVersion}";
            for (var attempt = 1; ; attempt++)
            {
                using var content = new StringContent(JsonSerializer.Serialize(new { query = wiql }), Encoding.UTF8, "application/json");
                using var requestLease = await EnterRequestAsync();
                using var response = await http.PostAsync(url, content, cancellationToken);
                if ((response.StatusCode == (HttpStatusCode)429 || (int)response.StatusCode >= 500) && attempt < 4)
                {
                    requestLease.Dispose();
                    await Task.Delay(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5 * attempt), cancellationToken);
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.NonAuthoritativeInformation)
                {
                    throw new UnauthorizedAccessException($"Azure DevOps denied access ({(int)response.StatusCode}) to {url.Split('?')[0]}. Check the token and its scopes.");
                }
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (!doc.RootElement.TryGetProperty("workItems", out var items) || items.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("WIQL did not return a flat workItems array; refusing incomplete results");
                return items.EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToList();
            }
        }

        /// <summary>Tag refs of a repository (name, objectId, peeledObjectId for annotated tags).</summary>
        public Task<List<JsonElement>> ListTagsAsync(string project, string repositoryId) =>
            GetAllAsync($"{orgUrl}{Q(project)}/_apis/git/repositories/{Q(repositoryId)}/refs?filter=tags/&peelTags=true&$top=1000&api-version={ApiVersion}");

        public async Task<JsonElement?> GetAnnotatedTagAsync(string project, string repositoryId, string objectId)
        {
            var (body, _) = await GetAsync($"{orgUrl}{Q(project)}/_apis/git/repositories/{Q(repositoryId)}/annotatedtags/{Q(objectId)}?api-version={ApiVersion}");
            return body.ValueKind == JsonValueKind.Object ? body : null;
        }

        /// <summary>Pushes that touched a ref, newest first. The date of a push that created a tag is when the tag was created.</summary>
        public async Task<List<JsonElement>> ListRefPushesAsync(string project, string repositoryId, string refName)
        {
            var (body, _) = await GetAsync($"{orgUrl}{Q(project)}/_apis/git/repositories/{Q(repositoryId)}/pushes?searchCriteria.refName={Q(refName)}&$top=10&api-version={ApiVersion}");
            return Values(body);
        }

        public async Task<JsonElement?> GetCommitAsync(string project, string repositoryId, string commitId)
        {
            var (body, _) = await GetAsync($"{orgUrl}{Q(project)}/_apis/git/repositories/{Q(repositoryId)}/commits/{Q(commitId)}?api-version={ApiVersion}");
            return body.ValueKind == JsonValueKind.Object ? body : null;
        }

        /// <summary>Files changed by a commit (against its first parent), paged 1000 at a time.</summary>
        public async Task<List<JsonElement>> GetCommitChangesAsync(string project, string repositoryId, string commitId)
        {
            var all = new List<JsonElement>();
            for (var skip = 0; skip < 20000; skip += 1000)
            {
                var (body, _) = await GetAsync($"{orgUrl}{Q(project)}/_apis/git/repositories/{Q(repositoryId)}/commits/{Q(commitId)}/changes?top=1000&skip={skip}&api-version={ApiVersion}");
                var page = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("changes", out var c) && c.ValueKind == JsonValueKind.Array
                    ? c.EnumerateArray().Select(e => e.Clone()).ToList()
                    : new List<JsonElement>();
                all.AddRange(page);
                if (page.Count < 1000) break;
            }
            return all;
        }

        public async Task<JsonElement?> GetIterationsAsync(string project)
        {
            var (body, _) = await GetAsync($"{orgUrl}{Q(project)}/_apis/wit/classificationnodes/iterations?$depth=14&api-version={ApiVersion}");
            return body.ValueKind == JsonValueKind.Object ? body : null;
        }

        public async Task<List<JsonElement>> ListPoolsAsync()
        {
            var (body, _) = await GetAsync($"{orgUrl}_apis/distributedtask/pools?api-version={ApiVersion}");
            return Values(body);
        }

        public async Task<List<JsonElement>> ListJobRequestsAsync(int poolId)
        {
            var all = new Dictionary<long, JsonElement>();
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            string? token = null;
            do
            {
                var url = $"{orgUrl}_apis/distributedtask/pools/{poolId}/jobrequests?$top=1000&api-version=7.1-preview.1";
                if (token != null) url += "&continuationToken=" + Q(token);
                var (body, next) = await GetAsync(url, requireFound: true);
                if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("value", out var items) || items.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Agent job page did not contain a value array");
                // ADO can retain an empty cursor header on the terminal empty page.
                // Accept only that observed sentinel, not arbitrary stalled pagination.
                if (items.GetArrayLength() == 0 && next == "||") break;
                var added = 0;
                foreach (var item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("requestId", out var id) || !id.TryGetInt64(out var requestId) || requestId <= 0)
                        throw new InvalidDataException("Agent job page contains an invalid request ID");
                    if (all.TryAdd(requestId, item.Clone())) added++;
                }
                if (!string.IsNullOrEmpty(next) && (!tokens.Add(next) || added == 0))
                    throw new InvalidDataException("Agent job pagination repeated a token or made no progress");
                token = next;
            } while (!string.IsNullOrEmpty(token));
            return all.Values.ToList();
        }
    }
}
