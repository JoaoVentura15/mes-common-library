using audit.Objects.PipelineRuns;
using Xunit;

namespace specs
{
    public class StepErrorScrubberTests
    {
        [Theory]
        [InlineData(@"error MSB3202: The project file ""C:\Users\jdoe\source\repos\Contoso\src\Foo.csproj"" was not found.", @"error MSB3202: The project file ""<path>\Foo.csproj"" was not found.")]
        [InlineData(@"D:\a\1\s\src\Business\Startup.cs(42,17): error CS0246: The type or namespace name 'Foo' could not be found", @"<path>\Startup.cs(42,17): error CS0246: The type or namespace name 'Foo' could not be found")]
        [InlineData("/home/vsts/work/1/s/node_modules/foo/index.js:10 failed", "<path>/index.js:10 failed")]
        [InlineData(@"Cannot open \\fileserver01\builds\drop\pkg.zip", @"Cannot open <path>\pkg.zip")]
        [InlineData("NU1301: Unable to load the service index for source https://pkgs.dev.azure.com/example/_packaging/feed/nuget/v3/index.json", "NU1301: Unable to load the service index for source <url>")]
        [InlineData("Unable to access https://user:" + "ghp_abcdefghijklmnopqrstuvwx" + "@github.com/org/repo.git", "Unable to access <url>")]
        [InlineData("git: Author identity unknown for user@example.com", "git: Author identity unknown for <email>")]
        [InlineData(@"fatal: could not authenticate as CRITICALMFG\svc-build", "fatal: could not authenticate as <account>")]
        [InlineData("Unable to connect to sql-prod-eu01.contoso.local:1433", "Unable to connect to <host>")]
        [InlineData("Connection refused 10.20.3.14:5432", "Connection refused <ip>")]
        [InlineData("Login failed, Password=Sup3rS3cret!;Server=x", "Login failed, Password=<redacted>;Server=x")]
        [InlineData("401 Unauthorized, Authorization: Bearer abc123def456ghi789", "401 Unauthorized, Authorization=<redacted>")]
        [InlineData("Invalid token ghp_0123456789abcdefghijABCDEFGH", "Invalid token <secret>")]
        [InlineData("Run 00000000-0000-4000-8000-00000000beef failed", "Run <guid> failed")]
        [InlineData("Violation of PRIMARY KEY constraint. The duplicate key value is (LOT-883412).", "Violation of PRIMARY KEY constraint. The duplicate key value is (<value>).")]
        [InlineData("Build 37016 failed on lot 883412 with HTTP 401 at line 42", "Build # failed on lot # with HTTP 401 at line 42")]
        [InlineData("The build was canceled by André Cruz.", "The build was canceled by <user>.")]
        [InlineData("The build was canceled by Tung Han Yang 楊 東翰.", "The build was canceled by <user>.")]
        [InlineData("The job running on agent build-agent-002 ran longer than the maximum time of 720 minutes.", "The job running on agent <agent> ran longer than the maximum time of 720 minutes.")]
        [InlineData("Error while getting access token. Security Portal is down", "Error while getting access token. Security Portal is down")]
        [InlineData("401, Authorization: Bearer abc123def456ghi789", "401, Authorization=<redacted>")]
        public void Scrub_removes_who_and_where(string input, string expected) => Assert.Equal(expected, StepErrorScrubber.Scrub(input));

        [Theory]
        [InlineData("Class Initialization method Cmf.Custom.Tests.IoTOrchestrator.Tests.UTC3000.Initialize threw exception. System.NullReferenceException")]
        [InlineData("System.Net.Http.HttpRequestException: The SSL connection could not be established")]
        [InlineData("npm ERR! code ERESOLVE could not resolve dependency tree")]
        [InlineData("Process completed with exit code 1.")]
        [InlineData("Test method Cmf.Custom.Tests.Biz.Materials.CustomMaterialStateTransitionWithoutRulesTests.TestMethod threw exception:")]
        [InlineData("The project file Foo.csproj was not found at line 12")]
        public void Scrub_keeps_messages_without_personal_data_unchanged(string input) => Assert.Equal(input, StepErrorScrubber.Scrub(input));

        [Fact]
        public void Scrub_uses_the_first_line_only_and_caps_the_length()
        {
            Assert.Equal("first", StepErrorScrubber.Scrub("first\r\nsecond C:\\Users\\x\\y.txt"));
            Assert.Equal(StepErrorScrubber.MaxLength, StepErrorScrubber.Scrub(string.Join(" ", Enumerable.Repeat("word", 400))).Length);
        }

        [Fact]
        public void Scrub_handles_empty_input() { Assert.Equal("", StepErrorScrubber.Scrub(null)); Assert.Equal("", StepErrorScrubber.Scrub("  \n ")); }

        [Fact]
        public void Scrub_survives_pathological_input() => _ = StepErrorScrubber.Scrub(string.Concat(Enumerable.Repeat("a1/", 5000)));

        [Theory]
        [InlineData("error MSB3202: The project file was not found", "MSB3202")]
        [InlineData("NU1301: Unable to load the service index", "NU1301")]
        [InlineData("src/a.ts(3,5): error TS2307: Cannot find module", "TS2307")]
        [InlineData("npm ERR! code ERR_PNPM_FETCH_404", "ERR_PNPM_FETCH_404")]
        [InlineData("Error: ENOENT: no such file", "ENOENT")]
        [InlineData("Process completed with exit code 1.", "")]
        [InlineData("", "")]
        public void ErrorCode_extracts_the_first_code(string input, string expected) => Assert.Equal(expected, StepErrorScrubber.ErrorCode(input));
    }
}
