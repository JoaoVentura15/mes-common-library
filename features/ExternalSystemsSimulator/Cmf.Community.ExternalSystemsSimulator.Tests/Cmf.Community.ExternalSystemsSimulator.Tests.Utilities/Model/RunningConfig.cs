namespace Cmf.Community.ExternalSystemsSimulator.Tests.Utilities.Model
{
    public class RunningConfig
    {
        public string DefaultStatusCode;
        public string DefaultResponseBody;
        public RunningMode RunningMode;
    }

    public enum RunningMode
    {
        Full,
        NoStatistics
    }
}
