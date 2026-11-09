using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Cmf.Community.ExternalSystemsSimulator.Tests.Utilities.Model.Actions.Parameters
{
    public  class LogEventParameter : IActionParameter
    {
        /// <summary>
        /// Entity type for which the event will be logged.
        /// This should match the entity type expected by the system for the given event.
        /// </summary>
        public string EntityType;

        /// <summary>
        /// Optional: ID of the entity for which to execute the LogEvent action. 
        /// </summary>
        public string? EntityId;
        /// <summary>
        /// Optional: Name of the entity for which to execute the LogEvent action
        /// </summary>
        public string? EntityName;

        /// <summary>
        /// Target state to which the entity will be transitioned on the LogEvent action.
        /// This should match a valid state, and, a valid state transition should exist between the entity current state and the target state.
        /// </summary>
        public string ToState;

        /// <summary>
        /// Property used to ignore the serviceId of the last executed action when executing this LogEvent action.
        /// </summary>
        /// <remarks>Default value is false</remarks>
        public bool IgnoreLastServiceId = false;
    }
}
