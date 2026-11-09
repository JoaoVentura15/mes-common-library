using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Linq;
using Cmf.Custom.Tests.Biz.Common.Constants;
using Cmf.Community.ExternalSystemsSimulator.Tests.Utilities.Model;
using Cmf.Community.ExternalSystemsSimulator.Tests.Utilities.Model.Actions;
using Cmf.Community.ExternalSystemsSimulator.Tests.Utilities.Model.Actions.Parameters;
using Cmf.Custom.TestUtilities;
using Cmf.Foundation.BusinessOrchestration.GenericServiceManagement.InputObjects;
using Cmf.Foundation.BusinessOrchestration.GenericServiceManagement.OutputObjects;
using Cmf.Foundation.BusinessOrchestration.Utilities.InputObjects;
using Cmf.Navigo.BusinessObjects;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using Settings;

namespace Cmf.Community.ExternalSystemsSimulator.Tests.Utilities;

/// <summary>
/// Class used to test the Simulator.
/// Can also be used as a template for future tests that may require it.
///
/// Before running the tests, ensure that the Simulator is properly set up and configured.
///
/// For demo purposes a PortForwarder is needed and setup using:
///
/// docker run -d \
/// --name port-forwarder \
/// --network micron-local-11_3_1_default \
/// -p 8080:8080 \
/// alpine/socat \
/// TCP-LISTEN:8080,fork TCP-CONNECT:externalsystemssimulatormanager:8080
///
/// </summary>

//[TestClass]
public class SimulatorTestExamples : BaseContext
{
    private static ConfigurationEntryManager s_configurationEntryManager;

    [ClassInitialize]
    public static void ClassInit(TestContext context)
    {
        BaseInit(context);

        //To set up all endpoints on MES
        s_configurationEntryManager = SimulatorEndpointSetup.SetupAll();
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        BaseEnd();
        SimulatorCommon.ClearConfiguration();
        s_configurationEntryManager.TearDown();
    }

    [TestInitialize]
    public void TestInit()
    {
        SimulatorCommon.ClearConfiguration();
    }

    [TestCleanup]
    public void TestCleanup()
    {
        // No current implementation, but defined for potential future use
    }

    [Owner(TestOwners.SquadDevExOps)]
    [TestMethod]
    public void ExampleTest_LogEventActionConfiguration_WithTemplatePattern()
    {
        #region Arrange

        #region Resource and Body configuration

        var resource = new Resource { Name = MicronConstants.Resource3DIStartLotStart };
        resource.Load();
        string currentState = resource.CurrentMainState.CurrentState.Name;

        resource.ChangeResourceState("Standby");

        JObject customBatchCreateInputJsonMessage = new
        (
            new JProperty("UserName", "TestUser"),
            new JProperty("BatchCreationTrigger", "Test"),
            new JProperty("BatchId", "Test"),
            new JProperty("BatchName", "Test"),
            new JProperty("BatchState", "Created"),
            new JProperty("Materials", ""),
            new JProperty("SourceMaterials", ""),
            new JProperty("ResourceId", ""),
            new JProperty("ResourceName", $"{resource.Name}")
        );

        #endregion

        #region Simulator Configuration

        HttpStatusCode expectedStatusCode = HttpStatusCode.OK;

        var action1 = new LogEventAction(new LogEventParameter
        {
            EntityName = "{{ResourceName}}",
            EntityType = typeof(Resource).ToString(),
            ToState = "Unscheduled Down"
        });

        var action2 = new LogEventAction(
            new LogEventParameter
            {
                EntityName = "{{ResourceName}}",
                EntityType = typeof(Resource).ToString(),
                ToState = "Engineering"
            }) { DependsOn = [action1.Id] };


        JObject replyBodyTemplate = new
        (
            new JProperty("TestUserName", "{{UserName}}"),
            new JProperty("TestResourceName", "{{ResourceName}}")
        );

        SimulatorCommon.ConfigureEndpoint(replyBodyTemplate, SimulatorTopic.BatchManagerCreateBatch,
            expectedStatusCode, [
                action1,
                action2
            ]);

        #endregion

        #endregion

        //Act
        DateTime dateTime = DateTime.UtcNow;
        HttpResponseMessage reply = CallSimulatorAsync(customBatchCreateInputJsonMessage).GetAwaiter().GetResult();

        #region Assert

        #region Validate Reply

        Assert.IsNotNull(reply);
        var replyBody = JObject.Parse(reply.Content.ReadAsStringAsync().GetAwaiter().GetResult());

        Assert.IsNotNull(replyBody, "Expected body from the reply");
        Assert.AreEqual(expectedStatusCode, reply.StatusCode, "Invalid reply status code");
        Assert.AreEqual(customBatchCreateInputJsonMessage["UserName"], replyBody["TestUserName"],
            "UserName does not match with reply");
        Assert.AreEqual(customBatchCreateInputJsonMessage["ResourceName"], replyBody["TestResourceName"],
            "ResourceName does not match with reply");

        #endregion

        #region Resource state validation

        Assert.AreEqual(2, RetrieveLogEventsCount(resource, dateTime), "Unexpected number of LogEvents");

        resource.Load();
        Assert.AreEqual("Engineering", resource.CurrentMainState.CurrentState.Name,
            $"Unexpected resource state for resource {resource.Name}");

        #endregion

        #region Simulator Statistics validation

        SimulatorCommon.GetEndpointStatistics(SimulatorTopic.BatchManagerCreateBatch, (data) =>
        {
            Assert.IsTrue(data.Count == 1, $"Expected 1 EndpointStatistic, got {data.Count}");
            Assert.IsTrue(data[0].ReceivedBody is not null, "Received body is null");
            Assert.IsTrue(JToken.DeepEquals(customBatchCreateInputJsonMessage,
                JObject.FromObject(data[0].ReceivedBody)));
            return true;
        });

        #endregion

        #endregion

        // Cleanup
        resource.ChangeResourceState(currentState);
    }

    [Owner(TestOwners.SquadDevExOps)]
    [TestMethod]
    public void ExampleTest_PreconfiguredViaDocument_WithTemplatePattern()
    {
        string substrateIdValue = "TESTING_SUBSTRATE_ID";
        string substrateTypeValue = "TESTING_SUBSTRATE_TYPE";

        JObject substrateMapAPIRequestBody = new
        (
            new JProperty("substrate", new JObject(
                new JProperty("type", substrateTypeValue),
                new JProperty("id", substrateIdValue)
            ))
        );

        //Act
        DateTime dateTime = DateTime.UtcNow;
        HttpResponseMessage reply =
            CallSimulatorAsync(substrateMapAPIRequestBody, SimulatorEndpoints.SubstrateMapAPIEndpoint)
                .GetAwaiter().GetResult();

        #region Validate Reply

        Assert.IsNotNull(reply);
        var replyBody = JObject.Parse(reply.Content.ReadAsStringAsync().GetAwaiter().GetResult());

        Assert.IsNotNull(replyBody, "Expected body from the reply");
        Assert.AreEqual(HttpStatusCode.OK, reply.StatusCode, "Invalid reply status code");

        Assert.AreEqual(JTokenType.Null, replyBody["Status"]?.Type, "Expected null Status on reply body");
        Assert.AreEqual(JTokenType.Null, replyBody["Details"]?.Type, "Expected null Details on reply body");


        string rawMapData = replyBody["SubstrateMapRequest"]?["Substrates"]?[0]?["MapData"]?.ToString();
        Assert.IsNotNull(rawMapData);

        var mapData = XDocument.Parse(rawMapData);

        XElement substrateElement =
            mapData?.Element("MapData")?.Elements("Substrates")?.ElementAt(0)?.Element("Substrate");
        XElement substrateMapElement = mapData?.Element("MapData")?.Elements("SubstrateMaps")?.ElementAt(0)
            .Element("SubstrateMap");

        Assert.IsNotNull(mapData);
        Assert.AreEqual(substrateIdValue, substrateElement?.Attribute("SubstrateId")?.Value);
        Assert.AreEqual(substrateTypeValue, substrateElement?.Attribute("SubstrateType")?.Value);
        Assert.AreEqual(substrateIdValue, substrateMapElement?.Attribute("SubstrateId")?.Value);
        Assert.AreEqual(substrateTypeValue, substrateMapElement?.Attribute("SubstrateType")?.Value);

        #endregion

        SimulatorCommon.GetEndpointStatistics(SimulatorTopic.SubstrateMapAPI, expectedCount: 1);
    }


    #region Sanity check tests

    [DataRow(0, RunningMode.Full)]
    [DataRow(1, RunningMode.Full)]
    [DataRow(10, RunningMode.Full)]
    [DataRow(0, RunningMode.NoStatistics)]
    [DataRow(1, RunningMode.NoStatistics)]
    [DataRow(10, RunningMode.NoStatistics)]
    [Owner(TestOwners.SquadDevExOps)]
    [DataTestMethod]
    public async System.Threading.Tasks.Task SanityCheckTest_ExpectedStatisticsCount_FullRunningConfig(
        int statisticCount, RunningMode runningMode)
    {
        RunningConfig oldRunningConfig = SimulatorCommon.RunningConfig;
        SimulatorCommon.RunningConfig = new RunningConfig { RunningMode = runningMode };

        await Parallel.ForEachAsync(
            Enumerable.Range(0, statisticCount),
            async (_, ct) => await CallSimulatorAsync(null)
        );

        List<EndpointStatistic> reply =
            SimulatorCommon.GetEndpointStatistics(SimulatorTopic.BatchManagerCreateBatch,
                expectedCount: runningMode == RunningMode.NoStatistics ? 0 : statisticCount);

        // Reset to default for other tests
        SimulatorCommon.RunningConfig = oldRunningConfig;
    }

    #endregion

    public Task<HttpResponseMessage> CallSimulatorAsync(object body,
        string endpointToTest = SimulatorEndpoints.BatchManagerCreateBatchEndpoint)
    {
        var httpClient = new HttpClient();

        //Not SimulatorEndpoints.BaseURL, as the tests are expected to run with a port forwarding to the simulator, so localhost should be used.
        httpClient.BaseAddress = new Uri("http://127.0.0.1:8080");

        return httpClient.PostAsJsonAsync(endpointToTest, body);
    }

    public static int RetrieveLogEventsCount(Resource entity, DateTime fromDateTime)
    {
        //retrieve the resource log event counts
        GetObjectServiceHistoryAsDataSetOutput batchStageGetObjectServiceHistoryAsDataSet =
            new GetObjectServiceHistoryAsDataSetInput { Entity = entity, DateFrom = fromDateTime }
                .GetObjectServiceHistoryAsDataSetSync();

        //validate resource logs before going to retrieve resource log events
        if (batchStageGetObjectServiceHistoryAsDataSet.Result != null &&
            batchStageGetObjectServiceHistoryAsDataSet.TotalRows != 0)
        {
            DataSet dataSet =
                new ToDataSetInput { NgpDataSet = batchStageGetObjectServiceHistoryAsDataSet.Result }
                    .ToDataSetSync().DataSet;
            int rowsCount = 0;

            foreach (object row in dataSet.Tables[0].Rows)
            {
                if (row is DataRow dataRow)
                {
                    if (dataRow["ServiceName"].ToString()?.Equals("LogObjectEvent") ?? false)
                    {
                        rowsCount++;
                    }
                }
            }

            return rowsCount;
        }
        else
        {
            return 0;
        }
    }
}
