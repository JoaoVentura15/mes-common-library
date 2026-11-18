using audit.Objects.PipelineRuns;
using audit.Services;
using System.Text.Json;
using Xunit;
using Moq;
using System.Net;
using static audit.Objects.PipelineRuns.PipelineRunsMapper;

namespace specs;

public class FailureEvidenceTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static PipelineRunsSettings Settings => new() { Organization = "org", Project = "project" };
    private static FailureEvidence Scan(params string[] lines)
    {
        var scanner = new FailureLogScanner();
        for (var i = 0; i < lines.Length; i++) scanner.Observe(lines[i], i + 1);
        return scanner.Finish("Complete", 17, "Bash exited with code '1'.");
    }

    [Theory]
    [InlineData("npm ERR! 401 Unauthorized - GET https://registry.example.test/package", "HTTP401", "Authentication / authorization")]
    [InlineData("error NU1301: Response status code does not indicate success: 401 (Unauthorized).", "HTTP401", "Authentication / authorization")]
    [InlineData("npm error code ECONNRESET", "ECONNRESET", "Connection failure")]
    [InlineData("error CS0246: The type or namespace name could not be found", "CS0246", "Compilation")]
    [InlineData("npm error code EINTEGRITY", "EINTEGRITY", "Dependency integrity")]
    public void SpecificEvidenceSurvivesGenericExit(string line, string code, string category)
    {
        var result = Scan(line, "##[error]Bash exited with code '1'.");
        Assert.Equal(code, result.Code);
        Assert.Equal(category, result.Category);
        Assert.Equal("TaskLog", result.Source);
        Assert.Equal(1, result.LogLine);
        Assert.DoesNotContain("registry.example.test", result.Message);
    }

    [Fact]
    public void CommandEchoAndGenericExitDoNotBecomeRootCause()
    {
        var result = Scan("##[command]echo npm error HTTP 401 Unauthorized", "Bash exited with code '1'.");
        Assert.Equal("Unknown", result.Category);
        Assert.Equal("Timeline", result.Source);
        Assert.Empty(result.Fingerprint);
    }

    [Theory]
    [InlineData("error token=shortSecret", "shortSecret")]
    [InlineData("error password='two words secret'", "two words secret")]
    [InlineData("error --token cliSecret", "cliSecret")]
    [InlineData("error _authToken=abc12345", "abc12345")]
    public void EvidenceRedactsCredentials(string line, string secret)
    {
        Assert.DoesNotContain(secret, Scan(line).Message);
    }

    [Fact]
    public void SkippedOversizedLineMarksIncomplete()
    {
        var result = Scan(new string('x', 17000), "npm ERR! 401 Unauthorized");
        Assert.Equal("Truncated", result.ScanStatus);
    }

    private static JsonElement Policy(string state, bool required = true, bool expired = false) => J(JsonSerializer.Serialize(new {
        evaluationId = "evaluation", status = state, context = new { buildId = 123, isExpired = expired },
        configuration = new { id = 7, isBlocking = required, isEnabled = true, type = new { id = BuildPolicyType }, settings = new { buildDefinitionId = 4 } }
    }));
    private static JsonElement PR => J("""{"pullRequestId":9,"status":"active","isDraft":false,"creationDate":"2026-10-01T12:00:00Z","repository":{"name":"repo"}}""");
    private static string V(PostTelemetry e, string key) => e.Tags.FirstOrDefault(t => t.Key == key)?.Value ?? "";

    [Theory]
    [InlineData("rejected", true, false, "1", "1")]
    [InlineData("rejected", false, false, "0", "0")]
    [InlineData("running", true, false, "1", "0")]
    [InlineData("approved", true, false, "0", "0")]
    [InlineData("rejected", true, true, "1", "0")]
    public void RequiredPoliciesDistinguishFailureFromWaiting(string state, bool required, bool expired, string blocks, string failing)
    {
        var e = MapPolicySnapshot(PR, Policy(state, required, expired), Settings, DateTime.UtcNow, "Complete");
        Assert.Equal(blocks, V(e, "BlocksMerge"));
        Assert.Equal(failing, V(e, "FailingBuildPolicy"));
        Assert.Equal(expired ? "0" : "123", V(e, "BuildId"));
    }

    [Fact]
    public void SnapshotMarkerHasStableEntityTimeAndNoOldFailure()
    {
        var one = AdoTelemetryEvents.Convert(new[] { MapPolicySnapshot(PR, null, Settings, new DateTime(2026,11,12), "Complete") }).Single();
        var two = AdoTelemetryEvents.Convert(new[] { MapPolicySnapshot(PR, null, Settings, new DateTime(2026,11,13), "AccessDenied") }).Single();
        Assert.Equal(one.EventTime, two.EventTime);
        Assert.Equal(0L, two.Data["PolicyId"]);
        Assert.Equal("AccessDenied", two.Data["SnapshotStatus"]);
        Assert.Equal(0L, two.Data["FailingBuildPolicy"]);
    }

    [Fact]
    public async Task CollectorPagesOnlyFailedTaskLogsAndOverridesGenericTimeline()
    {
        var mock = new Mock<IAzureDevOpsRestClient>(MockBehavior.Strict);
        var build = J("""{"id":123,"result":"failed","definition":{"id":4,"name":"PR-Check"},"repository":{"name":"repo"},"sourceBranch":"refs/pull/9/merge","startTime":"2026-11-13T09:00:00Z","finishTime":"2026-11-13T09:10:00Z"}""");
        var timeline = J("""{"records":[{"id":"failed","type":"Task","name":"Restore","result":"failed","log":{"id":17},"issues":[{"type":"error","message":"Bash exited with code '1'."}]},{"id":"ok","type":"Task","result":"succeeded","log":{"id":18}}]}""");
        mock.Setup(a => a.ListBuildsAsync("project", It.IsAny<DateTime>(), null)).ReturnsAsync(new List<JsonElement>{build});
        mock.Setup(a => a.GetTimelineAsync("project",123)).ReturnsAsync(timeline);
        mock.Setup(a => a.GetBuildLogLinesAsync("project",123,17,0,1999)).ReturnsAsync(Enumerable.Repeat("restoring",2000).ToArray());
        mock.Setup(a => a.GetBuildLogLinesAsync("project",123,17,2000,3999)).ReturnsAsync(new[]{"npm ERR! 401 Unauthorized"});
        var settings = new PipelineRunsSettings { Organization="org",Project="project", Parts=PipelineRunsParts.Runs|PipelineRunsParts.Steps|PipelineRunsParts.Failures, LogErrors=true };
        var events = await new PipelineRunsCollector(mock.Object,settings).CollectAsync();
        var failure = Assert.Single(events, e => e.Area.Name == AreaFailure);
        Assert.Equal("HTTP401", V(failure,"ErrorCode"));
        Assert.Equal("2001", V(failure,"LogLine"));
        Assert.Equal("Complete", V(failure,"ScanStatus"));
        Assert.Contains(events,e => e.Area.Name == AreaStep && V(e,"ErrorCode") == "HTTP401");
        mock.VerifyAll();
    }

    [Fact]
    public async Task StrictCollectionRejectsTruncatedEvidence()
    {
        var mock = new Mock<IAzureDevOpsRestClient>(MockBehavior.Strict);
        mock.Setup(a => a.ListBuildsAsync("project", It.IsAny<DateTime>(), null)).ReturnsAsync(new List<JsonElement>{
            J("""{"id":1,"finishTime":"2026-11-13T09:10:00Z"}""") });
        mock.Setup(a => a.GetTimelineAsync("project",1)).ReturnsAsync(J("""{"records":[{"id":"failed","type":"Task","result":"failed","log":{"id":17}}]}"""));
        mock.Setup(a => a.GetBuildLogLinesAsync("project",1,17,0,0)).ReturnsAsync(new[]{"npm ERR! 401 Unauthorized"});
        var settings = new PipelineRunsSettings { Project="project", Parts=PipelineRunsParts.Runs|PipelineRunsParts.Steps,StrictCollection=true, LogErrors=true, LogMaxLines=1 };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new PipelineRunsCollector(mock.Object,settings).CollectAsync());
        Assert.Contains("Truncated", ex.ToString());
    }

    private class LogHandler(string content, string mediaType = "text/plain", HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Uri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(content,System.Text.Encoding.UTF8,mediaType) });
        }
    }

    [Theory]
    [InlineData("first\nsecond\n","text/plain")]
    [InlineData("[\"first\",\"second\"]","application/json")]
    [InlineData("{\"value\":[\"first\",\"second\"]}","application/json")]
    public async Task RestClientReadsDocumentedLogFormats(string content, string mediaType)
    {
        var handler = new LogHandler(content,mediaType);
        using var client = new AzureDevOpsRestClient("https://dev.azure.com/example/","synthetic",handler);
        Assert.Equal(new[]{"first","second"},await client.GetBuildLogLinesAsync("a project",123,17,0,1999));
        Assert.Contains("/_apis/build/builds/123/logs/17?startLine=0&endLine=1999",handler.Uri!.AbsoluteUri);
    }

    [Fact]
    public async Task RestClientRejectsLoginHtmlAndReturnsMissingAsMissing()
    {
        using var html = new AzureDevOpsRestClient("https://dev.azure.com/example/","synthetic",new LogHandler("<html>login</html>","text/html"));
        await Assert.ThrowsAsync<InvalidDataException>(()=>html.GetBuildLogLinesAsync("project",123,17,0,1999));
        using var missing = new AzureDevOpsRestClient("https://dev.azure.com/example/","synthetic",new LogHandler("","text/plain",HttpStatusCode.NotFound));
        Assert.Null(await missing.GetBuildLogLinesAsync("project",123,17,0,1999));
    }

    [Fact]
    public async Task PoliciesRefreshOldActivePrEvenWhenNoBuildsInWindow()
    {
        var mock = new Mock<IAzureDevOpsRestClient>(MockBehavior.Strict);
        mock.Setup(a=>a.ListBuildsAsync("project",It.IsAny<DateTime>(),null)).ReturnsAsync(new List<JsonElement>());
        mock.Setup(a=>a.GetIterationsAsync("project")).ReturnsAsync((JsonElement?)null);
        mock.Setup(a=>a.ListRepositoriesAsync("project")).ReturnsAsync(new List<JsonElement>{J("""{"id":"repo-id","name":"repo"}""")});
        mock.Setup(a=>a.ListPullRequestsAsync("project","repo-id",It.IsAny<DateTime>(),It.IsAny<string>())).ReturnsAsync(new List<JsonElement>());
        mock.Setup(a=>a.ListActivePullRequestsAsync("project","repo-id")).ReturnsAsync(new List<JsonElement>{PR});
        mock.Setup(a=>a.GetPullRequestWorkItemRefsAsync("project","repo-id",9)).ReturnsAsync(new List<JsonElement>());
        mock.Setup(a=>a.GetWorkItemsAsync("project",It.IsAny<IEnumerable<int>>(),It.IsAny<IEnumerable<string>>())).ReturnsAsync(new List<JsonElement>());
        mock.Setup(a=>a.GetPullRequestThreadsAsync("project","repo-id",9)).ReturnsAsync(new List<JsonElement>());
        mock.Setup(a=>a.GetPullRequestIterationsAsync("project","repo-id",9)).ReturnsAsync(new List<JsonElement>());
        mock.Setup(a=>a.GetProjectIdAsync("project")).ReturnsAsync("11111111-1111-1111-1111-111111111111");
        mock.Setup(a=>a.GetPullRequestPoliciesAsync("project","11111111-1111-1111-1111-111111111111",9)).ReturnsAsync(new List<JsonElement>());
        var settings = new PipelineRunsSettings { Organization="org",Project="project",Since=new DateTime(2026,11,12),Parts=PipelineRunsParts.Runs|PipelineRunsParts.PullRequests|PipelineRunsParts.Policies };
        var events = await new PipelineRunsCollector(mock.Object,settings).CollectAsync();
        var marker = Assert.Single(events.Where(e=>e.Area.Name==AreaPolicy));
        Assert.Equal("0",V(marker,"PolicyId"));
        Assert.Equal("Complete",V(marker,"SnapshotStatus"));
        mock.VerifyAll();
    }

    [Fact]
    public async Task MalformedPolicyResponseCannotBeTreatedAsNoPolicies()
    {
        using var client = new AzureDevOpsRestClient("https://dev.azure.com/example/","synthetic",new LogHandler("{}","application/json"));
        await Assert.ThrowsAsync<InvalidDataException>(()=>client.GetPullRequestPoliciesAsync("project","11111111-1111-1111-1111-111111111111",9));
    }

    [Fact]
    public void CompilerWarningsCannotReplaceTheActualFailure()
    {
        var result = Scan("npm ERR! 401 Unauthorized", "warning CS8625: Cannot convert null literal to non-nullable reference type.");
        Assert.Equal("HTTP401", result.Code);
        Assert.Equal("Authentication / authorization", result.Category);
        var warningsOnly = Scan("warning CS8625: Cannot convert null literal to non-nullable reference type.", "Bash exited with code '1'.");
        Assert.Equal("Unknown", warningsOnly.Category);
    }
}
