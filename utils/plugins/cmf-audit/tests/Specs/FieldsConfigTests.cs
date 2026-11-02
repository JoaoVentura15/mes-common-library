using audit.Objects.PipelineRuns;
using Xunit;

namespace specs
{
    public class FieldsConfigTests
    {
        [Fact]
        public void FillsOptionsLeftOutOrAtTheirDefault()
        {
            var o = new PipelineRunsOptions { storyPointsField = PipelineRunsSettings.DefaultStoryPointsField };
            FieldsConfig.Apply(o, @"{""fields"": {
                ""storyPointsField"": {""value"": ""Microsoft.VSTS.Scheduling.Effort"", ""purpose"": ""size of an item""},
                ""teamFromIterationPath"": ""^[^\\\\]+\\\\([^\\\\]+)"" }}");
            Assert.Equal("Microsoft.VSTS.Scheduling.Effort", o.storyPointsField);
            Assert.Equal(@"^[^\\]+\\([^\\]+)", o.teamFromIterationPath);
        }

        [Fact]
        public void ExplicitCommandLineValuesWin()
        {
            var o = new PipelineRunsOptions { milestoneField = "Custom.Release" };
            FieldsConfig.Apply(o, @"{""fields"": {""milestoneField"": ""Custom.Other""}}");
            Assert.Equal("Custom.Release", o.milestoneField);
        }

        [Fact]
        public void RejectsUnknownSettingsAndBadShapes()
        {
            Assert.Throws<ArgumentException>(() => FieldsConfig.Apply(new PipelineRunsOptions(), @"{""fields"": {""nope"": ""x""}}"));
            Assert.Throws<ArgumentException>(() => FieldsConfig.Apply(new PipelineRunsOptions(), @"{}"));
            Assert.Throws<ArgumentException>(() => FieldsConfig.Apply(new PipelineRunsOptions(), @"{""fields"": {""epicType"": 5}}"));
        }
    }
}
