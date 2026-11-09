using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Cmf.Community.ExternalSystemsSimulator.Tests.Utilities.Model.Actions.Parameters
{
    /// <summary>
    /// Represents a parameter set for invoking a custom code function within an action context.
    /// </summary>
    public class CustomCodeParameter : IActionParameter
    {
        /// <summary>
        /// Names of the parameters to be passed to the custom code function.
        /// The order of names should match the order of values in the Parameters array.
        /// </summary>
        public string[]? ParameterNames;

        /// <summary>
        /// Parameter values to be passed to the custom code function.
        /// The order of values should match the order of names in the ParameterNames array.
        /// </summary>
        public object[]? Parameters;

        /// <summary>
        /// Function code to be executed, written in JavaScript.
        /// This code will be invoked with the specified parameters. Return value will only be logged by the simulator and won't have any effect on further behaviours.
        /// </summary>
        public string? Function;
    }
}
