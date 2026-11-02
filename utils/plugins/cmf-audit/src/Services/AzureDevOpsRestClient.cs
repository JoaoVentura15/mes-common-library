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

        public AzureDevOpsRestClient(string organizationUrl, string token, HttpMessageHandler? handler = null, bool useBearer = false)
        {
            orgUrl = organizationUrl.TrimEnd('/') + "/";
            http = handler == null ? new HttpClient() : new HttpClient(handler);
            http.Timeout = TimeSpan.FromSeconds(100);
            var credential = useBearer ? token : Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + token));
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(useBearer ? "Bearer" : "Basic", credential);
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public void Dispose() => http.Dispose();

        private static string Q(string value) => Uri.EscapeDataString(value);
        private static string Iso(DateTime value) => value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        /// <summary>GET with up to 3 retries on throttling or transient server errors.</summary>
        private async Task<(JsonElement body, string? continuation)> GetAsync(string url, bool requireFound = false)
        {
            for (var attempt = 1; ; attempt++)
            {
                using var response = await http.GetAsync(url);
                if ((response.StatusCode == (HttpStatusCode)429 || (int)response.StatusCode >= 500) && attempt < 4)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5 * attempt);
                    await Task.Delay(wait);
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
                var (body, _) = await GetAsync($"{orgUrl}{Q(project)}/_apis/wit/workitems/{workItemId}/revisions?$top={top}&$skip={skip}&api-version={ApiVersion}");
                var page = Values(body);
                all.AddRange(page);
                if (page.Count < top) return all;
            }
        }

        /// <summary>Ids returned by a WIQL query (read-only POST; WIQL answers at most 20000 ids).</summary>
        public async Task<List<int>> QueryWorkItemIdsAsync(string project, string wiql)
        {
            var url = $"{orgUrl}{Q(project)}/_apis/wit/wiql?api-version={ApiVersion}";
            for (var attempt = 1; ; attempt++)
            {
                using var content = new StringContent(JsonSerializer.Serialize(new { query = wiql }), Encoding.UTF8, "application/json");
                using var response = await http.PostAsync(url, content);
                if ((response.StatusCode == (HttpStatusCode)429 || (int)response.StatusCode >= 500) && attempt < 4)
                {
                    await Task.Delay(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5 * attempt));
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.NonAuthoritativeInformation)
                {
                    throw new UnauthorizedAccessException($"Azure DevOps denied access ({(int)response.StatusCode}) to {url.Split('?')[0]}. Check the token and its scopes.");
                }
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return doc.RootElement.TryGetProperty("workItems", out var items) && items.ValueKind == JsonValueKind.Array
                    ? items.EnumerateArray().Where(i => i.TryGetProperty("id", out _)).Select(i => i.GetProperty("id").GetInt32()).ToList()
                    : new List<int>();
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
            var (body, _) = await GetAsync($"{orgUrl}_apis/distributedtask/pools/{poolId}/jobrequests?completedRequestCount=2000&api-version={ApiVersion}");
            return Values(body);
        }
    }
}
