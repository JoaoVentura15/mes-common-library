using Cmf.CLI.Core;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace audit.Services
{
    public interface IRestApiService
    {
        Task PostEventAsync(PostTelemetry eventData);
        Task PostEventsAsync(List<PostTelemetry> eventsData, int batchSize = 0);
        Task PostTypedEventsAsync(List<audit.Objects.PipelineRuns.TypedEvent> eventsData, int batchSize = 0);
    }

    public class RestApiService : IRestApiService, IDisposable
    {
        private string? _accessToken;
        private HttpClient? _httpClient;
        private ClientConfiguration _clientConfiguration;

        /// <summary>Pause between batches. One second by default (a courtesy to the Data Platform); zero posts back to back.</summary>
        public TimeSpan BatchDelay { get; set; } = TimeSpan.FromSeconds(1);

        public RestApiService(ClientConfiguration clientConfiguration, HttpClient? httpClient = null)
        {
            this._clientConfiguration = clientConfiguration;
            this._httpClient = httpClient;
            if (!clientConfiguration.useSSL)
            {
                Log.Warning("SSL is disabled: the security token and audit data will be sent unencrypted.");
            }
            this._accessToken = AccessTokenAsync().GetAwaiter().GetResult();
        }

        public async Task<string> AccessTokenAsync()
        {
            this._httpClient ??= new HttpClient();
            this._httpClient.BaseAddress = new Uri($"http{(_clientConfiguration.useSSL ? "s" : "")}://{_clientConfiguration.HostAddress}");
            var url = $"/SecurityPortal/api/tenant/{Uri.EscapeDataString(_clientConfiguration.ClientTenantName ?? string.Empty)}/oauth2/token";

            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "client_id", _clientConfiguration.ClientId },
                { "grant_type", "refresh_token" },
                { "refresh_token", _clientConfiguration.SecurityAccessToken }
            });

            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

            var response = await this._httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync();
            var tokenResponse = JsonSerializer.Deserialize<JsonElement>(body);
            var accessToken = tokenResponse.GetProperty("access_token").GetString();

            _accessToken = accessToken;
            return accessToken;
        }

        public async Task PostEventsAsync(List<PostTelemetry> eventsData, int batchSize = 0)
        {
            // 0 = everything in one call (previous behaviour)
            batchSize = batchSize > 0 ? batchSize : Math.Max(1, eventsData.Count);

            for (int i = 0; i < eventsData.Count; i += batchSize)
            {
                var batch = eventsData.Skip(i).Take(batchSize).ToList();
                var batchNumber = i / batchSize + 1;
                var totalBatches = (eventsData.Count - 1) / batchSize + 1;
                var eventTypes = string.Join(", ", batch.GroupBy(item => item.Area?.Name ?? "Unknown")
                    .Select(group => $"{group.Key}={group.Count()}"));

                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    "/api/DataPlatform/PostMultipleIoTEvents");

                // Required headers
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
                request.Headers.Add("Cmf_ClientTenantName", _clientConfiguration.ClientTenantName);
                request.Headers.Add("Cmf_HostName", _clientConfiguration.HostAddress);

                var input = new List<PostEventInput>();
                foreach (var eventData in batch)
                {
                    input.Add(new PostEventInput
                    {
                        AppProperties = new AppProperties
                        {
                            ApplicationName = "CMF-Review",
                            EventDefinition = "PostTelemetry",
                            EventTime = DateTime.UtcNow
                        },
                        IgnoreLastServiceId = false,
                        NumberOfRetries = 0,
                        Data = eventData,
                        Priority = 0,
                        AcceptInvalidSchema = false
                    });
                }

                var call = new Dictionary<string, List<PostEventInput>>()
                    {
                        {"IoTEvents", input }
                    };
                var json = JsonSerializer.Serialize(call);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                Log.Information($"Posting batch {batchNumber}/{totalBatches}: {batch.Count} event(s) ({eventTypes}) to {_clientConfiguration.HostAddress}");

                try
                {
                    using var response = await _httpClient.SendAsync(request);
                    var responseBody = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        var preview = responseBody[..Math.Min(responseBody.Length, 2048)];
                        throw new HttpRequestException(
                            $"HTTP {(int)response.StatusCode} ({response.ReasonPhrase}) from PostMultipleIoTEvents. Response: {preview}",
                            null, response.StatusCode);
                    }
                    if (!string.IsNullOrWhiteSpace(responseBody))
                    {
                        using var output = JsonDocument.Parse(responseBody);
                        if (output.RootElement.TryGetProperty("HasErrors", out var hasErrors) && hasErrors.ValueKind == JsonValueKind.True)
                        {
                            var preview = responseBody[..Math.Min(responseBody.Length, 2048)];
                            throw new InvalidOperationException($"Data Platform returned HasErrors=true from PostMultipleIoTEvents. Response: {preview}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception(
                        $"Failed posting batch {batchNumber}/{totalBatches} (events {i + 1}-{i + batch.Count} of {eventsData.Count}; {eventTypes}). " +
                        $"{i} event(s) were acknowledged before this batch. The failed batch may already have been accepted; it was not retried.", ex);
                }

                Log.Information($"Posted batch {batchNumber}/{totalBatches}: {i + batch.Count}/{eventsData.Count} event(s) acknowledged");

                // Wait 1 second before next batch (except for the last batch)
                if (i + batchSize < eventsData.Count)
                {
                    await Task.Delay(BatchDelay);
                }
            }
        }

        private const int MaxAttempts = 5;

        /// <summary>Posts typed IoT events (one event definition per table, flat column data) with PostMultipleIoTEvents.</summary>
        public async Task PostTypedEventsAsync(List<audit.Objects.PipelineRuns.TypedEvent> eventsData, int batchSize = 0)
        {
            batchSize = batchSize > 0 ? batchSize : Math.Max(1, eventsData.Count);

            for (int i = 0; i < eventsData.Count; i += batchSize)
            {
                var batch = eventsData.Skip(i).Take(batchSize).ToList();
                var batchNumber = i / batchSize + 1;
                var totalBatches = (eventsData.Count - 1) / batchSize + 1;
                var eventTypes = string.Join(", ", batch.GroupBy(item => item.Definition.Split('\\').Last())
                    .Select(group => $"{group.Key}={group.Count()}"));

                var input = batch.Select(e => new PostTypedEventInput
                {
                    AppProperties = new AppProperties
                    {
                        ApplicationName = "CMF-Review",
                        EventDefinition = e.Definition,
                        // Stable per entity: it selects the monthly partition, and ReplacingMergeTree only dedupes inside a partition
                        EventTime = e.EventTime
                    },
                    IgnoreLastServiceId = false,
                    NumberOfRetries = 0,
                    Data = e.Data,
                    Priority = 0,
                    AcceptInvalidSchema = false
                }).ToList();

                var payload = JsonSerializer.Serialize(new Dictionary<string, List<PostTypedEventInput>> { { "IoTEvents", input } });
                // A request message can be sent only once, so each attempt builds its own
                HttpRequestMessage NewRequest()
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, "/api/DataPlatform/PostMultipleIoTEvents");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
                    request.Headers.Add("Cmf_ClientTenantName", _clientConfiguration.ClientTenantName);
                    request.Headers.Add("Cmf_HostName", _clientConfiguration.HostAddress);
                    request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    return request;
                }

                Log.Information($"Posting typed batch {batchNumber}/{totalBatches}: {batch.Count} event(s) ({eventTypes}) to {_clientConfiguration.HostAddress}");

                try
                {
                    // Typed events are idempotent (same key replaces the row), so transient network failures and 5xx are retried
                    HttpResponseMessage response;
                    for (var attempt = 1; ; attempt++)
                    {
                        try
                        {
                            response = await _httpClient!.SendAsync(NewRequest());
                            if ((int)response.StatusCode < 500 || attempt >= MaxAttempts) break;
                            response.Dispose();
                        }
                        catch (Exception ex) when (attempt < MaxAttempts && (ex is HttpRequestException || ex is IOException || ex is TaskCanceledException))
                        {
                        }
                        Log.Warning($"Typed batch {batchNumber}/{totalBatches} attempt {attempt}/{MaxAttempts} failed; retrying in {attempt * 5} s");
                        await Task.Delay(TimeSpan.FromSeconds(attempt * 5));
                    }
                    using var _ = response;
                    var responseBody = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        var preview = responseBody[..Math.Min(responseBody.Length, 2048)];
                        throw new HttpRequestException(
                            $"HTTP {(int)response.StatusCode} ({response.ReasonPhrase}) from PostMultipleIoTEvents. Response: {preview}",
                            null, response.StatusCode);
                    }
                    if (!string.IsNullOrWhiteSpace(responseBody))
                    {
                        using var output = JsonDocument.Parse(responseBody);
                        if (output.RootElement.TryGetProperty("HasErrors", out var hasErrors) && hasErrors.ValueKind == JsonValueKind.True)
                        {
                            var preview = responseBody[..Math.Min(responseBody.Length, 2048)];
                            throw new InvalidOperationException($"Data Platform returned HasErrors=true from PostMultipleIoTEvents. Response: {preview}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception(
                        $"Failed posting typed batch {batchNumber}/{totalBatches} (events {i + 1}-{i + batch.Count} of {eventsData.Count}; {eventTypes}). " +
                        $"{i} event(s) were acknowledged before this batch. Re-posting is safe: rows with the same key are replaced.", ex);
                }

                Log.Information($"Posted typed batch {batchNumber}/{totalBatches}: {i + batch.Count}/{eventsData.Count} event(s) acknowledged");

                if (i + batchSize < eventsData.Count)
                {
                    await Task.Delay(BatchDelay);
                }
            }
        }

        public async Task PostEventAsync(PostTelemetry eventData)
        {
            if (_httpClient == null)
            {
                throw new InvalidOperationException("REST API service not configured. Call Configure first.");
            }

            var request = new HttpRequestMessage(
                HttpMethod.Post,
                "/api/DataPlatform/PostEvent");

            // Required headers
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            request.Headers.Add("Cmf_ClientTenantName", _clientConfiguration.ClientTenantName);
            request.Headers.Add("Cmf_HostName", _clientConfiguration.HostAddress);

            var input = new PostEventInput
            {
                AppProperties = new AppProperties
                {
                    ApplicationName = "CMF-Review",
                    EventDefinition = "PostTelemetry",
                    EventTime = DateTime.UtcNow
                },
                IgnoreLastServiceId = false,
                NumberOfRetries = 0,
                Data = eventData,
                Priority = 0,
                AcceptInvalidSchema = false
            };

            var json = JsonSerializer.Serialize(input);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }

    public record Entity(string Name);
    public record Tag(string Key, string Value);

    public record Data(
        string Class,
        string Name,
        List<decimal> NumericValues,
        List<string> StringValues,
        List<long> Timestamps,
        string UnitOfMeasure
    );

    public record ClientConfiguration(string HostAddress, string ClientTenantName, string ClientId, string SecurityAccessToken, bool useSSL);

    public class BaseInput
    {
        public string Id { get; set; }
        public string Type { get; set; }
        public bool IgnoreLastServiceId { get; set; }
        public int NumberOfRetries { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        public string ServiceComments { get; set; }
        public Dictionary<string, object> ExtraParameters { get; set; } = new();
    }

    public class PostEventInput : BaseInput
    {
        public bool AcceptInvalidSchema { get; set; }
        public AppProperties AppProperties { get; set; }
        public PostTelemetry Data { get; set; }
        public int Priority { get; set; }
    }

    public class PostTypedEventInput : BaseInput
    {
        public bool AcceptInvalidSchema { get; set; }
        public AppProperties AppProperties { get; set; }
        public Dictionary<string, object?> Data { get; set; }
        public int Priority { get; set; }
    }

    public class AppProperties
    {
        public string ApplicationName { get; set; }
        public string EventDefinition { get; set; }
        public DateTime EventTime { get; set; }
    }

    public class PostTelemetry
    {
        public List<Data> Parameters { get; set; }
        public List<Tag> Tags { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Entity Resource { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Entity Area { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Entity Facility { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Entity Site { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Entity Enterprise { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Entity? Flow { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Entity? Step { get; set; }

        /// <summary>Empty event, for callers that set the ISA95 levels themselves (e.g. pipeline-runs).</summary>
        public PostTelemetry()
        {
            this.Parameters = new List<Data>();
            this.Tags = new List<Tag>();
        }

        public PostTelemetry(string projectName, string packageId, string packageVersion, string type, string name, Dictionary<string, string> metadata, List<Tag> tags = null)
        {
            // We will hijack the ISA95 for our context
            this.Enterprise = string.IsNullOrEmpty(projectName) ? null : new Entity(projectName);
            this.Site = string.IsNullOrEmpty(packageId) ? null : new Entity(packageId);
            this.Facility = string.IsNullOrEmpty(packageVersion) ? null : new Entity(packageVersion);
            this.Area = string.IsNullOrEmpty(type) ? null : new Entity(type);
            this.Resource = string.IsNullOrEmpty(name) ? null : new Entity(name);
            this.Tags = tags;

            this.Parameters = new List<Data>();
            foreach (var item in metadata)
            {
                if (item.Value != null)
                {
                    if (decimal.TryParse(item.Value, out decimal numericValue))
                    {
                        this.Parameters.Add(new Data("Property", item.Key, [numericValue], [], [DateTimeOffset.Now.ToUnixTimeSeconds()], ""));
                    }
                    else
                    {
                        this.Parameters.Add(new Data("Property", item.Key, [], [item.Value], [DateTimeOffset.Now.ToUnixTimeSeconds()], ""));
                    }
                }
            }
        }
    }
}