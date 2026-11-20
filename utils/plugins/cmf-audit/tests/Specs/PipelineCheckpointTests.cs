using audit.Objects.PipelineRuns;
using audit.Services;
using System.Net;
using System.Text.Json;
using Xunit;

namespace specs;

public class PipelineCheckpointTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "audit-checkpoint-test-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

    private class Handler(Func<HttpRequestMessage, HttpResponseMessage> read) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(read(request));
    }
    private static HttpResponseMessage Json(string body, string? token = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        if (token != null) response.Headers.Add("x-ms-continuationtoken", token);
        return response;
    }

    [Fact]
    public async Task AgentJobsReadBeyondTwoThousandAndResumeAtTheFailedPage()
    {
        var requests = new List<string>();
        var fail = true;
        var handler = new Handler(request =>
        {
            var url = request.RequestUri!.ToString();
            requests.Add(url);
            Assert.Contains("$top=1000", url);
            Assert.DoesNotContain("completedRequestCount", url);
            var offset = url.Contains("continuationToken=second") ? 1000 : url.Contains("continuationToken=third") ? 2000 : 0;
            if (offset == 1000 && fail) return new(HttpStatusCode.BadRequest) { Content = new StringContent("synthetic failure") };
            var count = offset == 2000 ? 501 : 1000;
            return Json(JsonSerializer.Serialize(new { value = Enumerable.Range(offset + 1, count).Select(requestId => new { requestId }) }),
                offset == 0 ? "second" : offset == 1000 ? "third" : null);
        });
        using (var checkpoint = new PipelineCheckpoint(root, "scope", false))
        using (var client = new AzureDevOpsRestClient("https://dev.azure.com/example", "not-saved", handler, checkpoint: checkpoint))
            await Assert.ThrowsAsync<HttpRequestException>(() => client.ListJobRequestsAsync(1));
        fail = false;
        // A fresh handler represents a new process; the successful first page must not be requested again.
        using (var checkpoint = new PipelineCheckpoint(root, "scope", true))
        using (var client = new AzureDevOpsRestClient("https://dev.azure.com/example", "renewed", new Handler(request =>
        {
            var url = request.RequestUri!.ToString();
            requests.Add(url);
            var offset = url.Contains("continuationToken=second") ? 1000 : 2000;
            Assert.Contains("continuationToken=", url);
            return Json(JsonSerializer.Serialize(new { value = Enumerable.Range(offset + 1, offset == 1000 ? 1000 : 501).Select(requestId => new { requestId }) }), offset == 1000 ? "third" : null);
        }), checkpoint: checkpoint))
        {
            var jobs = await client.ListJobRequestsAsync(1);
            Assert.Equal(2501, jobs.Count);
            Assert.Equal(2501, jobs.Select(j => j.GetProperty("requestId").GetInt32()).Distinct().Count());
        }
        Assert.Equal(4, requests.Count);
        Assert.DoesNotContain("not-saved", string.Join("", Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).Select(File.ReadAllText)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AgentPaginationRejectsRepeatedTokensAndNoProgress(bool repeatedToken)
    {
        var calls = 0;
        using var client = new AzureDevOpsRestClient("https://dev.azure.com/example", "test", new Handler(_ =>
        {
            calls++;
            return Json("{\"value\":[{\"requestId\":1}]}", calls == 1 || repeatedToken ? "same" : "different");
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ListJobRequestsAsync(1));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"value\":[{}]}")]
    public async Task AgentPaginationRejectsMalformedPages(string body)
    {
        using var client = new AzureDevOpsRestClient("https://dev.azure.com/example", "test", new Handler(_ => Json(body)));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ListJobRequestsAsync(1));
    }

    [Fact]
    public async Task OnlyAcknowledgedBatchesAreSkippedAfterRestart()
    {
        var posts = new List<int>();
        using (var checkpoint = new PipelineCheckpoint(root, "destination-and-options", false))
        {
            await Assert.ThrowsAsync<IOException>(() => checkpoint.PostBatchesAsync("typed", new List<int> { 1, 2, 3 }, 1, batch =>
            {
                posts.Add(batch[0]);
                if (batch[0] == 2) throw new IOException("synthetic disconnect");
                return Task.CompletedTask;
            }, TimeSpan.Zero));
        }
        using (var checkpoint = new PipelineCheckpoint(root, "destination-and-options", true))
        {
            await checkpoint.PostBatchesAsync("typed", new List<int> { 1, 2, 3 }, 1, batch => { posts.Add(batch[0]); return Task.CompletedTask; }, TimeSpan.Zero);
            await Assert.ThrowsAsync<InvalidDataException>(() => checkpoint.PostBatchesAsync("typed", new List<int> { 99 }, 1, _ => throw new Exception("must not post"), TimeSpan.Zero));
        }
        Assert.Equal(new[] { 1, 2, 2, 3 }, posts);
    }

    [Fact]
    public async Task FinishedExtractionRoundTripsBeforePostingAndSkipsRecollection()
    {
        var settings = PipelineRunsSettings.From(new PipelineRunsOptions { azureDevOpsUrl = "https://dev.azure.com/example/", teamProject = "Example", allWorkItemHistory = true }, DateTime.UtcNow);
        var revision = JsonSerializer.Deserialize<JsonElement>("{\"id\":42,\"rev\":1,\"fields\":{\"System.State\":\"New\",\"System.ChangedDate\":\"2026-01-01T00:00:00Z\"}}");
        var events = PipelineRunsMapper.MapWorkItemHistory(revision, new() { revision }, settings);
        string original;
        using (var checkpoint = new PipelineCheckpoint(root, "scope", false))
        {
            var saved = await checkpoint.GetOrCreateAsync("collected-events", () => Task.FromResult(events));
            original = JsonSerializer.Serialize(AdoTelemetryEvents.Convert(saved));
        }
        using (var checkpoint = new PipelineCheckpoint(root, "scope", true))
        {
            var saved = await checkpoint.GetOrCreateAsync<List<PostTelemetry>>("collected-events", () => throw new Exception("must not recollect"));
            Assert.Equal(original, JsonSerializer.Serialize(AdoTelemetryEvents.Convert(saved)));
        }
    }

    [Fact]
    public async Task RevisionPagesAndWiqlQueriesAreReusedWithRenewedCredentials()
    {
        using (var checkpoint = new PipelineCheckpoint(root, "scope", false))
        using (var client = new AzureDevOpsRestClient("https://dev.azure.com/example", "old", new Handler(request =>
        {
            if (request.Method == HttpMethod.Post) return Json("{\"workItems\":[]}");
            return Json("{\"value\":[{\"id\":42,\"rev\":1}]}");
        }), checkpoint: checkpoint))
        {
            Assert.Single(await client.GetWorkItemRevisionsAsync("Example", 42));
            Assert.Empty(await client.QueryWorkItemIdsAsync("Example", "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project"));
        }
        using (var checkpoint = new PipelineCheckpoint(root, "scope", true))
        using (var client = new AzureDevOpsRestClient("https://dev.azure.com/example", "renewed", new Handler(_ => throw new Exception("must not call ADO")), checkpoint: checkpoint))
        {
            Assert.Single(await client.GetWorkItemRevisionsAsync("Example", 42));
            Assert.Empty(await client.QueryWorkItemIdsAsync("Example", "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project"));
        }
    }

    [Fact]
    public async Task FailedReadsAreNotCachedAndCorruptedEntriesFailClosed()
    {
        using var checkpoint = new PipelineCheckpoint(root, "scope", false);
        await Assert.ThrowsAsync<IOException>(() => checkpoint.GetOrCreateAsync<int>("request", () => throw new IOException()));
        Assert.Equal(42, await checkpoint.GetOrCreateAsync("request", () => Task.FromResult(42)));
        var entry = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).Single(p => Path.GetFileName(p) != "manifest.json");
        File.WriteAllText(entry, "truncated");
        await Assert.ThrowsAsync<JsonException>(() => checkpoint.GetOrCreateAsync("request", () => Task.FromResult(99)));
    }

    [Fact]
    public void ScopeMismatchAndConcurrentUseAreRejected()
    {
        using (var checkpoint = new PipelineCheckpoint(root, "original", false))
            Assert.Throws<IOException>(() => new PipelineCheckpoint(root, "original", true));
        Assert.Throws<InvalidOperationException>(() => new PipelineCheckpoint(root, "different", true));
        Assert.Throws<InvalidOperationException>(() => new PipelineCheckpoint(root, "original", false));
        using var resumed = new PipelineCheckpoint(root, "original", true);
    }

    [Fact]
    public void FingerprintExcludesCredentialsButIncludesDestinationAndFilters()
    {
        var options = new PipelineRunsOptions { personalAccessToken = "old", securityAccessToken = "old", tenantName = "Tenant", repositories = "Repo" };
        var first = PipelineCheckpoint.Fingerprint(options);
        options.personalAccessToken = "new"; options.securityAccessToken = "new"; options.resume = true;
        Assert.Equal(first, PipelineCheckpoint.Fingerprint(options));
        options.tenantName = "Other";
        Assert.NotEqual(first, PipelineCheckpoint.Fingerprint(options));
        options.tenantName = "Tenant"; options.repositories = "Different";
        Assert.NotEqual(first, PipelineCheckpoint.Fingerprint(options));
    }
}
