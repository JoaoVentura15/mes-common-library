using audit.Objects.PipelineRuns;
using audit.Services;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;
using static audit.Objects.PipelineRuns.PipelineRunsMapper;

namespace specs
{
    public class PipelineRunsTests
    {
        private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
        private static string? TagOf(PostTelemetry e, string key) => e.Tags.FirstOrDefault(t => t.Key == key)?.Value;
        private static decimal? Num(PostTelemetry e, string name) => e.Parameters.FirstOrDefault(p => p.Name == name)?.NumericValues.FirstOrDefault();

        private static PipelineRunsSettings Settings(string? identity = null, string? roles = null, string? pools = "Contoso", string? rolePrefixes = null, string? branchVersions = null) =>
            PipelineRunsSettings.From(new PipelineRunsOptions
            {
                azureDevOpsUrl = "https://dev.azure.com/example/",
                teamProject = "Contoso",
                days = 30,
                approvalStages = "ManualApproval,FinalApproval",
                identity = identity,
                pipelineRoles = roles,
                pipelineRolePrefixes = rolePrefixes,
                branchVersions = branchVersions,
                pools = pools,
            }, new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc));

        // Synthetic shapes modelled on typical PR and CD runs: PR-Validate 7002 queued PR-Publish 7003; CD-Containers 7001.
        private static readonly string PrChanges = """
            {"id":7002,"buildNumber":"merge_PR-Validate.7002","uri":"vstfs:///Build/Build/7002","result":"succeeded","reason":"pullRequest",
             "queueTime":"2026-10-05T05:41:19.056Z","startTime":"2026-10-05T05:41:23.700Z","finishTime":"2026-10-05T06:12:20.457Z",
             "sourceBranch":"refs/pull/2002/merge","sourceVersion":"aaa","definition":{"id":7,"name":"PR-Validate"},
             "repository":{"id":"00000000-0000-4000-8000-0000000000a1","name":"Contoso","type":"TfsGit"},
             "parameters":"{\"system.pullRequest.targetBranch\":\"refs/heads/development\",\"system.pullRequest.pullRequestIteration\":\"2\"}",
             "requestedFor":{"id":"44444444-4444-4444-4444-444444444444","displayName":"Dev"}}
            """;
        private static readonly string PrPackage = """
            {"id":7003,"buildNumber":"merge_PR-Publish.7003","uri":"vstfs:///Build/Build/7003","result":"succeeded","reason":"manual",
             "queueTime":"2026-10-05T05:48:18.215Z","startTime":"2026-10-05T05:48:23.983Z","finishTime":"2026-10-05T06:11:15.095Z",
             "sourceBranch":"refs/pull/2002/merge","definition":{"id":6,"name":"PR-Publish"},
             "repository":{"id":"00000000-0000-4000-8000-0000000000a1","name":"Contoso","type":"TfsGit"},
             "templateParameters":{"packages":"- packageId: Cmf.Custom.Business\n  version: 1.6.0\n- packageId: Cmf.Custom.Data\n  version: 1.6.0\n"}}
            """;
        private static readonly string CdRun = """
            {"id":7001,"buildNumber":"Release-7001","uri":"vstfs:///Build/Build/7001","result":"failed","reason":"manual",
             "queueTime":"2026-10-08T21:58:50.701Z","startTime":"2026-10-08T21:59:02.917Z","finishTime":"2026-10-11T08:48:23.089Z",
             "sourceBranch":"refs/heads/release/1.3/development","definition":{"id":1,"name":"CD-Containers"},
             "repository":{"id":"00000000-0000-4000-8000-0000000000a1","name":"Contoso","type":"TfsGit"},
             "templateParameters":{"Environment":"Contoso-ContosoStaging","RunTests":"True"}}
            """;
        private static readonly string CdTimeline = """
            {"records":[
             {"id":"s1","type":"Stage","name":"Build","identifier":"Build","result":"succeeded","startTime":"2026-10-08T22:00:00Z","finishTime":"2026-10-08T23:00:00Z"},
             {"id":"j1","parentId":"s1","type":"Job","name":"Build","result":"succeeded","workerName":"BUILD-AGENT-05","startTime":"2026-10-08T22:00:05Z","finishTime":"2026-10-08T23:00:00Z"},
             {"id":"t1","parentId":"j1","type":"Task","name":"Build Cmf.Custom.Business @ Cmf.Custom.Business","result":"succeeded","workerName":"BUILD-AGENT-05","startTime":"2026-10-08T22:10:00Z","finishTime":"2026-10-08T22:16:00Z","attempt":1,"order":17},
             {"id":"s2","type":"Stage","name":"RunTests","identifier":"RunTests","result":"failed","startTime":"2026-10-08T23:00:00Z","finishTime":"2026-10-10T23:00:00Z"},
             {"id":"s3","type":"Stage","name":"Manual Approval","identifier":"ManualApproval","result":"succeeded","startTime":"2026-10-10T23:00:00Z","finishTime":"2026-10-11T08:40:00Z"},
             {"id":"c1","parentId":"s3","type":"Checkpoint","name":"Checkpoint","result":"succeeded"},
             {"id":"a1","parentId":"c1","type":"Checkpoint.Approval","name":"Checkpoint.Approval","result":"succeeded","startTime":"2026-10-10T23:00:00Z","finishTime":"2026-10-11T08:40:00Z"},
             {"id":"s4","type":"Stage","name":"FinalApproval","identifier":"FinalApproval","result":"skipped"}
            ]}
            """;

        private FakeAdo Ado() => new()
        {
            Builds = { J(PrChanges), J(PrPackage), J(CdRun) },
            Timelines = { [7001] = J(CdTimeline) },
            TestRuns = { ["vstfs:///Build/Build/7001"] = new() { J("""{"id":8001,"name":"E2E Tests","state":"Completed","totalTests":500,"passedTests":498,"startedDate":"2026-10-09T00:00:00Z","completedDate":"2026-10-10T00:00:00Z"}""") } },
            FailedResults =
            {
                [8001] = new()
                {
                    J("""{"id":8003,"outcome":"Failed","automatedTestName":"Contoso.E2E.Lots.TrackIn_Works","testCaseTitle":"TrackIn_Works","automatedTestStorage":"Contoso.E2E.dll","durationInMs":7557,"errorMessage":"Assert.Equal() Failure\nExpected: 1","failingSince":{"date":"2026-09-30T10:00:00Z","build":{"id":7005}},"owner":{"id":"11111111-1111-1111-1111-111111111111"}}"""),
                    J("""{"id":8004,"outcome":"Aborted","testCaseTitle":"Timeout_Test","durationInMs":0}"""),
                },
            },
            Approvals = { J("""{"id":"a1","status":"approved","createdOn":"2026-10-10T23:00:00Z","lastModifiedOn":"2026-10-11T08:40:00Z","steps":[{"actualApprover":{"id":"22222222-2222-2222-2222-222222222222","displayName":"Approver"},"status":"approved","comment":"Local evidence: https://teams/msg/1"}]}""") },
            Changes = { [7001] = new() { J("""{"id":"abc1234","message":"Merged PR 2001: Migrate to the new agents"}"""), J("""{"id":"abc","message":"bump version"}""") } },
            PullRequests = { J("""{"pullRequestId":2001,"status":"completed","title":"Migrate","creationDate":"2026-10-13T09:12:00Z","closedDate":"2026-10-15T10:00:00Z","sourceRefName":"refs/heads/a","targetRefName":"refs/heads/development","mergeStatus":"succeeded","isDraft":false,"createdBy":{"id":"33333333-3333-3333-3333-333333333333","displayName":"Dev"},"repository":{"name":"Contoso","id":"00000000-0000-4000-8000-0000000000a1"},"lastMergeCommit":{"commitId":"abc1234"}}""") },
            PrWorkItems = { [2001] = new() { J("""{"id":"6005"}""") } },
            WorkItems = { J("""{"id":6005,"fields":{"System.AreaPath":"Contoso\\Squad A","System.WorkItemType":"Bug"}}""") },
            Pools = { J("""{"id":13,"name":"Contoso","isHosted":false}"""), J("""{"id":2,"name":"Hosted","isHosted":true}""") },
            Jobs = { [13] = new() { J("""{"requestId":8002,"queueTime":"2026-10-08T11:01:10Z","assignTime":"2026-10-08T11:01:14Z","finishTime":"2026-10-08T12:23:56Z","result":"succeeded","reservedAgent":{"name":"BUILD-AGENT-05"},"owner":{"id":7006},"definition":{"name":"PR-Publish"},"planType":"Build"}""") } },
        };

        private async Task<List<PostTelemetry>> Collect(PipelineRunsSettings? settings = null) =>
            await new PipelineRunsCollector(Ado(), settings ?? Settings()).CollectAsync();

        [Theory]
        [InlineData("https://dev.azure.com/example/", "example")]
        [InlineData("https://example.visualstudio.com/", "example")]
        public void OrganizationFromUrl_HandlesBothUrlForms(string url, string expected) =>
            Assert.Equal(expected, PipelineRunsSettings.OrganizationFromUrl(url));

        [Theory]
        [InlineData("PR-Validate", "PR")]
        [InlineData("PR-Publish", "PR")]
        [InlineData("CI-Changes", "CI")]
        [InlineData("CI-Package", "CI")]
        [InlineData("CI-Publish", "Publish")]
        [InlineData("CD-Containers", "CD")]
        [InlineData("Backup Environment Pipeline", "Other")]
        public void ResolveRole_FollowsCliNamingConventions(string pipeline, string role) =>
            Assert.Equal(role, ResolveRole(pipeline, Settings()));

        [Fact]
        public void ResolveRole_ConfiguredPrefixesReplaceTheConvention()
        {
            var s = Settings(rolePrefixes: "Validate-=PR,Build-=CI,Build-Publish=Publish,Deploy-=CD");
            Assert.Equal("PR", ResolveRole("Validate-Main", s));
            Assert.Equal("CI", ResolveRole("Build-Main", s));
            Assert.Equal("Publish", ResolveRole("Build-Publish-Main", s));
            Assert.Equal("CD", ResolveRole("Deploy-Staging", s));
            Assert.Equal("Other", ResolveRole("PR-Validate", s));
        }

        [Fact]
        public void ResolveRole_PrefixesCanBeTurnedOff()
        {
            var s = Settings(rolePrefixes: "none", roles: "Gate=PR");
            Assert.Equal("Other", ResolveRole("PR-Validate", s));
            Assert.Equal("PR", ResolveRole("Gate", s));
        }

        [Fact]
        public void ResolveRole_ExplicitMappingWins() =>
            Assert.Equal("Other", ResolveRole("CD-ContainersOnClient", Settings(roles: "CD-ContainersOnClient=Other")));

        [Fact]
        public void FindParents_LinksPackageRunToItsChangesRun()
        {
            var parents = PipelineRunsCollector.FindParents(new List<JsonElement> { J(PrChanges), J(PrPackage), J(CdRun) }, Settings());
            Assert.Equal(7002L, parents[7003]);
            Assert.False(parents.ContainsKey(7002));
            Assert.False(parents.ContainsKey(7001));
        }

        [Fact]
        public async Task PipelineRun_CarriesRoleEnvironmentAndReleaseOutcome()
        {
            var cd = (await Collect()).Single(e => e.Area.Name == AreaRun && e.Resource.Name == "7001");
            Assert.Equal("CD", TagOf(cd, "Role"));
            Assert.Equal("Contoso-ContosoStaging", TagOf(cd, "Environment"));
            Assert.Equal(OutcomeReleasedWithFailures, TagOf(cd, "ReleaseOutcome"));
            Assert.Equal((decimal?)2, Num(cd, "FailedTests"));
            Assert.Equal("Windows", TagOf(cd, "Platform"));
            Assert.Equal("example", cd.Enterprise.Name);
            Assert.Equal("Contoso", cd.Site.Name);
            Assert.Equal("Contoso", cd.Facility.Name);
            Assert.Equal("CD-Containers", cd.Flow?.Name);
        }

        [Fact]
        public async Task Timeline_MarksOnlyConfiguredApprovalStages()
        {
            var typed = AdoTelemetryEvents.Convert(await Collect());
            var approval = typed.Single(item => item.Data.TryGetValue("RecordId", out var id) && (string?)id == "s3");
            var build = typed.Single(item => item.Data.TryGetValue("RecordId", out var id) && (string?)id == "s1");
            Assert.Equal(1L, approval.Data["IsApprovalStage"]);
            Assert.Equal(0L, build.Data["IsApprovalStage"]);
        }

        [Fact]
        public async Task PipelineRun_PackageRunLinksParentPrAndPackages()
        {
            var run = (await Collect()).Single(e => e.Area.Name == AreaRun && e.Resource.Name == "7003");
            Assert.Equal("7002", TagOf(run, "ParentBuildId"));
            Assert.Equal("2002", TagOf(run, "PullRequestId"));
            Assert.Equal("1.6.0", TagOf(run, "Version"));
            Assert.Equal((decimal?)2, Num(run, "PackageCount"));
            Assert.Equal(2, run.Parameters.First(p => p.Name == "Duration").Timestamps.Count);
        }

        [Fact]
        public async Task Timeline_MapsStagesAndStepsWithCategoryPackageAndStage()
        {
            var events = await Collect();
            Assert.Equal(4, events.Count(e => e.Area.Name == AreaStage));
            var step = events.Single(e => e.Area.Name == AreaStep && e.Step?.Name == "Build Cmf.Custom.Business @ Cmf.Custom.Business");
            Assert.Equal("build", TagOf(step, "Category"));
            Assert.Equal("Cmf.Custom.Business", TagOf(step, "Package"));
            Assert.Equal("Build", TagOf(step, "StageName"));
            Assert.Equal((decimal?)360, Num(step, "Duration"));
        }

        private static readonly string FailedStepTimeline = """
            {"records":[
             {"id":"t9","type":"Task","name":"Publish","result":"failed","errorCount":1,"warningCount":2,"startTime":"2026-10-08T22:10:00Z","finishTime":"2026-10-08T22:16:00Z",
              "issues":[{"type":"warning","message":"slow feed"},
                        {"type":"error","message":"MSB3073: copy failed for C:\\agent\\_work\\1\\s\\a.dll by CONTOSO\\jdoe at https://dev.azure.com/org/x\nsecond line"}]},
             {"id":"t10","type":"Task","name":"Quiet","result":"succeeded","startTime":"2026-10-08T22:10:00Z","finishTime":"2026-10-08T22:16:00Z"}
            ]}
            """;

        private static string? TextOf(PostTelemetry e, string name) => e.Parameters.FirstOrDefault(p => p.Name == name)?.StringValues.FirstOrDefault();

        [Fact]
        public void Timeline_FailedStepCarriesErrorCodeScrubbedMessageAndWarnings()
        {
            var events = MapTimeline(J(CdRun), J(FailedStepTimeline), Settings());
            var step = events.Single(e => e.Step?.Name == "Publish");
            Assert.Equal("MSB3073", TagOf(step, "ErrorCode"));
            var message = TextOf(step, "ErrorMessage");
            Assert.Equal("MSB3073: copy failed for <path>\\a.dll by <account> at <url>", message);
            Assert.DoesNotContain("jdoe", message);
            Assert.Equal((decimal?)2, Num(step, "Warnings"));
            Assert.Null(TagOf(events.Single(e => e.Step?.Name == "Quiet"), "ErrorCode"));
        }

        [Fact]
        public void TestFailure_ErrorTextIsScrubbed()
        {
            var result = J("""{"automatedTestName":"Contoso.T1","outcome":"Failed","errorMessage":"System.IO.IOException: cannot open D:\\agent\\_work\\s\\x.dat for CONTOSO\\jdoe\nstack"}""");
            var failure = MapTestFailure(J(CdRun), J("""{"id":1,"name":"run"}"""), result, Settings());
            Assert.Equal("System.IO.IOException: cannot open <path>\\x.dat for <account>", failure.Parameters.First(p => p.Name == "Error").StringValues[0]);
        }

        [Fact]
        public void Timeline_StepErrorsCanBeTurnedOff()
        {
            var off = PipelineRunsSettings.From(new PipelineRunsOptions { azureDevOpsUrl = "https://dev.azure.com/example/", teamProject = "Contoso", stepErrors = false }, new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc));
            var step = MapTimeline(J(CdRun), J(FailedStepTimeline), off).Single(e => e.Step?.Name == "Publish");
            Assert.Null(TagOf(step, "ErrorCode"));
            Assert.Null(TextOf(step, "ErrorMessage"));
        }

        [Fact]
        public async Task Approval_HasStageApproverWaitAndComment()
        {
            var approval = (await Collect()).Single(e => e.Area.Name == AreaApproval);
            Assert.Equal("Manual Approval", approval.Step?.Name);
            Assert.Equal("22222222-2222-2222-2222-222222222222", TagOf(approval, "Approver"));
            Assert.Equal((decimal?)34800, Num(approval, "Wait"));
            Assert.Contains("teams", approval.Parameters.First(p => p.Name == "Comment").StringValues[0]);
        }

        [Fact]
        public async Task Tests_RunTotalsAndFailuresWithFailingSince()
        {
            var events = await Collect();
            var run = events.Single(e => e.Area.Name == AreaTestRun);
            Assert.Equal((decimal?)500, Num(run, "Total"));
            Assert.Equal((decimal?)2, Num(run, "Failed"));

            var failure = events.Single(e => e.Area.Name == AreaTestFailure && e.Resource.Name == "Contoso.E2E.Lots.TrackIn_Works");
            Assert.Equal("7005", TagOf(failure, "FailingSinceBuildId"));
            Assert.True(Num(failure, "FailingFor") > 10);
            Assert.Equal("Assert.Equal() Failure", failure.Parameters.First(p => p.Name == "Error").StringValues[0]);
            Assert.Contains(events, e => e.Area.Name == AreaTestFailure && e.Resource.Name == "Timeout_Test" && TagOf(e, "Outcome") == "Aborted");
        }

        [Fact]
        public async Task RunChanges_ListCommitsAndMergedPullRequests()
        {
            var change = (await Collect()).Single(e => e.Area.Name == AreaRunChange);
            Assert.Equal(2, change.Tags.Count(t => t.Key == "Commit"));
            Assert.Equal("2001", TagOf(change, "PullRequestId"));
        }

        [Fact]
        public async Task PullRequest_HasWorkItemAreaPathAndAuthorId()
        {
            var pr = (await Collect()).Single(e => e.Area.Name == AreaPullRequest);
            Assert.Equal("Contoso\\Squad A", TagOf(pr, "AreaPath"));
            Assert.Equal("Bug", TagOf(pr, "WorkItemType"));
            Assert.Equal("33333333-3333-3333-3333-333333333333", TagOf(pr, "Author"));
        }

        [Fact]
        public async Task AgentJob_IsKeyedByAgentWithWaitAndBusy()
        {
            var job = (await Collect()).Single(e => e.Area.Name == AreaAgentJob);
            Assert.Equal("BUILD-AGENT-05", job.Resource.Name);
            Assert.Equal("Contoso", TagOf(job, "Pool"));
            Assert.Equal((decimal?)4, Num(job, "Wait"));
            Assert.Equal((decimal?)4962, Num(job, "Busy"));
        }

        [Fact]
        public async Task IdentityNone_PostsNoPeople()
        {
            var events = await Collect(Settings(identity: "none"));
            Assert.DoesNotContain(events.SelectMany(e => e.Tags), t => t.Key is "Author" or "Approver" or "RequestedFor" or "Owner");
        }

        [Theory]
        [InlineData("""{"records":[{"type":"Stage","name":"ManualApproval","result":"succeeded"},{"type":"Stage","name":"FinalApproval","result":"succeeded"}]}""", 0, OutcomeReleasedClean)]
        [InlineData("""{"records":[{"type":"Stage","name":"ManualApproval","result":"succeeded"}]}""", 3, OutcomeReleasedWithFailures)]
        [InlineData("""{"records":[{"type":"Stage","name":"ManualApproval","result":"rejected"}]}""", 0, OutcomeNotReleased)]
        [InlineData("""{"records":[{"type":"Stage","name":"Deploy","result":"succeeded"}]}""", 0, null)]
        public void ReleaseOutcome_Rules(string timeline, int failedTests, string? expected) =>
            Assert.Equal(expected, ReleaseOutcome(J(timeline), Settings(), failedTests));

        [Fact]
        public async Task Payload_WritesFlowAndOmitsMissingLevels()
        {
            var cd = (await Collect()).Single(e => e.Area.Name == AreaRun && e.Resource.Name == "7001");
            var json = JsonSerializer.Serialize(cd);
            Assert.Contains("\"Flow\":{\"Name\":\"CD-Containers\"}", json);
            Assert.DoesNotContain("\"Step\":null", json);
        }

        [Theory]
        [InlineData(HttpStatusCode.OK)]
        [InlineData(HttpStatusCode.NoContent)]
        public async Task RestClient_EmptyBody_IsTreatedAsNoData(HttpStatusCode status)
        {
            using var client = new AzureDevOpsRestClient("https://dev.azure.com/example/", "pat", new EmptyBodyHandler(status));
            Assert.Null(await client.GetTimelineAsync("Contoso", 7004));
            Assert.Empty(await client.GetBuildChangesAsync("Contoso", 7004));
        }

        [Theory]
        [InlineData("active", false)]
        [InlineData("abandoned", false)]
        [InlineData("completed", true)]
        public void PullRequest_OnlyCompletedRequestsHaveAMergeTime(string status, bool merged)
        {
            var pr = J("""{"pullRequestId":1,"status":"STATUS","creationDate":"2026-10-07T05:26:00Z","closedDate":"2026-10-16T14:23:00Z"}""".Replace("STATUS", status));
            var mapped = MapPullRequest(pr, new(), Settings());
            Assert.Equal(merged, Num(mapped, "Merged") != null);
            var active = MapPullRequest(J("""{"pullRequestId":2,"status":"active","creationDate":"2026-10-07T05:26:00Z"}"""), new(), Settings());
            Assert.Null(Num(active, "Open"));
        }

        private class EmptyBodyHandler(HttpStatusCode status) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("") });
        }

        private static PipelineRunsSettings FlowSettings(bool history = true, string identity = "id") => PipelineRunsSettings.From(new PipelineRunsOptions
        {
            azureDevOpsUrl = "https://dev.azure.com/example/", teamProject = "Contoso",
            workItemFields = "TargetPackageVersion=Custom.PackageVersion", workItemHistory = history,
            teamFromIterationPath = @"^[^\\]+\\[^\\]+\\([^\\]+)\\",
            sprintFromIterationPath = @"\\(Sprint \d+)$", branchVersions = "development=1.6", identity = identity, releaseKinds = "GoLive=Go-Live,Roadmap=Roadmap", releaseTitleContains = "Package Release",
        }, new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc));

        private static JsonElement FlowPr => J("""
            {"pullRequestId":2003,"status":"completed","creationDate":"2026-10-07T05:26:00Z","closedDate":"2026-10-16T14:23:00Z",
             "targetRefName":"refs/heads/development","repository":{"name":"Contoso"},"createdBy":{"id":"author"}}
            """);

        private static JsonElement FlowItem => J("""
            {"id":6004,"fields":{"System.WorkItemType":"Bug","System.State":"Resolved","System.AreaPath":"Contoso\\Platform\\Integration",
             "System.IterationPath":"Contoso\\Product Backlog\\Team A\\Sprint 79","Custom.PackageVersion":"1.6.0"}}
            """);

        private static List<JsonElement> FlowRevisions => new()
        {
            J("""{"rev":1,"fields":{"System.State":"Estimated","System.ChangedDate":"2026-10-05T00:00:00Z","System.IterationPath":"Contoso\\Product Backlog\\Team A\\Sprint 78"}}"""),
            J("""{"rev":2,"fields":{"System.State":"Active","System.ChangedDate":"2026-10-07T13:59:00Z","System.IterationPath":"Contoso\\Product Backlog\\Team A\\Sprint 78"}}"""),
            J("""{"rev":3,"fields":{"System.State":"Active","System.ChangedDate":"2026-10-13T00:00:00Z","System.IterationPath":"Contoso\\Product Backlog\\Team A\\Sprint 79"}}"""),
            J("""{"rev":4,"fields":{"System.State":"Resolved","System.ChangedDate":"2026-10-16T14:53:00Z","System.IterationPath":"Contoso\\Product Backlog\\Team A\\Sprint 79","System.ChangedBy":{"id":"resolver-guid","displayName":"Reviewer"}}}"""),
        };

        [Fact]
        public void PullRequest_ReviewMomentsUseVotesNotCurrentReviewerState()
        {
            var threads = new List<JsonElement>
            {
                J("""{"comments":[{"commentType":"text","publishedDate":"2026-10-07T06:00:00Z","author":{"id":"author"}}]}"""),
                J("""{"comments":[{"commentType":"text","publishedDate":"2026-10-08T12:26:00Z","author":{"id":"reviewer"}}]}"""),
                J("""{"publishedDate":"2026-10-16T14:04:00Z","properties":{"CodeReviewVoteResult":{"$value":10},"CodeReviewVotedByIdentity":{"$value":"1"}},"identities":{"1":{"id":"reviewer"}}}"""),
                J("""{"publishedDate":"2026-10-16T14:08:00Z","properties":{"CodeReviewVoteResult":{"$value":"10"},"CodeReviewVotedByIdentity":{"$value":"1"}},"identities":{"1":{"id":"reviewer"}}}"""),
                J("""{"publishedDate":"2026-10-16T14:11:00Z","properties":{"CodeReviewVoteResult":{"$value":"10"},"CodeReviewVotedByIdentity":{"$value":"2"}},"identities":{"2":{"id":"reviewer-2"}}}"""),
                J("""{"publishedDate":"2026-10-16T14:12:00Z","properties":{"CodeReviewVoteResult":{"$value":0}}}"""),
            };
            var mapped = MapPullRequest(FlowPr, new(), FlowSettings(), threads, new() { J("{}"), J("{}"), J("{}") });
            Assert.Equal((decimal?)111600, Num(mapped, "TimeToFirstReview"));
            Assert.Equal((decimal?)1140, Num(mapped, "ApprovalToMerge"));
            Assert.Equal((decimal?)Unix(DateTime.Parse("2026-10-16T14:11:00Z").ToUniversalTime()), Num(mapped, "LastApproval"));
            Assert.Equal((decimal?)2, Num(mapped, "Approvers"));
            Assert.Equal((decimal?)2, Num(mapped, "Pushes"));
            Assert.Null(Num(MapPullRequest(FlowPr, new(), FlowSettings(), new()), "FirstApproval"));
        }

        [Fact]
        public void WorkItemFlow_TracksCarryOverAndManualResolution()
        {
            var mapped = MapWorkItemFlow(FlowPr, FlowItem, FlowRevisions, FlowSettings());
            Assert.Equal("2003:6004", mapped.Resource.Name);
            Assert.Equal("Team A", TagOf(mapped, "Team"));
            Assert.Equal("Sprint 79", TagOf(mapped, "Sprint"));
            Assert.Equal("Contoso\\Platform\\Integration", TagOf(mapped, "AreaPath"));
            Assert.Equal((decimal?)1, Num(mapped, "CarriedOver"));
            Assert.Equal((decimal?)1, Num(mapped, "IterationChanges"));
            Assert.Equal((decimal?)1800, Num(mapped, "MergeToResolved"));
            Assert.Equal("resolver-guid", TagOf(mapped, "Resolver"));
            Assert.Equal((decimal?)0, Num(mapped, "VersionMismatch"));
            Assert.Null(TagOf(MapWorkItemFlow(FlowPr, FlowItem, FlowRevisions, FlowSettings(identity: "none")), "Resolver"));
        }

        [Fact]
        public void WorkItemFlow_StoryPointsUseTheEstimateAtResolutionAndStayNullWhenMissing()
        {
            // Current value 8, but the resolving revision (rev 4) said 5: the estimate at resolution wins.
            var item = J(FlowItem.GetRawText().Replace("\"fields\":{", "\"fields\":{\"Microsoft.VSTS.Scheduling.StoryPoints\":8,"));
            var history = FlowRevisions;
            history[3] = J(history[3].GetRawText().Replace("\"fields\":{", "\"fields\":{\"Microsoft.VSTS.Scheduling.StoryPoints\":5,"));
            Assert.Equal((decimal?)5, Num(MapWorkItemFlow(FlowPr, item, history, FlowSettings()), "StoryPoints"));
            // Resolving revision without points: fall back to the current value.
            Assert.Equal((decimal?)8, Num(MapWorkItemFlow(FlowPr, item, FlowRevisions, FlowSettings()), "StoryPoints"));
            // Never estimated: no parameter, so the typed column is NULL and not 0.
            Assert.Null(Num(MapWorkItemFlow(FlowPr, FlowItem, FlowRevisions, FlowSettings()), "StoryPoints"));
        }

        [Fact]
        public void WorkItemFlow_ReopenedItemIsNotResolvedAndMissingHistoryIsUnknown()
        {
            var reopened = J(FlowItem.GetRawText().Replace("\"Resolved\"", "\"Active\""));
            var history = FlowRevisions;
            history.Add(J("""{"rev":5,"fields":{"System.State":"Active","System.ChangedDate":"2026-10-17T00:00:00Z"}}"""));
            var mapped = MapWorkItemFlow(FlowPr, reopened, history, FlowSettings());
            Assert.Null(Num(mapped, "Resolved"));
            Assert.Null(Num(mapped, "MergeToResolved"));
            Assert.Null(TagOf(mapped, "Resolver"));
            Assert.Equal((decimal?)1, Num(mapped, "MergedNotDone"));
            var noHistory = MapWorkItemFlow(FlowPr, FlowItem, null, FlowSettings(false));
            Assert.Null(Num(noHistory, "CarriedOver"));
            Assert.Null(Num(noHistory, "Resolved"));
            Assert.Equal((decimal?)0, Num(noHistory, "HistoryAvailable"));
        }

        [Fact]
        public void WorkItemHistory_KeepsOneRowPerStateOrIterationChangeKeyedByRevision()
        {
            var rows = MapWorkItemHistory(FlowItem, FlowRevisions, FlowSettings());
            Assert.Equal(new[] { "Created", "State", "Iteration", "State" }, rows.Select(r => TagOf(r, "ChangeKind")).ToArray());
            Assert.Equal(new[] { "1", "2", "3", "4" }, rows.Select(r => TagOf(r, "Rev")).ToArray());
            Assert.Equal("Estimated", TagOf(rows[1], "PreviousState"));
            Assert.Equal("Active", TagOf(rows[1], "State"));
            Assert.Equal("Contoso\\Product Backlog\\Team A\\Sprint 78", TagOf(rows[2], "PreviousIterationPath"));
            Assert.Equal("Sprint 79", TagOf(rows[2], "Sprint"));
            Assert.Equal("resolver-guid", TagOf(rows[3], "ChangedBy"));
            Assert.Equal((decimal?)780840, Num(rows[3], "InPreviousState"));
            Assert.Null(TagOf(MapWorkItemHistory(FlowItem, FlowRevisions, FlowSettings(identity: "none"))[3], "ChangedBy"));
            Assert.Empty(MapWorkItemHistory(FlowItem, null, FlowSettings()));
        }

        private static List<JsonElement> ScheduleRevisions => new()
        {
            J("""{"rev":1,"fields":{"System.Title":"V1.3.18 - Go-Live Package Release","System.State":"New","System.Parent":6003,"System.ChangedDate":"2026-10-21T14:00:00Z"}}"""),
            J("""{"rev":2,"fields":{"System.Title":"V1.3.18 - Go-Live Package Release","System.State":"New","System.Parent":6003,"System.ChangedDate":"2026-10-21T15:00:00Z","Microsoft.VSTS.Scheduling.StartDate":"2026-10-22T00:00:00Z","Microsoft.VSTS.Scheduling.TargetDate":"2026-11-04T00:00:00Z","System.ChangedBy":{"id":"author-guid","displayName":"Author"}}}"""),
            J("""{"rev":3,"fields":{"System.Title":"V1.3.18 - Go-Live Package Release","System.State":"Active","System.Parent":6003,"System.ChangedDate":"2026-10-22T09:00:00Z","Microsoft.VSTS.Scheduling.StartDate":"2026-10-22T00:00:00Z","Microsoft.VSTS.Scheduling.TargetDate":"2026-11-04T00:00:00Z"}}"""),
            J("""{"rev":4,"fields":{"System.Title":"V1.3.18 - Go-Live Package Release","System.State":"Active","System.Parent":6003,"System.ChangedDate":"2026-10-26T07:00:00Z","Microsoft.VSTS.Scheduling.StartDate":"2026-10-20T00:00:00Z","Microsoft.VSTS.Scheduling.TargetDate":"2026-10-27T00:00:00Z","System.ChangedBy":{"id":"editor-guid","displayName":"Editor"}}}"""),
            J("""{"rev":5,"fields":{"System.Title":"V1.3.18 - Go-Live Package Release","System.State":"Active","System.Parent":6003,"System.ChangedDate":"2026-10-27T08:00:00Z","Microsoft.VSTS.Scheduling.StartDate":"2026-10-20T00:00:00Z","Microsoft.VSTS.Scheduling.TargetDate":"2026-10-28T00:00:00Z"}}"""),
            J("""{"rev":6,"fields":{"System.Title":"V1.3.18 - Go-Live Package Release","System.State":"Active","System.Parent":6003,"System.ChangedDate":"2026-10-28T08:00:00Z"}}"""),
        };

        private static JsonElement ScopeStory(string extraFields = "", string state = "Resolved", string path = "Contoso\\\\Product Backlog\\\\Team B\\\\Sprint 75") => J(
            "{\"id\":6006,\"fields\":{\"System.WorkItemType\":\"User Story\",\"System.Title\":\"Map recipe\",\"System.State\":\"" + state + "\",\"System.AreaPath\":\"Contoso\\\\Platform\\\\PLM\"," +
            "\"System.IterationPath\":\"" + path + "\",\"System.Parent\":6007,\"System.CreatedDate\":\"2026-08-03T09:00:00Z\",\"System.ChangedDate\":\"2026-10-20T10:00:00Z\"" + extraFields + "}}");

        private static readonly JsonElement ScopeFeature = J("""{"id":6007,"fields":{"System.WorkItemType":"Feature","System.Title":"Recipe mapping","System.Parent":6008}}""");
        private static readonly JsonElement ScopeEpic = J("""{"id":6008,"fields":{"System.WorkItemType":"Epic","System.Title":"Alpha"}}""");

        [Fact]
        public void WorkItemScope_IsDoneByItsResolvedDateEvenInAStateThatIsNotListedAsDone()
        {
            var item = ScopeStory(",\"Microsoft.VSTS.Common.ResolvedDate\":\"2026-10-19T08:00:00Z\",\"Microsoft.VSTS.Scheduling.StoryPoints\":5", state: "IT Tested");
            var t = MapWorkItemScope(item, ScopeFeature, ScopeEpic, FlowSettings())!;
            Assert.Equal("6006", t.Resource.Name);
            Assert.Equal("6006", TagOf(t, "WorkItemId"));
            Assert.Equal("IT Tested", TagOf(t, "State"));
            Assert.Equal("Team B", TagOf(t, "Team"));
            Assert.Equal("Sprint 75", TagOf(t, "Sprint"));
            Assert.Equal("6007", TagOf(t, "ParentId"));
            Assert.Equal("6007", TagOf(t, "FeatureId"));
            Assert.Equal("Recipe mapping", TagOf(t, "FeatureTitle"));
            Assert.Equal("6008", TagOf(t, "EpicId"));
            Assert.Equal("Alpha", TagOf(t, "EpicTitle"));
            Assert.Equal((decimal?)1, Num(t, "Done"));
            Assert.Equal((decimal?)0, Num(t, "Removed"));
            Assert.Equal((decimal?)5, Num(t, "StoryPoints"));
            Assert.Equal((decimal?)Unix(new DateTime(2026, 10, 19, 8, 0, 0)), Num(t, "DoneAt"));
            Assert.Equal((decimal?)Unix(new DateTime(2026, 8, 3, 9, 0, 0)), Num(t, "CreatedAt"));
        }

        [Fact]
        public void WorkItemScope_OpenItemHasNoDoneDateNoPointsAndNoTeamOutsideATeamIteration()
        {
            var t = MapWorkItemScope(ScopeStory(state: "Active", path: "Contoso\\\\Product Backlog"), null, null, FlowSettings())!;
            Assert.Equal((decimal?)0, Num(t, "Done"));
            Assert.Null(Num(t, "DoneAt"));
            Assert.Null(Num(t, "ResolvedAt"));
            Assert.Null(Num(t, "StoryPoints"));
            Assert.Null(TagOf(t, "Team"));
            Assert.Null(TagOf(t, "FeatureId"));
            Assert.Null(TagOf(t, "EpicId"));
        }

        [Fact]
        public void WorkItemScope_UsesTheStateChangeDateWhenADoneStateHasNoResolvedOrClosedDate()
        {
            var item = ScopeStory(",\"Microsoft.VSTS.Common.StateChangeDate\":\"2026-10-18T07:00:00Z\"", state: "Closed");
            var t = MapWorkItemScope(item, null, null, FlowSettings())!;
            Assert.Equal((decimal?)1, Num(t, "Done"));
            Assert.Equal((decimal?)Unix(new DateTime(2026, 10, 18, 7, 0, 0)), Num(t, "DoneAt"));
        }

        [Fact]
        public void WorkItemScope_ARemovedItemIsNeverDoneAndAnItemWithoutIdOrCreationDateIsSkipped()
        {
            var removed = MapWorkItemScope(ScopeStory(",\"Microsoft.VSTS.Common.ClosedDate\":\"2026-10-01T00:00:00Z\"", state: "Removed"), null, null, FlowSettings())!;
            Assert.Equal((decimal?)0, Num(removed, "Done"));
            Assert.Equal((decimal?)1, Num(removed, "Removed"));
            Assert.Null(Num(removed, "DoneAt"));
            Assert.Null(MapWorkItemScope(J("""{"id":1,"fields":{"System.State":"New"}}"""), null, null, FlowSettings()));
            Assert.Null(MapWorkItemScope(J("""{"fields":{"System.CreatedDate":"2026-08-03T09:00:00Z"}}"""), null, null, FlowSettings()));
        }

        [Fact]
        public async Task WorkItemScope_ReadsTheConfiguredFieldsStatesAndTypesOfAnotherProject()
        {
            var settings = PipelineRunsSettings.From(new PipelineRunsOptions
            {
                azureDevOpsUrl = "https://dev.azure.com/example/", teamProject = "Other", include = "scope", scopeTypes = "Product Backlog Item",
                milestoneField = "Custom.Release", storyPointsField = "Microsoft.VSTS.Scheduling.Effort", removedStates = "Cut,Dropped", featureType = "Capability", epicType = "Initiative",
            }, new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc));
            var item = J("{\"id\":7001,\"fields\":{\"System.WorkItemType\":\"Product Backlog Item\",\"System.State\":\"Cut\",\"System.CreatedDate\":\"2026-08-03T09:00:00Z\",\"System.Parent\":7002,\"Custom.Release\":\"R1\",\"Microsoft.VSTS.Scheduling.Effort\":8,\"Microsoft.VSTS.Scheduling.StoryPoints\":3}}");
            var t = MapWorkItemScope(item, null, null, settings)!;
            Assert.Equal("R1", TagOf(t, "Milestone"));
            Assert.Equal((decimal?)8, Num(t, "StoryPoints"));
            Assert.Equal((decimal?)1, Num(t, "Removed"));
            Assert.Equal((decimal?)0, Num(t, "Done"));
            var ado = new FakeAdo();
            ado.QueryIds.Add(7001);
            ado.WorkItems.AddRange(new[] { item, J("""{"id":7002,"fields":{"System.WorkItemType":"Capability","System.Title":"Cap","System.Parent":7003}}"""), J("""{"id":7003,"fields":{"System.WorkItemType":"Initiative","System.Title":"Init"}}""") });
            var typed = Assert.Single(AdoTelemetryEvents.Convert(await new PipelineRunsCollector(ado, settings).CollectAsync()));
            Assert.Equal(7003L, typed.Data["EpicId"]);
            Assert.Equal(7002L, typed.Data["FeatureId"]);
            Assert.Contains("'Cut', 'Dropped'", ado.LastWiql);
            // no milestone field: the column stays empty
            var plain = PipelineRunsSettings.From(new PipelineRunsOptions { azureDevOpsUrl = "https://dev.azure.com/example/", teamProject = "Other", include = "scope", milestoneField = "" }, new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc));
            Assert.Null(TagOf(MapWorkItemScope(item, null, null, plain)!, "Milestone"));
        }

        [Fact]
        public void WorkItemScope_ConvertsToTypedRowsKeyedByWorkItem()
        {
            var item = ScopeStory(",\"Microsoft.VSTS.Common.ResolvedDate\":\"2026-10-19T08:00:00Z\",\"Custom.Milestone\":\"Alpha\"");
            var typed = Assert.Single(AdoTelemetryEvents.Convert(new[] { MapWorkItemScope(item, ScopeFeature, ScopeEpic, FlowSettings())! }));
            Assert.EndsWith("\\WorkItemScope", typed.Definition);
            Assert.Equal(6006L, typed.Data["WorkItemId"]);
            Assert.Equal(6008L, typed.Data["EpicId"]);
            Assert.Equal(6007L, typed.Data["FeatureId"]);
            Assert.Equal("Alpha", typed.Data["Milestone"]);
            Assert.Equal(1L, typed.Data["Done"]);
            Assert.False(typed.Data.ContainsKey("StoryPoints")); // not estimated: left out, so NULL
            Assert.Equal(new DateTime(2026, 8, 3, 9, 0, 0, DateTimeKind.Utc), typed.EventTime);
        }

        [Fact]
        public async Task Collector_ScopeAloneWalksTheParentsToFeatureAndEpic()
        {
            var ado = new FakeAdo();
            ado.QueryIds.Add(6006);
            ado.WorkItems.AddRange(new[] { ScopeStory(",\"Microsoft.VSTS.Common.ResolvedDate\":\"2026-10-19T08:00:00Z\""), ScopeFeature, ScopeEpic });
            var settings = PipelineRunsSettings.From(new PipelineRunsOptions { azureDevOpsUrl = "https://dev.azure.com/example/", teamProject = "Contoso", include = "scope", scopeTypes = "User Story,Bug" }, new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal(PipelineRunsParts.WorkItemScope, settings.Parts);
            var events = await new PipelineRunsCollector(ado, settings).CollectAsync();
            var typed = Assert.Single(AdoTelemetryEvents.Convert(events));
            Assert.Equal(6008L, typed.Data["EpicId"]);
            Assert.Equal(6007L, typed.Data["FeatureId"]);
            Assert.Contains("[System.WorkItemType] IN ('User Story', 'Bug')", ado.LastWiql);
            // an Epic nested under another Epic: the root one is the item's Epic
            ado.WorkItems.Clear();
            ado.WorkItems.AddRange(new[] { ScopeStory(), J("""{"id":6007,"fields":{"System.WorkItemType":"Feature","System.Title":"Recipe mapping","System.Parent":6009}}"""), J("""{"id":6009,"fields":{"System.WorkItemType":"Epic","System.Title":"Orion","System.Parent":6008}}"""), ScopeEpic });
            var nested = Assert.Single(AdoTelemetryEvents.Convert(await new PipelineRunsCollector(ado, settings).CollectAsync()));
            Assert.Equal(6008L, nested.Data["EpicId"]);
            Assert.Equal(6007L, nested.Data["FeatureId"]);
            Assert.Contains("[System.State] NOT IN", ado.LastWiql);
        }

        [Fact]
        public void ParseParts_ScopeReadsOnlyTheScopeAndIsNotPartOfAll()
        {
            Assert.Equal(PipelineRunsParts.WorkItemScope, PipelineRunsSettings.ParseParts("scope"));
            Assert.False(PipelineRunsParts.All.HasFlag(PipelineRunsParts.WorkItemScope));
            Assert.Equal(new[] { "User Story", "Bug" }, PipelineRunsSettings.From(new PipelineRunsOptions { azureDevOpsUrl = "https://dev.azure.com/x/", teamProject = "P" }, DateTime.UtcNow).ScopeTypes);
        }

        [Fact]
        public void ReleaseSchedule_KeepsEveryDateChangeAsItsOwnRowKeyedByRevision()
        {
            var rows = MapReleaseSchedule(6002, ScheduleRevisions, FlowSettings());
            // rev 1 has no dates and rev 3 only changes the state: no rows for them
            Assert.Equal(new[] { "2", "4", "5", "6" }, rows.Select(r => TagOf(r, "Rev")).ToArray());
            Assert.Equal(new[] { "Set", "StartAndTarget", "Target", "Cleared" }, rows.Select(r => TagOf(r, "ChangeKind")).ToArray());
            Assert.All(rows, r => Assert.Equal("6002", TagOf(r, "WorkItemId")));
            Assert.Equal(new[] { "6002:2", "6002:4", "6002:5", "6002:6" }, rows.Select(r => r.Resource.Name).ToArray());
            Assert.All(rows, r => { Assert.Equal("1.3.18", TagOf(r, "Version")); Assert.Equal("release/1.3", TagOf(r, "Line")); Assert.Equal("GoLive", TagOf(r, "ReleaseKind")); Assert.Equal("6003", TagOf(r, "ParentId")); });
            Assert.Equal((decimal?)Unix(new DateTime(2026, 10, 22)), Num(rows[0], "StartDate"));
            Assert.Null(Num(rows[0], "PreviousStartDate"));
            Assert.Null(Num(rows[0], "TargetShift"));
            Assert.Equal((decimal?)Unix(new DateTime(2026, 11, 4)), Num(rows[1], "PreviousTargetDate"));
            Assert.Equal((decimal?)-2, Num(rows[1], "StartShift"));
            Assert.Equal((decimal?)-8, Num(rows[1], "TargetShift"));
            Assert.Equal((decimal?)1, Num(rows[2], "TargetShift"));
            Assert.Null(Num(rows[2], "StartShift")); // start did not move
            Assert.Null(Num(rows[3], "StartDate"));
            Assert.Null(Num(rows[3], "TargetDate"));
            Assert.Equal((decimal?)Unix(new DateTime(2026, 10, 28)), Num(rows[3], "PreviousTargetDate"));
            Assert.Equal("author-guid", TagOf(rows[0], "ChangedBy"));
            Assert.Null(TagOf(MapReleaseSchedule(6002, ScheduleRevisions, FlowSettings(identity: "none"))[0], "ChangedBy"));
        }

        [Fact]
        public void ReleaseSchedule_AddsAStateRowWhenTheLatestRevisionOnlyChangesTheState()
        {
            var revisions = new List<JsonElement>
            {
                J("""{"rev":1,"fields":{"System.Title":"V1.3.17 - Go-Live Package Release","System.State":"New","System.Parent":6003,"System.ChangedDate":"2026-10-01T00:00:00Z","Microsoft.VSTS.Scheduling.StartDate":"2026-10-06T00:00:00Z","Microsoft.VSTS.Scheduling.TargetDate":"2026-10-13T00:00:00Z"}}"""),
                J("""{"rev":2,"fields":{"System.Title":"V1.3.17 - Go-Live Package Release","System.State":"Resolved","System.Parent":6003,"System.ChangedDate":"2026-10-14T00:00:00Z","Microsoft.VSTS.Scheduling.StartDate":"2026-10-06T00:00:00Z","Microsoft.VSTS.Scheduling.TargetDate":"2026-10-13T00:00:00Z"}}"""),
            };
            var rows = MapReleaseSchedule(6001, revisions, FlowSettings());
            Assert.Equal(new[] { "Set", "State" }, rows.Select(r => TagOf(r, "ChangeKind")).ToArray());
            Assert.Equal(new[] { "1", "2" }, rows.Select(r => TagOf(r, "Rev")).ToArray());
            Assert.Equal("Resolved", TagOf(rows[1], "State"));
            Assert.Equal((decimal?)Unix(new DateTime(2026, 10, 13)), Num(rows[1], "TargetDate"));
            Assert.Null(Num(rows[1], "StartShift"));
            Assert.Null(Num(rows[1], "TargetShift"));
        }

        private static List<JsonElement> FileChanges(params string[] paths) =>
            paths.Select(p => J("{\"item\":{\"path\":\"" + p + "\"},\"changeType\":\"edit\"}")).ToList();

        [Fact]
        public void PullRequestFiles_CountsTestFilesUnderTheTestPathsAndFlagsTestOnlyChanges()
        {
            var settings = PipelineRunsSettings.From(new PipelineRunsOptions { azureDevOpsUrl = "https://dev.azure.com/example/", teamProject = "Contoso", include = "prfiles" }, new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc));
            var pr = J("""{"pullRequestId":2004,"status":"completed","targetRefName":"refs/heads/development","closedDate":"2026-10-23T12:00:00Z","repository":{"name":"Contoso"},"lastMergeCommit":{"commitId":"m1"}}""");
            var testOnly = MapPullRequestFiles(pr, FileChanges("/Cmf.Custom.Tests/Cmf.Custom.Tests.Biz/A.cs", "/cmf.custom.business/Cmf.Custom.UnitTests.Biz/B.cs"), settings)!;
            Assert.Equal((decimal?)2, Num(testOnly, "Files"));
            Assert.Equal((decimal?)2, Num(testOnly, "TestFiles"));
            Assert.Equal((decimal?)1, Num(testOnly, "TestOnly"));
            var mixed = MapPullRequestFiles(pr, FileChanges("/Cmf.Custom.Tests/A.cs", "/Cmf.Custom.Business/Cmf.Custom.Orchestration/C.cs"), settings)!;
            Assert.Equal((decimal?)1, Num(mixed, "TestFiles"));
            Assert.Equal((decimal?)0, Num(mixed, "TestOnly"));
            var folder = new List<JsonElement> { J("""{"item":{"path":"/Cmf.Custom.Tests/New","isFolder":true},"changeType":"add"}"""), FileChanges("/Cmf.Custom.Tests/A.cs")[0] };
            Assert.Equal((decimal?)1, Num(MapPullRequestFiles(pr, folder, settings)!, "Files"));
            Assert.Equal((decimal?)0, Num(MapPullRequestFiles(pr, new List<JsonElement>(), settings)!, "TestOnly"));
            Assert.Null(MapPullRequestFiles(pr, null, settings));
            Assert.Null(MapPullRequestFiles(J("""{"pullRequestId":1,"status":"completed"}"""), FileChanges("/x"), settings));
            var custom = PipelineRunsSettings.From(new PipelineRunsOptions { azureDevOpsUrl = "https://dev.azure.com/example/", teamProject = "Contoso", pathRules = "Cmf.Custom.HTML/src/spec=test-ui" }, new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc));
            var ui = MapPullRequestFiles(pr, FileChanges("/Cmf.Custom.HTML/src/spec/a.spec.ts"), custom)!;
            Assert.Equal((decimal?)1, Num(ui, "TestOnly"));
            Assert.Equal((decimal?)1, Num(ui, "UiTestFiles"));
        }

        [Fact]
        public void PullRequestFiles_ClassifiesTestKindsAndTheAreaOfNonTestFiles()
        {
            var settings = PipelineRunsSettings.From(new PipelineRunsOptions { azureDevOpsUrl = "https://dev.azure.com/example/", teamProject = "Contoso", include = "prfiles" }, new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc));
            var pr = J("""{"pullRequestId":7,"status":"completed","targetRefName":"refs/heads/development","closedDate":"2026-10-23T12:00:00Z","repository":{"name":"Contoso"},"lastMergeCommit":{"commitId":"m1"}}""");
            var t = MapPullRequestFiles(pr, FileChanges(
                "/Cmf.Custom.Business/Cmf.Custom.UnitTests.Biz/A.cs", "/Cmf.Custom.Tests/Cmf.Custom.Tests.Biz/B.cs", "/Cmf.Custom.Tests/Cmf.Custom.Tests.IoTOrchestrator/C.cs",
                "/Cmf.Custom.Tests/Cmf.Custom.Tests.GUI.PageObjects/D.cs", "/Cmf.Custom.Tests/Cmf.Custom.Tests.E2E/E.cs", "/Cmf.Custom.Tests/app.config",
                "/Cmf.Custom.Business/Cmf.Custom.Services/F.cs", "/Cmf.Custom.Data/Cmf.Custom.Data.Packages/DEEs/G.cs", "/Cmf.Custom.Data/Cmf.Custom.Data.Packages/MasterData/H.json",
                "/Cmf.Custom.IoT/Cmf.Custom.IoT.Data/X/I.json", "/Cmf.Custom.HTML/J.ts", "/azurepipelines-coverage.yml"), settings)!;
            Assert.Equal((decimal?)12, Num(t, "Files"));
            Assert.Equal((decimal?)6, Num(t, "TestFiles"));
            Assert.Equal((decimal?)1, Num(t, "UnitTestFiles"));
            Assert.Equal((decimal?)1, Num(t, "BusinessTestFiles"));
            Assert.Equal((decimal?)1, Num(t, "IoTTestFiles"));
            Assert.Equal((decimal?)1, Num(t, "UiTestFiles"));
            Assert.Equal((decimal?)1, Num(t, "E2ETestFiles"));
            Assert.Equal((decimal?)1, Num(t, "OtherTestFiles"));
            Assert.Equal((decimal?)2, Num(t, "BusinessFiles"));
            Assert.Equal((decimal?)1, Num(t, "MasterDataFiles"));
            Assert.Equal((decimal?)1, Num(t, "IoTFiles"));
            Assert.Equal((decimal?)1, Num(t, "UiFiles"));
            Assert.Equal((decimal?)1, Num(t, "OtherFiles"));
            Assert.Equal((decimal?)0, Num(t, "TestOnly"));        }

        [Fact]
        public async Task Collector_PullRequestFilesReadsTheMergeCommitChangesOfCompletedPullRequestsOnly()
        {
            var ado = new FakeAdo();
            ado.Repos["Contoso"] = J("""{"id":"r1","name":"Contoso"}""");
            ado.PullRequests.Add(J("""{"pullRequestId":1,"status":"completed","targetRefName":"refs/heads/development","closedDate":"2026-10-20T10:00:00Z","creationDate":"2026-10-19T10:00:00Z","repository":{"name":"Contoso"},"lastMergeCommit":{"commitId":"m1"}}"""));
            ado.PullRequests.Add(J("""{"pullRequestId":2,"status":"abandoned","targetRefName":"refs/heads/development","closedDate":"2026-10-20T10:00:00Z","creationDate":"2026-10-19T10:00:00Z","repository":{"name":"Contoso"},"lastMergeCommit":{"commitId":"m2"}}"""));
            ado.CommitChanges["m1"] = FileChanges("/Cmf.Custom.Tests/A.cs");
            var settings = PipelineRunsSettings.From(new PipelineRunsOptions { azureDevOpsUrl = "https://dev.azure.com/example/", teamProject = "Contoso", include = "prfiles", repositories = "Contoso", since = "2026-10-01" }, new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal(PipelineRunsParts.PullRequestFiles, settings.Parts);
            var events = await new PipelineRunsCollector(ado, settings).CollectAsync();
            var typed = AdoTelemetryEvents.Convert(events);
            var one = Assert.Single(typed);
            Assert.Equal("\\IoTEventDefinitions\\ADOTelemetry\\PullRequestFiles", one.Definition);
            Assert.Equal(1L, one.Data["PullRequestId"]);
            Assert.Equal(1L, one.Data["TestOnly"]);
        }

        [Fact]
        public void ReleaseSchedule_IgnoresFeaturesThatAreNotReleasesAndMissingHistory()
        {
            var other = new List<JsonElement> { J("""{"rev":1,"fields":{"System.Title":"Package Release notes","Microsoft.VSTS.Scheduling.TargetDate":"2026-10-27T00:00:00Z","System.ChangedDate":"2026-10-21T00:00:00Z"}}""") };
            Assert.Empty(MapReleaseSchedule(1, other, FlowSettings()));
            Assert.Empty(MapReleaseSchedule(1, null, FlowSettings()));
            Assert.Equal("development", ReleaseLine("1.6.0", Settings()));
            Assert.Equal("release/1.7", ReleaseLine("1.7.3", Settings()));
            Assert.Equal("", ReleaseLine("", Settings()));
            var configured = Settings(branchVersions: "main=1.6,release/1.3=1.3");
            Assert.Equal("main", ReleaseLine("1.6.0", configured));
            Assert.Equal("main", ReleaseLine("1.6.2", configured));
            Assert.Equal("release/1.3", ReleaseLine("1.3.1", configured));
            Assert.Equal("release/1.7", ReleaseLine("1.7.3", configured));
        }

        [Fact]
        public async Task Collector_ReleaseScheduleAloneReadsOnlyTheFeaturesNotRuns()
        {
            var ado = new FakeAdo();
            ado.QueryIds.AddRange(new[] { 6002, 99 });
            ado.Revisions[6002] = ScheduleRevisions;
            ado.Revisions[99] = new() { J("""{"rev":1,"fields":{"System.Title":"Something else","System.ChangedDate":"2026-10-21T00:00:00Z"}}""") };
            var settings = PipelineRunsSettings.From(new PipelineRunsOptions { azureDevOpsUrl = "https://dev.azure.com/example/", teamProject = "Contoso", include = "schedule", releaseTitleContains = "Package Release" }, new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal(PipelineRunsParts.ReleaseSchedule, settings.Parts);
            var events = await new PipelineRunsCollector(ado, settings).CollectAsync();
            Assert.Equal(4, events.Count);
            Assert.All(events, e => Assert.Equal(AreaReleaseSchedule, e.Area.Name));
            Assert.Contains("[System.Title] CONTAINS 'Package Release'", ado.LastWiql);
            Assert.Contains("[System.WorkItemType] = 'Feature'", ado.LastWiql);
            var typed = AdoTelemetryEvents.Convert(events);
            Assert.Equal(4, typed.Count);
            Assert.All(typed, t => Assert.Equal("\\IoTEventDefinitions\\ADOTelemetry\\ReleaseSchedule", t.Definition));
            Assert.Equal(new DateTime(2026, 10, 26, 7, 0, 0, DateTimeKind.Utc), typed[1].EventTime);
            Assert.Equal(new DateTime(2026, 10, 20), ((DateTime)typed[1].Data["StartDate"]).Date);
            Assert.Equal(-8L, typed[1].Data["TargetShiftDays"]);
            Assert.False(typed[0].Data.ContainsKey("PreviousStartDate"));
            Assert.Equal(PipelineRunsParts.All & PipelineRunsParts.ReleaseSchedule, PipelineRunsParts.None); // schedule is not part of all
            Assert.Equal(PipelineRunsParts.Runs | PipelineRunsParts.ReleaseSchedule, PipelineRunsSettings.ParseParts("schedule,runs"));
        }

        [Fact]
        public void ReleaseTag_UsesTheDateInTheTagNameAsReleaseDay()
        {
            var settings = FlowSettings();
            var lightweight = MapReleaseTag("Contoso", J("""{"name":"refs/tags/Contoso-20260515-1.3.4","objectId":"c1"}"""), null, J("""{"committer":{"date":"2026-05-14T16:00:00Z"}}"""), settings)!;
            Assert.Equal("Contoso-20260515-1.3.4", lightweight.Resource.Name);
            Assert.Equal("Contoso", lightweight.Facility.Name);
            Assert.Equal("1.3.4", TagOf(lightweight, "Version"));
            Assert.Equal("release/1.3", TagOf(lightweight, "Line"));
            Assert.Equal("Lightweight", TagOf(lightweight, "TagKind"));
            Assert.Equal("c1", TagOf(lightweight, "CommitId"));
            Assert.Equal((decimal?)Unix(new DateTime(2026, 5, 15)), Num(lightweight, "ReleasedAt"));
            Assert.Equal((decimal?)Unix(new DateTime(2026, 5, 14, 16, 0, 0)), Num(lightweight, "CommitAt"));
            Assert.Null(Num(lightweight, "TaggedAt"));
            Assert.Null(Num(lightweight, "PushedAt"));
            var pushed = MapReleaseTag("Contoso", J("""{"name":"refs/tags/Contoso-20260506-1.4.0","objectId":"c1"}"""), null, null, settings, new DateTime(2026, 5, 15, 14, 22, 0, DateTimeKind.Utc))!;
            Assert.Equal((decimal?)Unix(new DateTime(2026, 5, 15, 14, 22, 0)), Num(pushed, "PushedAt"));
            Assert.Equal((decimal?)Unix(new DateTime(2026, 5, 6)), Num(pushed, "ReleasedAt"));

            // an annotated tag created nine days late does not move the release day
            var annotated = MapReleaseTag("Contoso", J("""{"name":"refs/tags/Contoso-20260506-1.4.0","objectId":"a1","peeledObjectId":"c2"}"""), J("""{"taggedBy":{"date":"2026-05-15T09:00:00Z"}}"""), J("""{"committer":{"date":"2026-05-04T10:00:00Z"}}"""), settings)!;
            Assert.Equal("Annotated", TagOf(annotated, "TagKind"));
            Assert.Equal("c2", TagOf(annotated, "CommitId"));
            Assert.Equal((decimal?)Unix(new DateTime(2026, 5, 6)), Num(annotated, "ReleasedAt"));
            Assert.Equal((decimal?)Unix(new DateTime(2026, 5, 15, 9, 0, 0)), Num(annotated, "TaggedAt"));

            Assert.Null(MapReleaseTag("Contoso", J("""{"name":"refs/tags/nightly-1","objectId":"c3"}"""), null, null, settings));
            Assert.Null(MapReleaseTag("Contoso", J("""{"name":"refs/tags/Contoso-20260515-1.3.4-rc1","objectId":"c3"}"""), null, null, settings));
        }

        [Fact]
        public void ReleaseTag_FallsBackToTheTagThenCommitDateWithoutADateInTheName()
        {
            var settings = PipelineRunsSettings.From(new PipelineRunsOptions { azureDevOpsUrl = "https://dev.azure.com/example/", teamProject = "Contoso", tagPattern = @"^v(?<version>\d+\.\d+\.\d+)$" }, new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc));
            var viaCommit = MapReleaseTag("Contoso", J("""{"name":"refs/tags/v2.50.0","objectId":"c1"}"""), null, J("""{"committer":{"date":"2026-06-01T10:00:00Z"}}"""), settings)!;
            Assert.Equal((decimal?)Unix(new DateTime(2026, 6, 1, 10, 0, 0)), Num(viaCommit, "ReleasedAt"));
            Assert.Null(MapReleaseTag("Contoso", J("""{"name":"refs/tags/v2.50.0","objectId":"c1"}"""), null, null, settings));
            var viaPush = MapReleaseTag("Contoso", J("""{"name":"refs/tags/v2.50.0","objectId":"c1"}"""), null, J("""{"committer":{"date":"2026-06-01T10:00:00Z"}}"""), settings, new DateTime(2026, 6, 2, 8, 0, 0, DateTimeKind.Utc))!;
            Assert.Equal((decimal?)Unix(new DateTime(2026, 6, 2, 8, 0, 0)), Num(viaPush, "ReleasedAt"));
            Assert.Throws<ArgumentException>(() => PipelineRunsSettings.From(new PipelineRunsOptions { azureDevOpsUrl = "https://dev.azure.com/example/", teamProject = "Contoso", tagPattern = @"^v(\d+)$" }, DateTime.UtcNow));
        }

        [Fact]
        public async Task Collector_TagsAloneReadsTheMatchingTagsOfTheRepositories()
        {
            var ado = new FakeAdo();
            ado.Repos["Contoso"] = J("""{"id":"repo-1","name":"Contoso"}""");
            ado.Tags.Add(J("""{"name":"refs/tags/Contoso-20260506-1.4.0","objectId":"a1","peeledObjectId":"c2"}"""));
            ado.Tags.Add(J("""{"name":"refs/tags/Contoso-20260515-1.3.4","objectId":"c1"}"""));
            ado.Tags.Add(J("""{"name":"refs/tags/some-other-tag","objectId":"c9"}"""));
            ado.Pushes["refs/tags/Contoso-20260506-1.4.0"] = new() { J("""{"date":"2026-05-15T14:22:00Z"}""") };
            ado.AnnotatedTags["a1"] = J("""{"taggedBy":{"date":"2026-05-15T09:00:00Z"}}""");
            ado.Commits["c1"] = J("""{"committer":{"date":"2026-05-14T16:00:00Z"}}""");
            ado.Commits["c2"] = J("""{"committer":{"date":"2026-05-04T10:00:00Z"}}""");
            var settings = PipelineRunsSettings.From(new PipelineRunsOptions { azureDevOpsUrl = "https://dev.azure.com/example/", teamProject = "Contoso", include = "tags", repositories = "Contoso" }, new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal(PipelineRunsParts.ReleaseTags, settings.Parts);
            var events = await new PipelineRunsCollector(ado, settings).CollectAsync();
            Assert.Equal(2, events.Count);
            Assert.All(events, e => Assert.Equal(AreaReleaseTag, e.Area.Name));
            var typed = AdoTelemetryEvents.Convert(events);
            Assert.All(typed, t => Assert.Equal("\\IoTEventDefinitions\\ADOTelemetry\\ReleaseTag", t.Definition));
            var byVersion = typed.ToDictionary(t => (string)t.Data["Version"]);
            Assert.Equal(new DateTime(2026, 5, 6), ((DateTime)byVersion["1.4.0"].Data["ReleasedAt"]).Date);
            Assert.Equal(new DateTime(2026, 5, 6), byVersion["1.4.0"].EventTime.Date);
            Assert.Equal(new DateTime(2026, 5, 15), ((DateTime)byVersion["1.4.0"].Data["PushedAt"]).Date);
            Assert.False(byVersion["1.3.4"].Data.ContainsKey("PushedAt"));
            Assert.Equal("repo-1", ado.RepoIdsAsked.Single());
            Assert.Equal(PipelineRunsParts.ReleaseSchedule | PipelineRunsParts.ReleaseTags, PipelineRunsSettings.ParseParts("schedule,tags"));
            Assert.Equal(PipelineRunsParts.Runs | PipelineRunsParts.ReleaseTags, PipelineRunsSettings.ParseParts("tags,runs"));
        }

        [Fact]
        public void Sprints_PreserveIterationPathsAndIncludeTheWholeFinishDay()
        {
            var root = J("""{"name":"Contoso","children":[{"name":"Product Backlog","children":[{"name":"Team A","children":[{"name":"Sprint 79","attributes":{"startDate":"2026-10-12T00:00:00Z","finishDate":"2026-10-23T00:00:00Z"}}]}]}]}""");
            var mapped = Assert.Single(MapSprints(root, FlowSettings()));
            Assert.Equal("Contoso\\Product Backlog\\Team A\\Sprint 79", TagOf(mapped, "IterationPath"));
            Assert.Equal("Team A", TagOf(mapped, "Team"));
            Assert.Equal((decimal?)Unix(new DateTime(2026, 10, 24)), Num(mapped, "Finish"));
        }

        [Fact]
        public async Task Collector_KeepsAllLinksAndReadsHistoryOncePerWorkItem()
        {
            var ado = Ado();
            ado.PullRequests.Add(FlowPr);
            ado.PrWorkItems[2003] = new() { J("""{"id":"6005"}"""), J("""{"id":"6004"}""") };
            ado.WorkItems.Add(FlowItem);
            var events = await new PipelineRunsCollector(ado, FlowSettings()).CollectAsync();
            Assert.Equal(3, events.Count(e => e.Area.Name == AreaPullRequestWorkItem));
            Assert.Equal(3, events.Count(e => e.Area.Name == AreaWorkItemFlow));
            Assert.Empty(events.Where(e => e.Area.Name == AreaWorkItemHistory));
            Assert.Equal(2, ado.HistoryCalls);
            Assert.Contains("Custom.PackageVersion", ado.SelectedFields);
        }

        [Fact]
        public void WorkItemLink_FlagsConfiguredVersionMismatchAndMissingVersion()
        {
            Assert.Equal((decimal?)1, Num(MapPullRequestWorkItem(FlowPr, J(FlowItem.GetRawText().Replace("1.6.0", "1.3.0")), FlowSettings()), "VersionMismatch"));
            var missing = MapPullRequestWorkItem(FlowPr, J(FlowItem.GetRawText().Replace("1.6.0", "")), FlowSettings());
            Assert.Null(Num(missing, "VersionMismatch"));
            Assert.Equal((decimal?)1, Num(missing, "DoneWithoutVersion"));
        }

        private static string EnvCdRun(int id, string commit, string branch = "env/Contoso-ContosoReleaseEnvironment", string pipeline = "CD-Containers") => $$$"""
            {"id":{{{id}}},"buildNumber":"Release-{{{id}}}","uri":"vstfs:///Build/Build/{{{id}}}","result":"succeeded","reason":"manual",
             "queueTime":"2026-10-08T21:58:50.701Z","startTime":"2026-10-08T21:59:02.917Z","finishTime":"2026-10-08T22:48:23.089Z",
             "sourceBranch":"refs/heads/{{{branch}}}","sourceVersion":"{{{commit}}}","definition":{"id":1,"name":"{{{pipeline}}}"},
             "repository":{"id":"00000000-0000-4000-8000-0000000000a1","name":"Contoso","type":"TfsGit"}}
            """;

        private static readonly JsonElement CmfPackage = J("""{"packageId":"Cmf.Custom.Package","version":"1.7.1","dependencies":[]}""");

        [Fact]
        public void MapRun_InstalledPackageSetsVersionAndPackages()
        {
            var run = MapRun(J(EnvCdRun(1, "abc")), null, Settings(), installed: ("Cmf.Custom.Package", "1.7.1"));
            Assert.Equal("1.7.1", TagOf(run, "Version"));
            Assert.Equal("Cmf.Custom.Package", TagOf(run, "Packages"));
        }

        [Fact]
        public void MapRun_WithoutInstalledBehavesAsBefore()
        {
            var cd = MapRun(J(EnvCdRun(1, "abc")), null, Settings());
            Assert.Null(TagOf(cd, "Version"));
            Assert.Null(TagOf(cd, "Packages"));
            var withPackages = MapRun(J(PrPackage), null, Settings());
            Assert.Equal("1.6.0", TagOf(withPackages, "Version"));
            Assert.Equal("Cmf.Custom.Business;Cmf.Custom.Data", TagOf(withPackages, "Packages"));
            var kept = MapRun(J(PrPackage), null, Settings(), installed: ("Cmf.Custom.Package", "9.9.9"));
            Assert.Equal("Cmf.Custom.Business;Cmf.Custom.Data", TagOf(kept, "Packages"));
        }

        [Fact]
        public async Task Collector_ReadsCmfPackageOncePerCommitForEnvBranchCdRuns()
        {
            var ado = new FakeAdo { Builds = { J(EnvCdRun(1, "abc")), J(EnvCdRun(2, "abc")), J(EnvCdRun(3, "def")) } };
            ado.JsonFiles["abc"] = CmfPackage;
            var events = await new PipelineRunsCollector(ado, Settings()).CollectAsync();
            Assert.Equal(new[] { "abc", "def" }, ado.JsonFileCalls.Select(c => c.Commit).OrderBy(c => c));
            Assert.All(ado.JsonFileCalls, c => Assert.Equal("/cmfpackage.json", c.Path));
            var runs = events.Where(e => e.Area.Name == AreaRun).ToDictionary(e => e.Resource.Name);
            Assert.Equal("1.7.1", TagOf(runs["1"], "Version"));
            Assert.Equal("1.7.1", TagOf(runs["2"], "Version"));
            Assert.Equal("Cmf.Custom.Package", TagOf(runs["2"], "Packages"));
            Assert.Null(TagOf(runs["3"], "Version"));
        }

        [Fact]
        public async Task Collector_DoesNotReadCmfPackageForNonCdRunsOrNonEnvBranches()
        {
            var ado = new FakeAdo { Builds = { J(EnvCdRun(1, "abc", pipeline: "CI-Changes")), J(EnvCdRun(2, "abc", branch: "release/1.3/development")) } };
            ado.JsonFiles["abc"] = CmfPackage;
            var events = await new PipelineRunsCollector(ado, Settings()).CollectAsync();
            Assert.Empty(ado.JsonFileCalls);
            Assert.All(events.Where(e => e.Area.Name == AreaRun), e => Assert.Null(TagOf(e, "Version")));
        }

        [Fact]
        public async Task Collector_MissingFileOrIncompleteFileOrDeniedAccessGivesNoVersion()
        {
            var ado = new FakeAdo { Builds = { J(EnvCdRun(1, "missing")), J(EnvCdRun(2, "nopackage")), J(EnvCdRun(3, "denied")) } };
            ado.JsonFiles["nopackage"] = J("""{"name":"other"}""");
            ado.DeniedCommits.Add("denied");
            var events = await new PipelineRunsCollector(ado, Settings()).CollectAsync();
            var runs = events.Where(e => e.Area.Name == AreaRun).ToList();
            Assert.Equal(3, runs.Count);
            Assert.All(runs, e => Assert.Null(TagOf(e, "Version")));
        }

        [Fact]
        public async Task Collector_StopsReadingCmfPackageAfterDeniedAccess()
        {
            var ado = new FakeAdo { Builds = { J(EnvCdRun(1, "a")), J(EnvCdRun(2, "b")), J(EnvCdRun(3, "c")) } };
            ado.DeniedCommits.UnionWith(new[] { "a", "b", "c" });
            var events = await new PipelineRunsCollector(ado, Settings(), parallelism: 1).CollectAsync();
            Assert.Single(ado.JsonFileCalls);
            Assert.Equal(3, events.Count(e => e.Area.Name == AreaRun));
        }

        [Fact]
        public async Task Collector_RetriesAFailedReadButNotACleanMiss()
        {
            var ado = new FakeAdo { Builds = { J(EnvCdRun(1, "flaky")), J(EnvCdRun(2, "flaky")), J(EnvCdRun(3, "missing")), J(EnvCdRun(4, "missing")) } };
            ado.TimeoutCommits.Add("flaky");
            var events = await new PipelineRunsCollector(ado, Settings(), parallelism: 1).CollectAsync();
            Assert.Equal(2, ado.JsonFileCalls.Count(c => c.Commit == "flaky"));
            Assert.Single(ado.JsonFileCalls, c => c.Commit == "missing");
            Assert.All(events.Where(e => e.Area.Name == AreaRun), e => Assert.Null(TagOf(e, "Version")));
        }

        [Theory]
        [InlineData(HttpStatusCode.NotFound, "")]
        [InlineData(HttpStatusCode.OK, """{"path":"/cmfpackage.json","content":"not json {"}""")]
        public async Task RestClient_GetJsonFile_MissingOrInvalidIsNull(HttpStatusCode status, string body)
        {
            using var client = new AzureDevOpsRestClient("https://dev.azure.com/example/", "pat", new BodyHandler(status, body));
            Assert.Null(await client.GetJsonFileAsync("Contoso", "repo", "/cmfpackage.json", "abc"));
        }

        [Fact]
        public async Task RestClient_GetJsonFile_ParsesContentAndRequestsTheCommit()
        {
            var handler = new BodyHandler(HttpStatusCode.OK, """{"path":"/cmfpackage.json","content":"{\"packageId\":\"Cmf.Custom.Package\",\"version\":\"1.7.1\"}"}""");
            using var client = new AzureDevOpsRestClient("https://dev.azure.com/example/", "pat", handler);
            var file = await client.GetJsonFileAsync("Contoso", "repo", "/cmfpackage.json", "abc");
            Assert.Equal("1.7.1", Str(file!.Value, "version"));
            Assert.Contains("versionDescriptor.version=abc", handler.LastUrl);
            Assert.Contains("includeContent=true", handler.LastUrl);
        }

        [Fact]
        public async Task RestClient_GetJsonFile_DeniedAccessThrowsUnauthorized()
        {
            using var client = new AzureDevOpsRestClient("https://dev.azure.com/example/", "pat", new BodyHandler(HttpStatusCode.Forbidden, ""));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.GetJsonFileAsync("Contoso", "repo", "/cmfpackage.json", "abc"));
        }

        [Fact]
        public async Task RestClient_RequiredListings_FailOnNotFound()
        {
            using var client = new AzureDevOpsRestClient("https://dev.azure.com/example/", "pat", new BodyHandler(HttpStatusCode.NotFound, ""));
            var since = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            await Assert.ThrowsAsync<HttpRequestException>(() => client.ListBuildsAsync("Contoso", since, null));
            await Assert.ThrowsAsync<HttpRequestException>(() => client.ListRepositoriesAsync("Contoso"));
            await Assert.ThrowsAsync<HttpRequestException>(() => client.ListPullRequestsAsync("Contoso", "repo", since, "closed"));
            await Assert.ThrowsAsync<HttpRequestException>(() => client.ListActivePullRequestsAsync("Contoso", "repo"));
        }

        [Theory]
        [InlineData(false, "Basic", "OnRlc3QtdG9rZW4=")]
        [InlineData(true, "Bearer", "test-token")]
        public async Task RestClient_UsesTheSelectedAuthenticationScheme(bool bearer, string scheme, string credential)
        {
            var handler = new BodyHandler(HttpStatusCode.OK, "{}");
            using var client = new AzureDevOpsRestClient("https://dev.azure.com/example/", "test-token", handler, useBearer: bearer);
            await client.GetRepositoryAsync("Contoso", "repo");
            Assert.Equal(scheme, handler.AuthorizationScheme);
            Assert.Equal(credential, handler.AuthorizationParameter);
        }

        private class BodyHandler(HttpStatusCode status, string body) : HttpMessageHandler
        {
            public string LastUrl { get; private set; } = "";
            public string? AuthorizationScheme { get; private set; }
            public string? AuthorizationParameter { get; private set; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                LastUrl = request.RequestUri!.ToString();
                AuthorizationScheme = request.Headers.Authorization?.Scheme;
                AuthorizationParameter = request.Headers.Authorization?.Parameter;
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
            }
        }

        [Fact]
        public async Task Collector_RejectsMissingConfiguredRepository()
        {
            var settings = PipelineRunsSettings.From(new PipelineRunsOptions
            {
                azureDevOpsUrl = "https://dev.azure.com/example/",
                teamProject = "Contoso",
                repositories = "missing"
            }, DateTime.UtcNow);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new PipelineRunsCollector(new FakeAdo(), settings).CollectAsync());
            Assert.Contains("Repository 'missing' not found", error.Message);
        }

        [Fact]
        public async Task Collector_StrictModeFailsWhenTestAccessIsDenied()
        {
            var settings = PipelineRunsSettings.From(new PipelineRunsOptions
            {
                azureDevOpsUrl = "https://dev.azure.com/example/",
                teamProject = "Contoso",
                include = "runs,tests",
                strictCollection = true
            }, DateTime.UtcNow);
            var ado = new FakeAdo { DenyTests = true, Builds = { J(EnvCdRun(1, "abc", pipeline: "CI-Changes")) } };
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new PipelineRunsCollector(ado, settings).CollectAsync());
            Assert.Contains("Test results skipped", error.ToString());
        }

        private static FakeAdo ChangedWorkItemFake()
        {
            var ado = new FakeAdo { QueryIds = { 500 } };
            ado.Repos["repo"] = J("""{"id":"repo","name":"repo"}""");
            // PR 7 is old and closed; work item 500 changed recently and is linked to it (and to a pull request of another repository)
            ado.PullRequestsById[7] = J("""{"pullRequestId":7,"status":"completed","creationDate":"2025-01-01T00:00:00Z","closedDate":"2025-01-02T00:00:00Z","repository":{"name":"repo","id":"repo"}}""");
            ado.PrWorkItems[7] = new() { J("""{"id":"500"}""") };
            ado.WorkItems.Add(J("""{"id":500,"fields":{"System.State":"Closed","System.WorkItemType":"User Story"}}"""));
            ado.WorkItemRelations.Add(J("""{"id":500,"relations":[{"rel":"ArtifactLink","url":"vstfs:///Git/PullRequestId/project%2frepo%2f7","attributes":{"name":"Pull Request"}},{"rel":"ArtifactLink","url":"vstfs:///Git/PullRequestId/project%2fother%2f8","attributes":{"name":"Pull Request"}},{"rel":"Hyperlink","url":"https://example.test"}]}"""));
            return ado;
        }

        private static PipelineRunsSettings ChangedWorkItemSettings(bool refresh) => PipelineRunsSettings.From(new PipelineRunsOptions
        {
            azureDevOpsUrl = "https://dev.azure.com/example/",
            teamProject = "Contoso",
            repositories = "repo",
            include = "prs",
            refreshChangedWorkItems = refresh
        }, new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc));

        [Fact]
        public async Task Collector_RefreshesOldPullRequestsLinkedToChangedWorkItems()
        {
            var ado = ChangedWorkItemFake();
            var events = await new PipelineRunsCollector(ado, ChangedWorkItemSettings(true)).CollectAsync();
            Assert.Contains(events, item => item.Area.Name == AreaPullRequest && item.Resource.Name == "7");
            Assert.DoesNotContain(events, item => item.Area.Name == AreaPullRequest && item.Resource.Name == "8");
            Assert.Contains("[System.ChangedDate] >= '2026-10-16'", ado.LastWiql);
        }

        [Fact]
        public async Task Collector_LeavesOldPullRequestsAloneWithoutTheChangedWorkItemRefresh()
        {
            var ado = ChangedWorkItemFake();
            var events = await new PipelineRunsCollector(ado, ChangedWorkItemSettings(false)).CollectAsync();
            Assert.DoesNotContain(events, item => item.Area.Name == AreaPullRequest && item.Resource.Name == "7");
            Assert.Null(ado.LastWiql);
        }

        [Fact]
        public async Task Collector_RefreshesOldOpenPullRequestsWithoutRecentBuilds()
        {
            var settings = PipelineRunsSettings.From(new PipelineRunsOptions
            {
                azureDevOpsUrl = "https://dev.azure.com/example/",
                teamProject = "Contoso",
                include = "prs",
                refreshOpenPullRequests = true
            }, new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc));
            var ado = new FakeAdo();
            ado.Repos["repo"] = J("""{"id":"repo","name":"repo"}""");
            ado.ActivePullRequests.Add(J("""{"pullRequestId":51,"status":"active","creationDate":"2025-01-01T00:00:00Z","repository":{"name":"repo","id":"repo"}}"""));
            var events = await new PipelineRunsCollector(ado, settings).CollectAsync();
            Assert.Contains(events, item => item.Area.Name == AreaPullRequest && item.Resource.Name == "51");
        }

        private class FakeAdo : IAzureDevOpsRestClient
        {
            public List<JsonElement> Builds { get; } = new();
            public bool DenyTests { get; set; }
            public Dictionary<int, JsonElement> Timelines { get; } = new();
            public Dictionary<string, List<JsonElement>> TestRuns { get; } = new();
            public Dictionary<int, List<JsonElement>> FailedResults { get; } = new();
            public List<JsonElement> Approvals { get; } = new();
            public Dictionary<int, List<JsonElement>> Changes { get; } = new();
            public List<JsonElement> PullRequests { get; } = new();
            public Dictionary<int, List<JsonElement>> PrWorkItems { get; } = new();
            public List<JsonElement> WorkItems { get; } = new();
            public List<JsonElement> Pools { get; } = new();
            public Dictionary<int, List<JsonElement>> Jobs { get; } = new();
            public Dictionary<string, JsonElement> JsonFiles { get; } = new();
            public HashSet<string> DeniedCommits { get; } = new();
            public HashSet<string> TimeoutCommits { get; } = new();
            public List<(string Commit, string Path)> JsonFileCalls { get; } = new();
            public int HistoryCalls { get; private set; }
            public List<string> SelectedFields { get; private set; } = new();
            public List<int> QueryIds { get; } = new();
            public Dictionary<int, List<JsonElement>> Revisions { get; } = new();
            public string? LastWiql { get; private set; }
            public List<JsonElement> Tags { get; } = new();
            public Dictionary<string, JsonElement> AnnotatedTags { get; } = new();
            public Dictionary<string, JsonElement> Commits { get; } = new();
            public Dictionary<string, List<JsonElement>> Pushes { get; } = new();

            public Task<List<JsonElement>> ListBuildsAsync(string project, DateTime since, string? repositoryId) => Task.FromResult(Builds);
            public Task<JsonElement?> GetTimelineAsync(string project, int buildId) => Task.FromResult(Timelines.TryGetValue(buildId, out var t) ? (JsonElement?)t : null);
            public Task<List<JsonElement>> GetBuildChangesAsync(string project, int buildId) => Task.FromResult(Changes.GetValueOrDefault(buildId) ?? new());
            public Task<List<JsonElement>> GetTestRunsAsync(string project, string buildUri) => DenyTests
                ? Task.FromException<List<JsonElement>>(new UnauthorizedAccessException("denied"))
                : Task.FromResult(TestRuns.GetValueOrDefault(buildUri) ?? new());
            public Task<List<JsonElement>> GetFailedTestResultsAsync(string project, int testRunId) => Task.FromResult(FailedResults.GetValueOrDefault(testRunId) ?? new());
            public Task<List<JsonElement>> GetApprovalsAsync(string project, IEnumerable<string> ids) => Task.FromResult(Approvals.Where(a => ids.Contains(a.GetProperty("id").GetString())).ToList());
            public Dictionary<string, JsonElement> Repos { get; } = new();
            public List<string> RepoIdsAsked { get; } = new();
            public Task<JsonElement?> GetRepositoryAsync(string project, string nameOrId) => Task.FromResult(Repos.TryGetValue(nameOrId, out var r) ? (JsonElement?)r : null);
            public Task<List<JsonElement>> ListRepositoriesAsync(string project) => Task.FromResult(Repos.Values.ToList());
            public Task<JsonElement?> GetJsonFileAsync(string project, string repositoryId, string path, string commitId)
            {
                lock (JsonFileCalls) JsonFileCalls.Add((commitId, path));
                if (DeniedCommits.Contains(commitId)) throw new UnauthorizedAccessException("denied (403)");
                if (TimeoutCommits.Contains(commitId)) throw new TaskCanceledException("timed out");
                return Task.FromResult(JsonFiles.TryGetValue(commitId, out var f) ? (JsonElement?)f : null);
            }
            public Task<List<JsonElement>> ListPullRequestsAsync(string project, string repositoryId, DateTime since, string range) => Task.FromResult(PullRequests);
            public Task<List<JsonElement>> ListActivePullRequestsAsync(string project, string repositoryId) => Task.FromResult(ActivePullRequests);
            public List<JsonElement> ActivePullRequests { get; } = new();
            public Task<List<JsonElement>> GetPullRequestWorkItemRefsAsync(string project, string repositoryId, int id) => Task.FromResult(PrWorkItems.GetValueOrDefault(id) ?? new());
            public Task<List<JsonElement>> GetWorkItemsAsync(string project, IEnumerable<int> ids, IEnumerable<string>? fields = null)
            {
                SelectedFields = fields?.ToList() ?? new();
                return Task.FromResult(WorkItems);
            }
            public Dictionary<int, JsonElement> PullRequestsById { get; } = new();
            public List<JsonElement> WorkItemRelations { get; } = new();
            public Task<List<JsonElement>> GetWorkItemRelationsAsync(string project, IEnumerable<int> ids) => Task.FromResult(WorkItemRelations);
            public Task<JsonElement?> GetPullRequestAsync(string project, string repositoryId, int pullRequestId) => Task.FromResult(PullRequestsById.TryGetValue(pullRequestId, out var p) ? (JsonElement?)p : null);
            public Task<List<JsonElement>> GetPullRequestThreadsAsync(string project, string repositoryId, int pullRequestId) => Task.FromResult(new List<JsonElement>());
            public Task<List<JsonElement>> GetPullRequestIterationsAsync(string project, string repositoryId, int pullRequestId) => Task.FromResult(new List<JsonElement>());
            public Task<List<JsonElement>> GetWorkItemRevisionsAsync(string project, int workItemId)
            {
                HistoryCalls++;
                return Task.FromResult(Revisions.GetValueOrDefault(workItemId) ?? new List<JsonElement>());
            }
            public Task<List<int>> QueryWorkItemIdsAsync(string project, string wiql)
            {
                LastWiql = wiql;
                return Task.FromResult(QueryIds);
            }
            public Task<List<JsonElement>> ListTagsAsync(string project, string repositoryId) { RepoIdsAsked.Add(repositoryId); return Task.FromResult(Tags); }
            public Task<JsonElement?> GetAnnotatedTagAsync(string project, string repositoryId, string objectId) => Task.FromResult(AnnotatedTags.TryGetValue(objectId, out var a) ? (JsonElement?)a : null);
            public Task<List<JsonElement>> ListRefPushesAsync(string project, string repositoryId, string refName) => Task.FromResult(Pushes.GetValueOrDefault(refName) ?? new());
            public Dictionary<string, List<JsonElement>> CommitChanges { get; } = new();
            public Task<List<JsonElement>> GetCommitChangesAsync(string project, string repositoryId, string commitId) => Task.FromResult(CommitChanges.GetValueOrDefault(commitId) ?? new());
            public Task<JsonElement?> GetCommitAsync(string project, string repositoryId, string commitId) => Task.FromResult(Commits.TryGetValue(commitId, out var c) ? (JsonElement?)c : null);
            public Task<JsonElement?> GetIterationsAsync(string project) => Task.FromResult<JsonElement?>(null);
            public Task<List<JsonElement>> ListPoolsAsync() => Task.FromResult(Pools);
            public Task<List<JsonElement>> ListJobRequestsAsync(int poolId) => Task.FromResult(Jobs.GetValueOrDefault(poolId) ?? new());
        }
    }
}
