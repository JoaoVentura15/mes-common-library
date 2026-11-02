using audit.Objects.PipelineRuns;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Xunit;
using static audit.Objects.PipelineRuns.PipelineRunsMapper;

namespace specs
{
    public class AdoTelemetryEventsTests
    {
        private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

        private static PipelineRunsSettings Settings() =>
            PipelineRunsSettings.From(new PipelineRunsOptions
            {
                azureDevOpsUrl = "https://dev.azure.com/example/",
                teamProject = "Contoso",
                days = 30,
            }, new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc));

        private const string Run = """
            {"id":7002,"buildNumber":"merge_PR-Validate.7002","result":"succeeded","reason":"pullRequest",
             "queueTime":"2026-10-05T05:41:19Z","startTime":"2026-10-05T05:41:23Z","finishTime":"2026-10-05T06:12:20Z",
             "sourceBranch":"refs/pull/2002/merge","sourceVersion":"aaa","definition":{"id":7,"name":"PR-Validate"},
             "repository":{"id":"00000000","name":"Contoso","type":"TfsGit"},
             "parameters":"{\"system.pullRequest.targetBranch\":\"refs/heads/development\",\"system.pullRequest.pullRequestIteration\":\"2\"}",
             "requestedFor":{"id":"44444444-4444-4444-4444-444444444444","displayName":"Dev"}}
            """;

        private const string Timeline = """
            {"records":[
              {"id":"s1","type":"Stage","name":"Build","state":"completed","result":"failed","startTime":"2026-10-05T05:42:00Z","finishTime":"2026-10-05T06:10:00Z","order":1},
              {"id":"t1","parentId":"s1","type":"Task","name":"Publish","state":"completed","result":"failed","startTime":"2026-10-05T06:00:00Z","finishTime":"2026-10-05T06:05:30Z","order":2,"attempt":1,"errorCount":1,
               "issues":[{"type":"error","message":"error NU1101: Unable to find package Foo"}]},
              {"id":"t2","parentId":"s1","type":"Task","name":"Never ran","state":"pending","order":3}
            ]}
            """;

        private static List<TypedEvent> Typed()
        {
            var events = new List<audit.Services.PostTelemetry> { MapRun(J(Run), J(Timeline), Settings()) };
            events.AddRange(MapTimeline(J(Run), J(Timeline), Settings()));
            return AdoTelemetryEvents.Convert(events);
        }

        [Fact]
        public void Run_BecomesATypedEventWithNumbersAndUtcTimes()
        {
            var run = Typed().Single(e => e.Definition.EndsWith("\\PipelineRun"));
            Assert.Equal("\\IoTEventDefinitions\\ADOTelemetry\\PipelineRun", run.Definition);
            Assert.Equal("example", run.Data["Organization"]);
            Assert.Equal("Contoso", run.Data["Project"]);
            Assert.Equal(7002L, run.Data["BuildId"]);
            Assert.Equal(2002L, run.Data["PullRequestId"]);
            Assert.Equal(0L, run.Data["ParentBuildId"]);
            Assert.Equal("PR-Validate", run.Data["Pipeline"]);
            Assert.Equal("PR", run.Data["Role"]);
            Assert.Equal(1857L, run.Data["DurationSeconds"]); // 05:41:23 -> 06:12:20
            Assert.Equal(4L, run.Data["QueueSeconds"]);
            Assert.Equal(new DateTime(2026, 10, 5, 5, 41, 19, DateTimeKind.Utc), run.Data["QueuedAt"]);
            Assert.Equal(new DateTime(2026, 10, 5, 6, 12, 20, DateTimeKind.Utc), run.Data["Finished"]);
        }

        [Fact]
        public void EventTime_IsTheEntityTimeNotThePostTime()
        {
            // Same input twice must give the same EventTime (same partition), otherwise re-posting duplicates rows
            var a = Typed();
            var b = Typed();
            Assert.Equal(a.Select(e => e.EventTime), b.Select(e => e.EventTime));
            Assert.Equal(new DateTime(2026, 10, 5, 5, 41, 19, DateTimeKind.Utc), a.Single(e => e.Definition.EndsWith("\\PipelineRun")).EventTime);
        }

        [Fact]
        public void Steps_AreOneEventPerRecordKeyedByRecordId()
        {
            var steps = Typed().Where(e => e.Definition.EndsWith("\\PipelineStep")).ToList();
            Assert.Equal(3, steps.Count); // the stage and two tasks share the PipelineStep table
            Assert.Equal(new[] { "s1", "t1", "t2" }, steps.Select(s => (string)s.Data["RecordId"]!).OrderBy(x => x));
            var publish = steps.Single(s => (string)s.Data["RecordId"]! == "t1");
            Assert.Equal("Publish", publish.Data["StepName"]);
            Assert.Equal("Build", publish.Data["StageName"]);
            Assert.Equal(0L, publish.Data["IsApprovalStage"]);
            Assert.Equal(330L, publish.Data["DurationSeconds"]);
            Assert.Equal("NU1101", publish.Data["ErrorCode"]);
            Assert.Equal(1L, publish.Data["Errors"]);
        }

        [Fact]
        public void PendingStep_UsesItsRunsTimeAndLeavesTimesOut()
        {
            var pending = Typed().Single(e => e.Data.TryGetValue("RecordId", out var id) && (string?)id == "t2");
            Assert.False(pending.Data.ContainsKey("Started"));
            Assert.Equal(new DateTime(2026, 10, 5, 5, 41, 19, DateTimeKind.Utc), pending.EventTime);
        }

        [Fact]
        public void OtherAreas_AreNotConverted() =>
            Assert.Empty(AdoTelemetryEvents.Convert(new[] { new audit.Services.PostTelemetry { Area = new audit.Services.Entity("Unknown"), Tags = new(), Parameters = new() } }));

        [Fact]
        public void PullRequest_KeepsTimesDurationsAndLinkedWorkItems()
        {
            var pr = J("""{"pullRequestId":2002,"status":"completed","title":"Fix","creationDate":"2026-10-07T05:26:00Z","closedDate":"2026-10-07T07:26:00Z","repository":{"name":"Contoso"},"createdBy":{"id":"a","displayName":"A"}}""");
            var items = new List<JsonElement> { J("""{"id":1,"fields":{"System.WorkItemType":"Bug","System.AreaPath":"X"}}"""), J("""{"id":2,"fields":{"System.WorkItemType":"Bug","System.AreaPath":"X"}}""") };
            var typed = Assert.Single(AdoTelemetryEvents.Convert(new[] { MapPullRequest(pr, items, Settings()) }));
            Assert.EndsWith("\\PullRequest", typed.Definition);
            Assert.Equal(2002L, typed.Data["PullRequestId"]);
            Assert.Equal("1;2", typed.Data["WorkItemIds"]);
            Assert.Equal("Bug", typed.Data["WorkItemTypes"]);
            Assert.Equal(7200L, typed.Data["OpenSeconds"]);
            Assert.Equal(new DateTime(2026, 10, 7, 5, 26, 0, DateTimeKind.Utc), typed.Data["Created"]);
            Assert.Equal(typed.Data["Created"], typed.EventTime);
            Assert.False(typed.Data.ContainsKey("FirstApproval")); // not collected -> null column
            Assert.False(typed.Data.ContainsKey("TimeToFirstApprovalSeconds")); // absent stays NULL, not 0
            Assert.False(typed.Data.ContainsKey("Pushes"));
        }

        [Fact]
        public void TestFailure_UsesTheRunTimeAndMillisecondDuration()
        {
            var result = J("""{"automatedTestName":"A.B.C","outcome":"Failed","durationInMs":1500,"errorMessage":"boom"}""");
            var events = new List<audit.Services.PostTelemetry> { MapRun(J(Run), J(Timeline), Settings()), MapTestFailure(J(Run), J("""{"id":9,"name":"run"}"""), result, Settings()) };
            var typed = AdoTelemetryEvents.Convert(events).Single(e => e.Definition.EndsWith("\\TestFailure"));
            Assert.Equal("A.B.C", typed.Data["TestName"]);
            Assert.Equal(9L, typed.Data["TestRunId"]);
            Assert.Equal(1500L, typed.Data["DurationMs"]);
            Assert.Equal(new DateTime(2026, 10, 5, 5, 41, 19, DateTimeKind.Utc), typed.EventTime);
        }

        [Fact]
        public void TestFailure_ReadsADoubleDurationAndLeavesAMissingOneNull()
        {
            // Azure DevOps sends durationInMs as a double; it used to be read as 0.
            var withDouble = J("""{"automatedTestName":"A.B.C","outcome":"Failed","durationInMs":7557.0}""");
            var without = J("""{"automatedTestName":"A.B.D","outcome":"Failed"}""");
            var run = J("""{"id":9,"name":"run"}""");
            var events = new List<audit.Services.PostTelemetry> { MapRun(J(Run), J(Timeline), Settings()), MapTestFailure(J(Run), run, withDouble, Settings()), MapTestFailure(J(Run), run, without, Settings()) };
            var failures = AdoTelemetryEvents.Convert(events).Where(e => e.Definition.EndsWith("\\TestFailure")).ToDictionary(e => (string)e.Data["TestName"]!);
            Assert.Equal(7557L, failures["A.B.C"].Data["DurationMs"]);
            Assert.False(failures["A.B.D"].Data.ContainsKey("DurationMs")); // not reported -> NULL, not 0
        }

        [Fact]
        public void PipelineRun_FailedTestsIsNullWithoutATestRunAndZeroWhenTestsPassed()
        {
            var none = Assert.Single(AdoTelemetryEvents.Convert(new[] { MapRun(J(Run), J(Timeline), Settings()) }));
            Assert.False(none.Data.ContainsKey("FailedTests"));
            var clean = Assert.Single(AdoTelemetryEvents.Convert(new[] { MapRun(J(Run), J(Timeline), Settings(), failedTests: 0) }));
            Assert.Equal(0L, clean.Data["FailedTests"]);
        }

        [Fact]
        public void AgentJob_And_Sprint_AreConverted()
        {
            var job = J("""{"requestId":77,"owner":{"id":7002},"planType":"Build","result":"succeeded","queueTime":"2026-10-05T05:00:00Z","assignTime":"2026-10-05T05:01:00Z","finishTime":"2026-10-05T05:11:00Z","reservedAgent":{"name":"agent1"},"definition":{"name":"PR-Validate"}}""");
            var typed = Assert.Single(AdoTelemetryEvents.Convert(new[] { MapAgentJob(job, "Pool", Settings()) }));
            Assert.Equal(77L, typed.Data["RequestId"]);
            Assert.Equal("Pool", typed.Data["Pool"]);
            Assert.Equal(60L, typed.Data["WaitSeconds"]);
            Assert.Equal(600L, typed.Data["BusySeconds"]);
        }

        [Fact]
        public void EveryEventHasKeysStartingWithOrganizationAndProject_AndUniqueColumnNames()
        {
            Assert.Equal(16, AdoTelemetryEvents.Schemas.Count);
            Assert.All(AdoTelemetryEvents.Schemas, s =>
            {
                var keys = s.Columns.Where(c => c.KeyOrder > 0).OrderBy(c => c.KeyOrder).Select(c => c.Name).ToList();
                Assert.Equal(new[] { "Organization", "Project" }, keys.Take(2));
                Assert.Equal(Enumerable.Range(1, keys.Count), s.Columns.Where(c => c.KeyOrder > 0).Select(c => c.KeyOrder).OrderBy(k => k));
                Assert.Equal(s.Columns.Count, s.Columns.Select(c => c.Name).Distinct().Count());
            });
        }

        [Fact]
        public void MasterData_MarksTheKeyColumnsIndexedInOrder()
        {
            var root = JsonNode.Parse(AdoTelemetryEvents.MasterDataJson())!.AsObject();
            var props = root["IoTSchemaProperties"]!.AsObject().Select(p => p.Value!.AsObject()).ToList();
            string[] Keys(string ev) => props.Where(p => (string)p["IoTEventDefinitionName"]! == ev && (string)p["IsIndexed"]! == "Yes")
                .OrderBy(p => int.Parse((string)p["IndexOrder"]!)).Select(p => (string)p["Name"]!).ToArray();
            Assert.Equal(new[] { "Organization", "Project", "BuildId" }, Keys("PipelineRun"));
            Assert.Equal(new[] { "Organization", "Project", "BuildId", "RecordId" }, Keys("PipelineStep"));
            Assert.All(root["<DM>IoTEventDefinition"]!.AsObject(), d => Assert.Equal("ADOTelemetry", (string)d.Value!["Folder"]!));
        }

        [Fact]
        public void PackagedMasterData_MatchesTheGeneratedDefinitions()
        {
            var packageDirectory = Path.Combine(AppContext.BaseDirectory, "resources", "audit_package", "Cmf.Audit", "Cmf.Audit.Data");
            var packaged = JsonNode.Parse(File.ReadAllText(Path.Combine(packageDirectory, "MasterData", "2.0.0", "001-ADOTelemetry-IoTEvents.json")));
            var generated = JsonNode.Parse(AdoTelemetryEvents.MasterDataJson());

            Assert.True(JsonNode.DeepEquals(generated, packaged));
        }

        [Fact]
        public void PackagedFolderWorkaround_CoversEveryEventAndRunsBeforeMasterData()
        {
            var packageDirectory = Path.Combine(AppContext.BaseDirectory, "resources", "audit_package", "Cmf.Audit", "Cmf.Audit.Data");
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(packageDirectory, "cmfpackage.json")))!;
            var contentTypes = manifest["contentToPack"]!.AsArray().Select(content => (string)content!["contentType"]!).ToArray();
            var registration = JsonNode.Parse(File.ReadAllText(Path.Combine(packageDirectory, "MasterData", "2.0.0", "000-AdoTelemetryFolderWorkaround.json")))!;
            var action = registration["<SM>DEEAction"]!["1"]!;
            var order = registration["DEEActionOrder"]!["1"]!;
            var actionPath = ((string)action["CodeFileRelativePath"]!).Replace('/', Path.DirectorySeparatorChar);
            var source = File.ReadAllText(Path.Combine(packageDirectory, "DEEs", actionPath));

            Assert.Contains("ProcessRulesPre", contentTypes);
            Assert.Contains("MasterData", contentTypes);
            Assert.Equal("BusinessObjects.IoTEventDefinitionCollection.Create.Pre", (string)order["ActionGroup"]!);
            Assert.Equal((string)action["Action"]!, (string)order["Action"]!);
            Assert.All(AdoTelemetryEvents.Schemas, schema => Assert.Contains($"\"{schema.Name}\"", source));
        }

        [Theory]
        [InlineData("ADORunChangesTyped")]
        [InlineData("ADOSprintsTyped")]
        [InlineData("ADOWorkItemFlowTyped")]
        public void PackagedDataSet_UsesEnqueueTimeForObservedAt(string name)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "resources", "audit_package", "Cmf.Audit", "Cmf.Audit.ExportedObjects", "ExportedObjects", "2.0.0", $"Dataset-{name}.xml");
            var query = (string)XDocument.Load(path).Root!.Element("Object")!.Element("QueryString")!.Attribute("value")!;
            Assert.Contains("SysProperties_EnqueueTime", query);
            Assert.DoesNotContain("_created_at", query);
        }

        [Theory]
        [InlineData("ADOEpicForecastTyped")]
        [InlineData("ADORemainingWorkTyped")]
        public void PackagedScopeDataSet_IsProjectAgnostic(string name)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "resources", "audit_package", "Cmf.Audit", "Cmf.Audit.ExportedObjects", "ExportedObjects", "2.0.0", $"Dataset-{name}.xml");
            var query = (string)XDocument.Load(path).Root!.Element("Object")!.Element("QueryString")!.Attribute("value")!;
            Assert.Contains("{Organization: String}", query);
            Assert.Contains("{Project: String}", query);
            Assert.Contains("{Feature: String}", query); // Scope = feature
            Assert.DoesNotContain("Solution Backlog", query); // the team comes from --teamFromIterationPath, not from a path convention
        }

        [Fact]
        public void ExportXml_HasTheKeysIndexedInOrderTheFolderAndAValidSchema()
        {
            foreach (var schema in AdoTelemetryEvents.Schemas)
            {
                var root = XDocument.Parse(AdoTelemetryEvents.ExportXml(schema)).Root!;
                Assert.Equal("CMF.ExportFile", root.Name.LocalName);
                var definition = root.Element("Object")!;
                Assert.Equal(schema.Name, (string)definition.Element("Name")!.Attribute("value")!);
                Assert.Equal("\\IoTEventDefinitions\\ADOTelemetry", (string)definition.Element("Folder")!.Element("ObjectRef")!.Attribute("RefName")!);
                Assert.Equal("True", (string)definition.Element("CreateDataSet")!.Attribute("value")!);

                var iot = definition.Element("IoTSchema")!.Element("Object")!;
                var props = iot.Element("SchemaProperties")!.Elements("Item").Select(i => i.Element("Object")!).ToList();
                Assert.Equal(schema.Columns.Count, props.Count);
                string V(XElement o, string n) => (string)o.Element(n)!.Attribute("value")!;
                var keys = props.Where(p => V(p, "IsIndexed") == "True").OrderBy(p => int.Parse(V(p, "IndexOrder"))).Select(p => V(p, "Name")).ToArray();
                Assert.Equal(schema.Columns.Where(c => c.KeyOrder > 0).OrderBy(c => c.KeyOrder).Select(c => c.Name), keys);
                Assert.Contains(props, p => V(p, "Name") == "Organization" && V(p, "DataType") == "String");
                Assert.Contains(props, p => V(p, "DataType") == "DateTime" || schema.Columns.All(c => c.Type != ColumnType.DateTime));

                var json = JsonNode.Parse(V(iot, "Schema"))!.AsObject();
                Assert.Equal(keys, json["required"]!.AsArray().Select(x => (string)x!));
                Assert.Equal(schema.Columns.Count, json["properties"]!.AsObject().Count);
                Assert.Equal("string", (string)json["properties"]![schema.Columns.First(c => c.Type == ColumnType.String).Name]!["type"]!);
            }
        }

        [Theory]
        [InlineData(null, "Version=11.2.0.0")]
        [InlineData("11.2.3", "Version=11.2.0.0")]
        [InlineData("11.1", "Version=11.1.0.0")]
        public void ExportXml_UsesTheMesVersionForTheAssemblyReferences(string? version, string expected)
        {
            var xml = AdoTelemetryEvents.ExportXml(AdoTelemetryEvents.PipelineRun, version);
            Assert.Contains($"Cmf.Foundation.BusinessObjects.IoTEventDefinition, Cmf.Foundation.BusinessObjects, {expected}", xml);
            Assert.Contains($"IoTSchemaProperty, Cmf.Foundation.BusinessObjects, {expected}", xml);
        }

        [Fact]
        public void EveryColumnHasAValueAndANameOnce()
        {
            foreach (var schema in AdoTelemetryEvents.Schemas)
            {
                Assert.All(schema.Columns, c => Assert.NotNull(c.Value));
                Assert.Equal(schema.Columns.Count, schema.Columns.Select(c => c.Name).Distinct().Count());
                var keys = schema.Columns.Where(c => c.KeyOrder > 0).Select(c => c.KeyOrder).OrderBy(x => x).ToList();
                Assert.Equal(Enumerable.Range(1, keys.Count), keys); // IndexOrder must be 1..n or the platform ignores it
            }
        }

        [Fact]
        public void EveryEventKeepsItsDataForever()
        {
            foreach (var schema in AdoTelemetryEvents.Schemas)
            {
                Assert.Equal(0, schema.TimeToLiveDays);
                Assert.Equal(0, schema.RetentionDays);
                var xml = AdoTelemetryEvents.ExportXml(schema);
                Assert.Contains("<TimeToLive value=\"0\"", xml);
                Assert.Contains("DataSetRetentionTime value=\"[]\"", xml);
                Assert.DoesNotContain("Delete", xml);
            }
        }
    }
}
