using System;
using Cmf.Community.ExternalSystemsSimulator.Tests.Utilities.Model.Actions.Parameters;

namespace Cmf.Community.ExternalSystemsSimulator.Tests.Utilities.Model.Actions
{
    /// <summary>
    /// Object representing an action to be executed by the simulator,
    /// containing the necessary information for its execution.
    /// </summary>
    /// <param name="actionType">Type of action to be executed by the simulator</param>
    public class Action(string actionType, object actionParameter)
    {
        public string Id = Guid.NewGuid().ToString();
        public string ActionType = actionType;
        public string? ActionState;
        public object? ActionParameter = actionParameter;
        public string[]? DependsOn;
    }
}
