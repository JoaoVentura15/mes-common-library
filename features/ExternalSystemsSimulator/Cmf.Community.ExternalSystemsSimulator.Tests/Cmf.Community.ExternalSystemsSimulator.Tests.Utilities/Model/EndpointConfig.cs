using System.Collections.Generic;
using Cmf.Community.ExternalSystemsSimulator.Tests.Utilities.Model.Actions;

namespace Cmf.Community.ExternalSystemsSimulator.Tests.Utilities.Model
{
    public class EndpointConfig
    {
        public string? Id;
        public int StatusCode;
        public object? BodyToSend;
        public List<Action>? ActionsToExecute;
    }
}
