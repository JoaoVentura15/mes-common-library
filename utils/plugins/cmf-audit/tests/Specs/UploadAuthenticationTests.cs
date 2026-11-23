using audit.Services;
using System.Net;
using Xunit;

namespace specs;

public class UploadAuthenticationTests
{
    [Fact]
    public async Task ParallelUnauthorizedUploadsRefreshOnceAndPreservePayloads()
    {
        var handler = new AuthHandler();
        using var client = new HttpClient(handler);
        using var service = new RestApiService(new ClientConfiguration("example.test", "Tenant", "MES", "seed", true), client)
        { UploadParallelism = 3, BatchDelay = TimeSpan.Zero };
        await service.PostEventsAsync(Enumerable.Range(0, 3).Select(_ => new PostTelemetry { Area = new Entity("PipelineRun") }).ToList(), 1);
        Assert.Equal(2, handler.AuthCalls);
        Assert.Equal(3, handler.Accepted);
    }

    [Theory]
    [InlineData(503, 1, 2)]
    [InlineData(503, 3, 3)]
    [InlineData(401, 1, 1)]
    public void AuthenticationRetriesOnlyTransientResponses(int status, int failures, int expectedCalls)
    {
        var handler = new AuthHandler { Failures = failures, FailureStatus = status };
        using var client = new HttpClient(handler);
        if (failures >= 3 || status == 401)
            Assert.Throws<HttpRequestException>(() => new RestApiService(new ClientConfiguration("example.test", "Tenant", "MES", "seed", true), client));
        else
            using (new RestApiService(new ClientConfiguration("example.test", "Tenant", "MES", "seed", true), client)) { }
        Assert.Equal(expectedCalls, handler.AuthCalls);
    }

    private sealed class AuthHandler : HttpMessageHandler
    {
        public int AuthCalls, Accepted, Failures, FailureStatus;
        private int rejected;
        private readonly TaskCompletionSource allRejected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> payloads = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("oauth2"))
            {
                var count = Interlocked.Increment(ref AuthCalls);
                if (count <= Failures)
                {
                    var failure = new HttpResponseMessage((HttpStatusCode)FailureStatus);
                    failure.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                    return failure;
                }
                var form = await request.Content!.ReadAsStringAsync();
                Assert.Contains(count == Failures + 1 ? "refresh_token=seed" : "refresh_token=rotated", form);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"access_token\":\"token{count}\",\"refresh_token\":\"rotated\"}}") };
            }
            var payload = await request.Content!.ReadAsStringAsync();
            if (request.Headers.Authorization!.Parameter == "token1")
            {
                payloads.TryAdd(payload, 0);
                if (Interlocked.Increment(ref rejected) == 3) allRejected.SetResult();
                await allRejected.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
            Assert.True(payloads.ContainsKey(payload));
            Assert.Equal("token2", request.Headers.Authorization.Parameter);
            Interlocked.Increment(ref Accepted);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
}

