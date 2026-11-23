using System;
using System.Collections.Generic;

namespace Cmf.Audit.Actions
{
    public abstract class DeeDevBase
    {
        public abstract bool DeeTestCondition(Dictionary<string, object> Input);

        public abstract Dictionary<string, object> DeeActionCode(Dictionary<string, object> Input);

        public static void UseReference(string assembly, string nameSpace) { }
    }
}