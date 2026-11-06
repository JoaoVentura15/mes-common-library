using Cmf.Community.ExternalSystemsSimulator.Tests.Utilities.Model.Actions.Parameters;

namespace Cmf.Community.ExternalSystemsSimulator.Tests.Utilities.Model.Actions
{
    /// <summary>
    /// Action to be used to execute a Log Event action for a given entity via the simulator
    /// </summary>
    public class LogEventAction(LogEventParameter actionParameter) : Action("LogEvent", actionParameter)
    {
        public new LogEventParameter ActionParameter = actionParameter;
    }
}
