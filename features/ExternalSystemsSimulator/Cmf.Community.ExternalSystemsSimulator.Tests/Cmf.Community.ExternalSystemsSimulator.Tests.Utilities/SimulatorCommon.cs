#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using Cmf.Community.ExternalSystemsSimulator.Tests.Utilities.Model;
using Cmf.Custom.TestUtilities;
using Cmf.MessageBus.Client;
using Cmf.MessageBus.Messages;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Settings;
using Action = Cmf.Community.ExternalSystemsSimulator.Tests.Utilities.Model.Actions.Action;

namespace Cmf.Community.ExternalSystemsSimulator.Tests.Utilities;

public class SimulatorCommon
{
    private const string PREFIX = "ExternalSystemsSimulator";
    private const string CONFIGURE = "Configure";
    private const string CLEAR = "Clear";
    private const string RETRIEVE = "Retrieve";
    private const string STATISTICS = "Statistics";
    private const string CONFIGURATION = "Configuration";
    private const string SEPARATOR = ".";
    private const int MBUS_TIMEOUT = 3600;
    private const int MAX_NUMBER_OF_RETRIES = 10;
    private const int MS_SLEEP_TIME = 500;

    private static Transport? s_messageBus;
    private static RunningConfig? s_runningConfig;
    private static DateTime s_runningConfigLastRefreshed = DateTime.MinValue;
    private static TimeSpan s_runningConfigRefreshInterval = TimeSpan.FromMinutes(2);

    public static Transport MessageBus
    {
        get
        {
            if (s_messageBus is not null)
            {
                return s_messageBus;
            }

            s_messageBus = new Transport(BaseContext.GetMessageBusTransportConfiguration());
            s_messageBus.Start();
            return s_messageBus;
        }
    }
    
    /// <summary>
    /// Configures the refresh interval for the RunningConfig.
    /// Setting this property will also trigger a refresh of the RunningConfig
    /// </summary>
    public static TimeSpan RunningConfigRefreshInterval
    {
        get { return s_runningConfigRefreshInterval; }
        set
        {
            s_runningConfigRefreshInterval = value;
            s_runningConfig = RefreshRunningConfig();
        }
    }

    /// <summary>
    /// Setting this property will configure the simulator RunningConfig and cache it for subsequent retrievals until the refresh interval is reached.
    /// After that, the next retrieval will fetch the RunningConfig from the simulator again.
    /// Getting this property will not trigger a refresh, it will only return the cached value until the refresh interval is reached.
    /// </summary>
    public static RunningConfig RunningConfig
    {
        get
        {
            if (s_runningConfig == null ||
                DateTime.Now - s_runningConfigLastRefreshed > s_runningConfigRefreshInterval)
            {
                s_runningConfig = RefreshRunningConfig();
            }

            return RefreshRunningConfig();
        }
        set
        {
            s_runningConfig = ConfigureSimulator(value);
            s_runningConfigLastRefreshed = DateTime.Now;
        }
    }

    #region Private Methods

    private static RunningConfig RefreshRunningConfig(bool failIfNoReply = true)
    {
        s_runningConfigLastRefreshed = DateTime.Now;
        return SendMBusRequestWithRetry<RunningConfig>(GetRetrieveSubject(null),
                   failIfNoReply: failIfNoReply) ??
               new RunningConfig
               {
                   DefaultResponseBody = string.Empty,
                   DefaultStatusCode = string.Empty,
                   RunningMode = RunningMode.Full
               };
    }
    
    private static string GetSubject(string subjectType, string? endpointTopic)
    {
        string subject = PREFIX + SEPARATOR + subjectType;

        if (string.IsNullOrWhiteSpace(endpointTopic))
        {
            return subject;
        }

        return subject + SEPARATOR + endpointTopic;
    }

    private static string GetConfigureSubject(string? endpointTopic) =>
        GetSubject(CONFIGURE, endpointTopic);

    private static string GetRetrieveSubject(string? endpointTopic) =>
        GetSubject(RETRIEVE, endpointTopic);

    private static string GetClearSubject(string? endpointTopic) =>
        GetSubject(CLEAR, endpointTopic);

    private static string GetRetrieveStatisticsSubject(string? endpointTopic) =>
        GetSubject(RETRIEVE + "." + STATISTICS, endpointTopic);

    private static string GetRetrieveConfigureSubject(string? endpointTopic) =>
        GetSubject(RETRIEVE + "." + CONFIGURE, endpointTopic);

    private static MbMessage? SendMBusRequestWithRetry(
        string subject,
        object? objectToSend = null,
        bool failIfNoReply = true,
        int maxNumberOfRetries = MAX_NUMBER_OF_RETRIES,
        int msSleepTime = MS_SLEEP_TIME)
    {
        MbMessage? response = null;
        string payload = JsonConvert.SerializeObject(objectToSend ?? new object());

        GenericUtilities.WaitFor(() =>
        {
            response = MessageBus.SendRequest(subject, payload, MBUS_TIMEOUT);

            return response is not null;
        }, maxNumberOfRetries, msSleepTime);

        if (response is null && failIfNoReply)
        {
            Assert.Inconclusive($"Failed to receive reply for subject {subject} with payload {payload}");
        }

        return response;
    }

    private static T? SendMBusRequestWithRetry<T>(
        string subject,
        object? objectToSend = null,
        bool failIfNoReply = true,
        int maxNumberOfRetries = MAX_NUMBER_OF_RETRIES,
        int msSleepTime = MS_SLEEP_TIME)
    {
        string? sanitizedJson =
            SendMBusRequestWithRetry(subject, objectToSend, failIfNoReply, maxNumberOfRetries, msSleepTime)!.Data
                ?.Trim();

        if (typeof(T).GetInterface("IEnumerable") != null)
        {
            sanitizedJson = sanitizedJson switch
            {
                "{}" or "" => "[]",
                var data => data
            };
        }

        return sanitizedJson is null ? default : JsonConvert.DeserializeObject<T>(sanitizedJson);
    }

    #endregion

    #region Public Methods

    /// <summary>
    ///     Used to configure endpoint expected reply and status code for a given endpoint
    /// </summary>
    /// <param name="bodyToSend">Body of the response</param>
    /// <param name="endpointTopic">Topic for endpoint to configure. Use SimulatorTopics </param>
    /// <param name="statusCode">Response code to define. Only valid values between 100-599. Default 200</param>
    /// <param name="actions">List of actions to execute when endpoint is reached</param>
    /// <param name="failIfNoReply">Optional: Fail with Inconclusive if </param>
    /// <returns> Reply from the simulator with current configs </returns>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public static List<EndpointConfig>? ConfigureEndpoint(object bodyToSend, string endpointTopic,
        HttpStatusCode statusCode = HttpStatusCode.OK, List<Action>? actions = null, bool failIfNoReply = true)
    {
        string subject = GetConfigureSubject(endpointTopic);
        int statusCodeToSend = (int)statusCode;

        if (statusCodeToSend is <= 100 or >= 599)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode), statusCode,
                "Invalid value for statusCode. Value should be between 100 and 599");
        }

        EndpointConfig endpointConfig = new()
        {
            Id = Guid.NewGuid().ToString(),
            BodyToSend = bodyToSend,
            StatusCode = statusCodeToSend,
            ActionsToExecute = actions
        };
        
        return SendMBusRequestWithRetry<List<EndpointConfig>>(subject, endpointConfig, failIfNoReply);
    }

    /// <summary>
    /// Configure Simulator RunningConfig.
    /// </summary>
    /// <param name="config">Configuration to set</param>
    /// <param name="failIfNoReply"></param>
    /// <returns></returns>
    public static RunningConfig? ConfigureSimulator(RunningConfig config, bool failIfNoReply = true) =>
        SendMBusRequestWithRetry<RunningConfig>(GetConfigureSubject(null), config, failIfNoReply);

    /// <summary>
    /// Gets specified endpoint current configurations
    /// </summary>
    /// <param name="endpointTopic">Endpoint topic to configure. eg: BatchManager.CreateBatch</param>
    /// <param name="failIfNoReply">Optional: Fail with Inconclusive if </param>
    /// <returns> Reply from the simulator with current configs </returns>
    public static EndpointData? GetCurrentConfiguration(string endpointTopic, bool failIfNoReply = true)
    {
        if (RunningConfig.RunningMode == RunningMode.NoStatistics)
        {
            return null;
        }

        return SendMBusRequestWithRetry<EndpointData>(GetRetrieveSubject(endpointTopic), failIfNoReply: failIfNoReply);
    }

    /// <summary>
    /// Gets the EndpointStatistics list for the specified endpoint.
    /// Optionally, it can also assert the count of statistics retrieved against an expected count, if provided.
    /// </summary>
    /// <param name="endpointTopic">Topic from which to retrieve the statistics</param>
    /// <param name="customValidation">Optional: Custom validation to execute when value is retrieved</param>
    /// <param name="expectedCount">Optional: Number of statistics a given endpoint should have</param>
    /// <param name="failIfNoReply">Optional: Fail with Inconclusive if </param>
    /// <returns>EndpointStatistics list</returns>
    /// <exception cref="Exception">In case it fails to retrieve the EndpointConfig</exception>
    public static List<EndpointStatistic>? GetEndpointStatistics(string endpointTopic,
        Func<List<EndpointStatistic>?, bool>? customValidation = null, int expectedCount = -1,
        bool failIfNoReply = true)
    {
        if (RunningConfig.RunningMode == RunningMode.NoStatistics)
        {
            return null;
        }

        List<EndpointStatistic>? statistics =
            SendMBusRequestWithRetry<List<EndpointStatistic>>(GetRetrieveStatisticsSubject(endpointTopic),
                failIfNoReply: failIfNoReply);

        if (customValidation is null && expectedCount < 0)
        {
            return statistics;
        }

        if (customValidation is null)
        {
            Assert.AreEqual(expectedCount, statistics?.Count,
                $"Endpoint for {endpointTopic} was called {statistics?.Count} times, instead of {expectedCount}. Statistics: {JsonConvert.SerializeObject(statistics)}");
        }
        else
        {
            Assert.IsTrue(customValidation(statistics), "Custom validation failed");
        }

        return statistics;
    }

    /// <summary>
    ///     Clears specified endpoint current configurations. All by default
    /// </summary>
    /// <param name="endpointTopic">Endpoint topic to configure. eg: BatchManager.CreateBatch</param>
    /// <param name="failIfNoReply">Optional: Fail with Inconclusive if </param>
    /// <param name="retainRunningConfig">Optional: To retain test functionality. Default: True</param>
    public static void ClearConfiguration(string? endpointTopic = null, bool failIfNoReply = true, bool retainRunningConfig = true)
    {
        RunningConfig? currentConfig = null;

        if (retainRunningConfig)
        {
            currentConfig = RunningConfig;
        }

        //If not retain then it is a full reset.
        if(retainRunningConfig && RunningConfig.RunningMode.Equals(RunningMode.NoStatistics))
        {
            return;
        }

        SendMBusRequestWithRetry(GetClearSubject(endpointTopic), failIfNoReply: failIfNoReply);

        if (currentConfig is not null)
        {
            RunningConfig = currentConfig;
        }
    }

    #endregion
}
