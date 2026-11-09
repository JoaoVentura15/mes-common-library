using Cmf.Community.ExternalSystemsSimulator.Tests.Utilities.Model.Actions.Parameters;

namespace Cmf.Community.ExternalSystemsSimulator.Tests.Utilities.Model.Actions
{
    /// <summary>
    /// Action to be used to execute Custom Code in the simulator
    /// </summary>
    /// <remarks>Code to be used should be written in JavaScript</remarks>
    public class CustomCodeAction(CustomCodeParameter actionParameter) : Action("CustomCode", actionParameter)
    {
        public new CustomCodeParameter ActionParameter = actionParameter;
    }
}
