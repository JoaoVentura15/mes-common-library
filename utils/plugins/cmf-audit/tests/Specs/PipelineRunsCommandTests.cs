using Cmf.Cli.Plugin.Audit.Commands;
using audit.Objects.PipelineRuns;
using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.IO.Abstractions.TestingHelpers;
using Xunit;

namespace specs
{
    public class PipelineRunsCommandTests
    {
        private static Command Build(Action<PipelineRunsOptions> capture)
        {
            var cmd = new Command("pipeline-runs");
            new PipelineRunsAuditor(new MockFileSystem()).Configure(cmd);
            cmd.Handler = CommandHandler.Create<PipelineRunsOptions>(capture);
            return cmd;
        }

        [Theory]
        [InlineData("explicit", "system", "environment", "explicit", false)]
        [InlineData(null, "system", "environment", "system", true)]
        [InlineData(null, null, "environment", "environment", false)]
        [InlineData(null, null, null, "", false)]
        public void ResolveAdoToken_UsesTheCorrectAuthentication(string? explicitPat, string? systemToken, string? environmentPat, string expected, bool bearer)
        {
            Assert.Equal((expected, bearer), PipelineRunsAuditor.ResolveAdoToken(explicitPat, systemToken, environmentPat));
        }

        [Fact]
        public void EveryOptionIsBoundToAPropertyOfTheOptionsClass()
        {
            var cmd = Build(_ => { });
            var properties = typeof(PipelineRunsOptions).GetProperties().Select(p => p.Name.ToLowerInvariant()).ToHashSet();
            var unbound = cmd.Options.Select(o => o.Name).Where(n => n != "help" && n != "?" && !properties.Contains(n.ToLowerInvariant())).ToList();
            Assert.Empty(unbound);
        }

        [Fact]
        public async Task PipelineOptionsArriveInTheOptionsAndSettings()
        {
            PipelineRunsOptions? bound = null;
            var cmd = Build(o => bound = o);
            var args = new[]
            {
                "--teamProject", "Contoso", "--azureDevOpsUrl", "https://dev.azure.com/example/",
                "--pipelineRoles", "Gate=PR", "--pipelineRolePrefixes", "Validate-=PR,Build-=CI,Deploy-=CD",
                "--approvalStages", "ManualApproval", "--branchVersions", "main=1.6,release/1.3=1.3",
                "--releaseTitleContains", "Release", "--releaseKinds", "Hotfix=hotfix", "--releaseTitle", @"^V(\d+\.\d+\.\d+)",
                "--teamFromIterationPath", @"^[^\\]+\\([^\\]+)", "--sprintFromIterationPath", @"(Sprint \d+)$",
                "--workItemFields", "Package=Custom.Package", "--doneStates", "Closed", "--include", "runs,steps",
                "--days", "7", "--identity", "name", "--typedEvents", "--strictCollection", "--refreshOpenPullRequests", "--refreshChangedWorkItems", "--stepErrors", "false",
            };
            Assert.Equal(0, await cmd.InvokeAsync(args));
            Assert.NotNull(bound);
            var s = PipelineRunsSettings.From(bound!, new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal("example", s.Organization);
            Assert.Equal("PR", PipelineRunsMapper.ResolveRole("Gate", s));
            Assert.Equal("CD", PipelineRunsMapper.ResolveRole("Deploy-Staging", s));
            Assert.Equal("Other", PipelineRunsMapper.ResolveRole("PR-Validate", s));
            Assert.Contains("ManualApproval", s.ApprovalStages);
            Assert.Equal("main", PipelineRunsMapper.ReleaseLine("1.6.0", s));
            Assert.Equal("Release", s.ReleaseTitleContains);
            Assert.Equal("Hotfix", s.ReleaseKinds.Single().Kind);
            Assert.Equal(new[] { "Closed" }, s.DoneStates.ToArray());
            Assert.Equal(7, bound!.days);
            Assert.True(bound.typedEvents);
            Assert.True(s.StrictCollection);
            Assert.True(s.RefreshOpenPullRequests);
            Assert.True(s.RefreshChangedWorkItems);
            Assert.False(bound.stepErrors);
            Assert.NotNull(s.TeamFromIterationPath);
            Assert.NotNull(s.SprintFromIterationPath);
            Assert.Equal("Custom.Package", s.WorkItemFields["Package"]);
        }
    }
}
