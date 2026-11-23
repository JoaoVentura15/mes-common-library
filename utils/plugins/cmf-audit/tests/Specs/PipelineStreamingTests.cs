using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using audit.Objects.PipelineRuns;
using audit.Services;
using Moq;
using Xunit;

namespace specs;

public class PipelineStreamingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "audit-stream-test-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static List<PostTelemetry> Source(params int[] ids)
    {
        var settings = PipelineRunsSettings.From(new PipelineRunsOptions
        {
            azureDevOpsUrl = "https://dev.azure.com/example", teamProject = "Example", allWorkItemHistory = true
        }, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        return ids.SelectMany(id =>
        {
            var revision = JsonSerializer.SerializeToElement(new
            {
                id, rev = 1, fields = new Dictionary<string, object>
                {
                    ["System.WorkItemType"] = "User Story", ["System.State"] = "New",
                    ["System.ChangedDate"] = "2025-12-31T12:00:00Z"
                }
            });
            return PipelineRunsMapper.MapWorkItemHistory(revision, new() { revision }, settings);
        }).ToList();
    }
    private static int Id(List<TypedEvent> batch) => Convert.ToInt32(batch[0].Data["WorkItemId"].ToString());

    [Fact]
    public async Task UploadStartsBeforeExtractionFinishesAndPayloadIsDurableFirst()
    {
        using var checkpoint = new PipelineCheckpoint(root, "scope", false);
        var posted = Signal();
        var stream = new PipelineStreamingUpload(checkpoint, 1, 2, (batch, progress) =>
        {
            Assert.True(Directory.EnumerateFiles(Path.Combine(root, "stream-v1"), "*.json").Any());
            Assert.Equal(1, Id(batch));
            Assert.Equal(-1, progress.TotalBatches);
            posted.TrySetResult();
            return Task.CompletedTask;
        }, TimeSpan.Zero);
        var result = await stream.RunAsync(async (emit, cancellation) =>
        {
            await emit(Source(1));
            await posted.Task.WaitAsync(Timeout, cancellation); // Would deadlock with collect-then-upload.
        }).WaitAsync(Timeout);
        Assert.Equal(1, result.TypedEvents);
        Assert.True(File.Exists(Path.Combine(root, "stream-v1", "complete.json")));
    }

    [Fact]
    public async Task InterruptedExtractionResumesInDifferentOrderWithoutRepostingAcknowledgedEvents()
    {
        var ids = new ConcurrentBag<int>();
        var acknowledged = Signal();
        using (var checkpoint = new PipelineCheckpoint(root, "scope", false))
        {
            var stream = new PipelineStreamingUpload(checkpoint, 1, 1, (batch, _) =>
            {
                ids.Add(Id(batch)); acknowledged.TrySetResult(); return Task.CompletedTask;
            }, TimeSpan.Zero);
            await Assert.ThrowsAsync<IOException>(() => stream.RunAsync(async (emit, cancellation) =>
            {
                await emit(Source(1));
                await acknowledged.Task.WaitAsync(Timeout, cancellation);
                throw new IOException("failed ADO page");
            }));
        }
        Assert.False(File.Exists(Path.Combine(root, "stream-v1", "complete.json")));
        using (var checkpoint = new PipelineCheckpoint(root, "scope", true))
        {
            var stream = new PipelineStreamingUpload(checkpoint, 1, 3, (batch, _) =>
            { ids.Add(Id(batch)); return Task.CompletedTask; }, TimeSpan.Zero);
            var result = await stream.RunAsync(async (emit, _) => await emit(Source(3, 1, 2)));
            Assert.Equal(3, result.SourceEvents);
            Assert.Equal(3, result.TypedEvents);
        }
        Assert.Equal(new[] { 1, 2, 3 }, ids.OrderBy(i => i));
        using (var checkpoint = new PipelineCheckpoint(root, "scope", true))
        {
            var stream = new PipelineStreamingUpload(checkpoint, 1, 2,
                (_, _) => throw new Exception("must not upload acknowledged payloads"), TimeSpan.Zero);
            await stream.RunAsync((_, _) => throw new Exception("must not recollect a sealed extraction"));
        }
    }

    [Fact]
    public async Task UncertainAcknowledgementReplaysTheExactFrozenPayload()
    {
        string? accepted = null;
        using (var checkpoint = new PipelineCheckpoint(root, "scope", false))
        {
            var stream = new PipelineStreamingUpload(checkpoint, 1, 1, (batch, _) =>
            {
                accepted = JsonSerializer.Serialize(batch);
                throw new IOException("accepted remotely, acknowledgement lost");
            }, TimeSpan.Zero);
            await Assert.ThrowsAsync<IOException>(() => stream.RunAsync(async (emit, _) => await emit(Source(1))));
        }
        using (var checkpoint = new PipelineCheckpoint(root, "scope", true))
        {
            var stream = new PipelineStreamingUpload(checkpoint, 1, 1, (batch, _) =>
            {
                Assert.Equal(accepted, JsonSerializer.Serialize(batch));
                return Task.CompletedTask;
            }, TimeSpan.Zero);
            await stream.RunAsync(async (emit, _) => await emit(Source(1)));
        }
    }

    [Fact]
    public async Task UploadFailureUnblocksAProducerOnTheBoundedQueue()
    {
        using var checkpoint = new PipelineCheckpoint(root, "scope", false);
        var stream = new PipelineStreamingUpload(checkpoint, 1, 1,
            (_, _) => throw new IOException("publisher failed"), TimeSpan.Zero);
        await Assert.ThrowsAsync<IOException>(() => stream.RunAsync(async (emit, cancellation) =>
        {
            foreach (var item in Source(Enumerable.Range(1, 100).ToArray())) await emit(new[] { item });
        }).WaitAsync(Timeout));
        Assert.False(File.Exists(Path.Combine(root, "stream-v1", "complete.json")));
    }

    [Fact]
    public async Task StreamingUploadConcurrencyIsBounded()
    {
        using var checkpoint = new PipelineCheckpoint(root, "scope", false);
        var release = Signal(); var full = Signal();
        var active = 0; var peak = 0;
        var stream = new PipelineStreamingUpload(checkpoint, 1, 3, async (_, _) =>
        {
            var n = Interlocked.Increment(ref active);
            InterlockedExtensions.Max(ref peak, n);
            if (n == 3) full.TrySetResult();
            try { await release.Task.WaitAsync(Timeout); }
            finally { Interlocked.Decrement(ref active); }
        }, TimeSpan.Zero);
        var run = stream.RunAsync(async (emit, _) => await emit(Source(Enumerable.Range(1, 12).ToArray())));
        try { await full.Task.WaitAsync(Timeout); }
        finally { release.TrySetResult(); }
        var result = await run.WaitAsync(Timeout);
        Assert.Equal(3, peak);
        Assert.Equal(12, result.TypedEvents);
    }

    [Fact]
    public async Task CorruptSpoolIsRejectedBeforeAnyUpload()
    {
        using (var checkpoint = new PipelineCheckpoint(root, "scope", false))
        {
            var stream = new PipelineStreamingUpload(checkpoint, 1, 1, (_, _) => Task.CompletedTask, TimeSpan.Zero);
            await stream.RunAsync(async (emit, _) => await emit(Source(1, 2)));
        }
        File.Delete(Path.Combine(root, "stream-v1", "0000000000.json"));
        using var resumed = new PipelineCheckpoint(root, "scope", true);
        var upload = new PipelineStreamingUpload(resumed, 1, 1,
            (_, _) => throw new Exception("must not post corrupt checkpoint"), TimeSpan.Zero);
        await Assert.ThrowsAsync<InvalidDataException>(() => upload.RunAsync((_, _) => throw new Exception("must not collect")));
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("receipt")]
    [InlineData("seal")]
    public async Task MutatedPayloadReceiptOrSealIsRejectedBeforePublication(string mutation)
    {
        using (var checkpoint = new PipelineCheckpoint(root, "scope", false))
        {
            var stream = new PipelineStreamingUpload(checkpoint, 1, 1, (_, _) => Task.CompletedTask, TimeSpan.Zero);
            await stream.RunAsync(async (emit, _) => await emit(Source(1, 2)));
        }
        if (mutation == "payload")
        {
            var file = Path.Combine(root, "stream-v1", "0000000000.json");
            var before = File.ReadAllText(file);
            Assert.Contains("New", before);
            File.WriteAllText(file, before.Replace("New", "Changed"));
        }
        else if (mutation == "seal")
            File.Delete(Path.Combine(root, "stream-v1", "0000000001.json"));
        else
        {
            var file = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
                .First(p => File.ReadAllText(p).Contains("\"Key\":\"stream-ack:0\""));
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
            json["Value"] = "corrupt receipt";
            File.WriteAllText(file, json.ToJsonString());
        }
        using var resumed = new PipelineCheckpoint(root, "scope", true);
        var upload = new PipelineStreamingUpload(resumed, 1, 2,
            (_, _) => throw new Exception("must not post corrupt checkpoint"), TimeSpan.Zero);
        await Assert.ThrowsAsync<InvalidDataException>(() => upload.RunAsync((_, _) => throw new Exception("must not collect")));
    }

    [Fact]
    public async Task LegacyAndStreamingCheckpointModesCannotBeMixed()
    {
        using (var checkpoint = new PipelineCheckpoint(root, "scope", false)) await checkpoint.BindUploadModeAsync(false);
        using var resumed = new PipelineCheckpoint(root, "scope", true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => resumed.BindUploadModeAsync(true));
    }

    [Fact]
    public async Task OlderCheckpointCannotBeSilentlyConvertedToStreaming()
    {
        using (var checkpoint = new PipelineCheckpoint(root, "scope", false)) { }
        using var resumed = new PipelineCheckpoint(root, "scope", true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => resumed.BindUploadModeAsync(true));
        await resumed.BindUploadModeAsync(false);
    }

    [Fact]
    public async Task IndependentCollectionPhasesOverlap()
    {
        var scopeStarted = Signal();
        var ado = new Mock<IAzureDevOpsRestClient>();
        ado.Setup(a => a.QueryWorkItemIdsAsync("Example", It.IsAny<string>())).Returns(async (string _, string wiql) =>
        {
            if (wiql.Contains("System.WorkItemType")) scopeStarted.TrySetResult();
            else await scopeStarted.Task.WaitAsync(Timeout);
            return new List<int>();
        });
        var settings = PipelineRunsSettings.From(new PipelineRunsOptions
        {
            azureDevOpsUrl = "https://dev.azure.com/example", teamProject = "Example", include = "scope", allWorkItemHistory = true
        }, DateTime.UtcNow);
        var result = await new PipelineRunsCollector(ado.Object, settings).CollectAsync().WaitAsync(Timeout);
        Assert.Empty(result);
        Assert.True(scopeStarted.Task.IsCompletedSuccessfully);
    }

    private sealed class GatedHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    [Fact]
    public async Task HttpRequestLimitIsSharedAcrossGetWiqlAndLogReads()
    {
        var full = Signal(); var release = Signal(); var active = 0; var peak = 0;
        using var client = new AzureDevOpsRestClient("https://dev.azure.com/example", "test", new GatedHandler(async request =>
        {
            var n = Interlocked.Increment(ref active);
            InterlockedExtensions.Max(ref peak, n);
            if (n == 2) full.TrySetResult();
            try
            {
                await release.Task.WaitAsync(Timeout);
                var body = request.Method == HttpMethod.Post ? "{\"workItems\":[]}" : request.RequestUri!.AbsolutePath.Contains("/logs/") ? "ok\n" : "{}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            }
            finally { Interlocked.Decrement(ref active); }
        }), parallelism: 2);
        var tasks = new Task[]
        {
            client.GetTimelineAsync("Example", 1), client.GetTimelineAsync("Example", 2),
            client.QueryWorkItemIdsAsync("Example", "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project"),
            client.GetBuildLogLinesAsync("Example", 1, 1, 0, 1)
        };
        try { await full.Task.WaitAsync(Timeout); }
        finally { release.TrySetResult(); }
        await Task.WhenAll(tasks).WaitAsync(Timeout);
        Assert.Equal(2, peak);
    }

    [Fact]
    public void PerformanceFlagsDoNotInvalidateLegacyFingerprints()
    {
        var options = new PipelineRunsOptions { tenantName = "Tenant", typedEvents = true, telemetryEvents = false };
        var fingerprint = PipelineCheckpoint.Fingerprint(options);
        options.parallelism = 12; options.uploadParallelism = 4; options.concurrentCollection = false; options.streamEvents = true;
        Assert.Equal(fingerprint, PipelineCheckpoint.Fingerprint(options));
    }

    [Fact]
    public async Task ParallelLegacyReceiptsSurviveOutOfOrderFailureAndSequentialResume()
    {
        var posted = new ConcurrentBag<int>();
        var second = Signal();
        using (var checkpoint = new PipelineCheckpoint(root, "legacy", false))
        {
            await Assert.ThrowsAsync<IOException>(() => checkpoint.PostBatchesAsync("typed", new List<int> { 1, 2, 3, 4 }, 1,
                async batch =>
                {
                    posted.Add(batch[0]);
                    if (batch[0] == 1) { await second.Task.WaitAsync(Timeout); throw new IOException("batch one failed late"); }
                    if (batch[0] == 2) second.TrySetResult();
                }, TimeSpan.Zero, parallelism: 2));
        }
        using (var checkpoint = new PipelineCheckpoint(root, "legacy", true))
            await checkpoint.PostBatchesAsync("typed", new List<int> { 1, 2, 3, 4 }, 1,
                batch => { posted.Add(batch[0]); return Task.CompletedTask; }, TimeSpan.Zero);
        Assert.Equal(2, posted.Count(id => id == 1)); // The failed batch is retried.
        Assert.Equal(1, posted.Count(id => id == 2)); // A later acknowledged batch is never retried.
        Assert.Equal(new[] { 1, 2, 3, 4 }, posted.Distinct().OrderBy(id => id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NonCheckpointPostingAlsoUsesBoundedUploadConcurrency(bool typed)
    {
        var full = Signal(); var release = Signal(); var active = 0; var peak = 0; var calls = 0;
        using var http = new HttpClient(new GatedHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/oauth2/token"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"test\"}") };
            Interlocked.Increment(ref calls);
            var n = Interlocked.Increment(ref active);
            InterlockedExtensions.Max(ref peak, n);
            if (n == 3) full.TrySetResult();
            try
            {
                await release.Task.WaitAsync(Timeout);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }
            finally { Interlocked.Decrement(ref active); }
        }));
        using var service = new RestApiService(new ClientConfiguration("example.invalid", "Tenant", "MES", "test", true), http)
        { UploadParallelism = 3, BatchDelay = TimeSpan.Zero };
        var source = Source(1, 2, 3, 4, 5, 6, 7);
        var run = typed ? service.PostTypedEventsAsync(AdoTelemetryEvents.Convert(source), 2) : service.PostEventsAsync(source, 2);
        try { await full.Task.WaitAsync(Timeout); }
        finally { release.TrySetResult(); }
        await run.WaitAsync(Timeout);
        Assert.Equal(3, peak);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task StreamingPendingStepsKeepTheParentRunPartitionAcrossChunks()
    {
        var settings = PipelineRunsSettings.From(new PipelineRunsOptions
        { azureDevOpsUrl = "https://dev.azure.com/example", teamProject = "Example" }, DateTime.UtcNow);
        var build = JsonSerializer.Deserialize<JsonElement>("""{"id":7,"startTime":"2026-01-31T23:00:00Z","finishTime":"2026-01-31T23:10:00Z","definition":{"name":"CI-Build"}}""");
        var timeline = JsonSerializer.Deserialize<JsonElement>("""{"records":[{"id":"step","type":"Task","name":"Skipped task","state":"completed","result":"skipped"}]}""");
        var source = new List<PostTelemetry> { PipelineRunsMapper.MapRun(build, timeline, settings) };
        source.AddRange(PipelineRunsMapper.MapTimeline(build, timeline, settings));
        var expected = AdoTelemetryEvents.Convert(source);
        var actual = new List<TypedEvent>();
        using var checkpoint = new PipelineCheckpoint(root, "scope", false);
        var stream = new PipelineStreamingUpload(checkpoint, 1, 1, (batch, _) =>
        { actual.AddRange(batch); return Task.CompletedTask; }, TimeSpan.Zero);
        await stream.RunAsync(async (emit, _) => await emit(source));
        Assert.Equal(expected.Select(e => e.EventTime), actual.Select(e => e.EventTime));
        Assert.All(actual, e => Assert.Equal(1, e.EventTime.Month));
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int target, int value)
        {
            int before;
            do { before = Volatile.Read(ref target); if (before >= value) return; }
            while (Interlocked.CompareExchange(ref target, value, before) != before);
        }
    }
}
