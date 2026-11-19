using audit.Services;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace specs;

public class WorkItemPagingTests
{
    private class RevisionHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var offset = Calls++ * 200;
            Assert.Contains($"$skip={offset}", request.RequestUri!.Query);
            var count = offset < 400 ? 200 : 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { value = Enumerable.Range(offset + 1, count).Select(rev => new { id = 123, rev }) }))
            });
        }
    }

    [Fact]
    public async Task ReadsEveryRevisionBeyondTheFirstTwoPages()
    {
        var handler = new RevisionHandler();
        using var client = new AzureDevOpsRestClient("https://dev.azure.com/example/", "test", handler);
        var revisions = await client.GetWorkItemRevisionsAsync("Project", 123);
        Assert.Equal(401, revisions.Count);
        Assert.Equal(401, revisions[^1].GetProperty("rev").GetInt32());
        Assert.Equal(3, handler.Calls);
    }

    private class Handler(bool repeat = false) : HttpMessageHandler
    {
        public List<string> Queries { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Contains("$top=10000", request.RequestUri!.Query);
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var query = doc.RootElement.GetProperty("query").GetString()!;
            Queries.Add(query);
            Assert.Equal(1, query.Split("ORDER BY").Length - 1);
            Assert.Contains("([System.TeamProject] = @project OR [System.State] = 'Closed') AND [System.Id] >", query);
            var offset = repeat ? 0 : (Queries.Count - 1) * 10000;
            var count = offset < 20000 ? 10000 : offset == 20000 ? 3 : 0;
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { workItems = Enumerable.Range(offset + 1, count).Select(id => new { id }) }), Encoding.UTF8, "application/json")
            };
        }
    }

    [Fact]
    public async Task EnumeratesMoreThanTwentyThousandIncludingFinalShortPage()
    {
        var handler = new Handler();
        using var client = new AzureDevOpsRestClient("https://dev.azure.com/example/", "test", handler);
        var ids = await client.QueryWorkItemIdsAsync("Project", "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project OR [System.State] = 'Closed' ORDER BY [System.Id]");
        Assert.Equal(Enumerable.Range(1, 20003), ids);
        Assert.Equal(4, handler.Queries.Count);
        Assert.Contains("[System.Id] > 20003", handler.Queries[^1]);
    }

    [Fact]
    public async Task RefusesRepeatedPagesInsteadOfLoopingOrPostingPartialHistory()
    {
        using var client = new AzureDevOpsRestClient("https://dev.azure.com/example/", "test", new Handler(true));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.QueryWorkItemIdsAsync("Project", "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project OR [System.State] = 'Closed'"));
    }
}
