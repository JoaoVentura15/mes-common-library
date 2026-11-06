using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Cmf.Community.ExternalSystemsSimulator.Tests.Utilities.Model
{
    public class EndpointStatistic
    {
        public string? RequestId;
        public EndpointConfig? EndpointConfig;
        public object? ReceivedBody;
        public object? ReplySent;
        public int? StatusCodeSent;
        public object? Error;
    }
}
