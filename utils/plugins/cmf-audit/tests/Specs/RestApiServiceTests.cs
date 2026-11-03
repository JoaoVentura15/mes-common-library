using audit.Services;
using System.Net;
using System.Text.Json;
using Xunit;

namespace specs
{
    public class RestApiServiceTests
    {
        [Theory]
        [InlineData(0, "PipelineRun")]
        [InlineData(1, "Sprint")]
        public async Task PostEvents_FailureIdentifiesBatchAndPreservesResponseWithoutRetry(int acknowledgedBatches, string failedType)
        {
            var handler = new FailingPostHandler(acknowledgedBatches);
            using var client = new HttpClient(handler);
            using var service = new RestApiService(new ClientConfiguration("example.test", "Tenant", "MES", "unused", true), client);
            var events = new List<PostTelemetry>
            {
                new() { Area = new Entity("PipelineRun") },
                new() { Area = new Entity("Sprint") },
                new() { Area = new Entity("WorkItemFlow") },
            };

            var exception = await Assert.ThrowsAsync<Exception>(() => service.PostEventsAsync(events, 1));

            Assert.Contains($"batch {acknowledgedBatches + 1}/3", exception.Message);
            Assert.Contains($"{failedType}=1", exception.Message);
            Assert.Contains($"{acknowledgedBatches} event(s) were acknowledged", exception.Message);
            Assert.Contains("may already have been accepted", exception.Message);
            var httpException = Assert.IsType<HttpRequestException>(exception.InnerException);
            Assert.Equal(HttpStatusCode.BadGateway, httpException.StatusCode);
            Assert.Contains("upstream request timed out", httpException.Message);
            Assert.Equal(acknowledgedBatches + 1, handler.Payloads.Count);
            foreach (var payload in handler.Payloads)
            {
                using var document = JsonDocument.Parse(payload);
                var postedEvents = document.RootElement.GetProperty("IoTEvents");
                Assert.Equal(1, postedEvents.GetArrayLength());
                Assert.Equal("PostTelemetry", postedEvents[0].GetProperty("AppProperties").GetProperty("EventDefinition").GetString());
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task PostEvents_ChecksDataPlatformErrorsEvenWhenHttpStatusIsSuccessful(bool hasErrors)
        {
            var body = hasErrors
                ? """{"HasErrors":true,"ErrorList":{"$typeCMF":"CMFMap","CMFMapData":[{"Key":0,"Value":["Event 0 did not have the required fields."]}]}}"""
                : """{"HasErrors":false,"ErrorList":{"$typeCMF":"CMFMap","CMFMapData":[]}}""";
            var handler = new FailingPostHandler(0, HttpStatusCode.OK, body);
            using var client = new HttpClient(handler);
            using var service = new RestApiService(new ClientConfiguration("example.test", "Tenant", "MES", "unused", true), client);
            var events = new List<PostTelemetry> { new() { Area = new Entity("Sprint") } };

            if (hasErrors)
            {
                var exception = await Assert.ThrowsAsync<Exception>(() => service.PostEventsAsync(events, 1));
                Assert.Contains("0 event(s) were acknowledged", exception.Message);
                var rejection = Assert.IsType<InvalidOperationException>(exception.InnerException);
                Assert.Contains("HasErrors=true", rejection.Message);
                Assert.Contains("Event 0 did not have the required fields.", rejection.Message);
            }
            else
            {
                await service.PostEventsAsync(events, 1);
            }
            Assert.Single(handler.Payloads);
        }

        [Fact]
        public async Task PostTypedEvents_SendsTheDefinitionPathStableEventTimeAndFlatData()
        {
            var handler = new FailingPostHandler(int.MaxValue, HttpStatusCode.OK, "{\"HasErrors\":false}");
            using var client = new HttpClient(handler);
            using var service = new RestApiService(new ClientConfiguration("example.test", "Tenant", "MES", "unused", true), client);
            var time = new DateTime(2026, 10, 5, 5, 41, 19, DateTimeKind.Utc);
            var events = new List<audit.Objects.PipelineRuns.TypedEvent>
            {
                new("\\IoTEventDefinitions\\ADOTelemetry\\PipelineRun", time, new Dictionary<string, object?> { ["BuildId"] = 7002L, ["Pipeline"] = "PR-Validate", ["Started"] = time }),
            };

            await service.PostTypedEventsAsync(events, 1);

            using var document = JsonDocument.Parse(Assert.Single(handler.Payloads));
            var posted = document.RootElement.GetProperty("IoTEvents")[0];
            var app = posted.GetProperty("AppProperties");
            Assert.Equal("\\IoTEventDefinitions\\ADOTelemetry\\PipelineRun", app.GetProperty("EventDefinition").GetString());
            Assert.Equal(time, app.GetProperty("EventTime").GetDateTime().ToUniversalTime());
            var data = posted.GetProperty("Data");
            Assert.Equal(7002L, data.GetProperty("BuildId").GetInt64());
            Assert.Equal("PR-Validate", data.GetProperty("Pipeline").GetString());
            Assert.Equal(time, data.GetProperty("Started").GetDateTime().ToUniversalTime());
        }

        [Fact]
        public async Task PostTypedEvents_FailureSaysReRunningIsSafe()
        {
            var handler = new FailingPostHandler(0, HttpStatusCode.OK, """{"HasErrors":true}""");
            using var client = new HttpClient(handler);
            using var service = new RestApiService(new ClientConfiguration("example.test", "Tenant", "MES", "unused", true), client);
            var events = new List<audit.Objects.PipelineRuns.TypedEvent>
            {
                new("\\IoTEventDefinitions\\ADOTelemetry\\PipelineStep", DateTime.UtcNow, new Dictionary<string, object?> { ["RecordId"] = "t1" }),
            };

            var exception = await Assert.ThrowsAsync<Exception>(() => service.PostTypedEventsAsync(events, 1));

            Assert.Contains("PipelineStep=1", exception.Message);
            Assert.Contains("Re-posting is safe", exception.Message);
        }

        [Fact]
        public async Task PostTypedEvents_PausesBetweenBatchesOnlyWhenAsked()
        {
            var handler = new FailingPostHandler(int.MaxValue, HttpStatusCode.OK, "{\"HasErrors\":false}");
            using var client = new HttpClient(handler);
            using var service = new RestApiService(new ClientConfiguration("example.test", "Tenant", "MES", "unused", true), client);
            var events = Enumerable.Range(0, 3).Select(i => new audit.Objects.PipelineRuns.TypedEvent("\\IoTEventDefinitions\\ADOTelemetry\\PipelineRun", DateTime.UtcNow, new Dictionary<string, object?> { ["BuildId"] = (long)i })).ToList();

            service.BatchDelay = TimeSpan.Zero;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await service.PostTypedEventsAsync(events, 1);
            Assert.True(watch.ElapsedMilliseconds < 500, $"took {watch.ElapsedMilliseconds} ms");
            Assert.Equal(3, handler.Payloads.Count);

            service.BatchDelay = TimeSpan.FromMilliseconds(250);
            watch.Restart();
            await service.PostTypedEventsAsync(events, 1);
            Assert.True(watch.ElapsedMilliseconds >= 450, $"took {watch.ElapsedMilliseconds} ms"); // two pauses
        }

        private sealed class FailingPostHandler(int acknowledgedBatches,
            HttpStatusCode failureStatus = HttpStatusCode.BadGateway, string failureBody = "upstream request timed out") : HttpMessageHandler
        {
            public List<string> Payloads { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/oauth2/token"))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"test-token\"}") };

                Assert.Equal("/api/DataPlatform/PostMultipleIoTEvents", request.RequestUri.AbsolutePath);
                Payloads.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                return Payloads.Count <= acknowledgedBatches
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    : new HttpResponseMessage(failureStatus) { Content = new StringContent(failureBody) };
            }
        }
    }
}