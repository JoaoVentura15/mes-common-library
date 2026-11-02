using System;
using System.Collections.Generic;
using System.Data;
using Cmf.Foundation.BusinessObjects.Abstractions;
using Cmf.Foundation.BusinessObjects.QueryObject;
using Cmf.Foundation.Common;
using Cmf.Foundation.Common.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Cmf.Audit.Actions.Workarounds;

public class ResolveAdoTelemetryFolder : DeeDevBase
{
    public override Dictionary<string, object> DeeActionCode(Dictionary<string, object> Input)
    {
        //---Start DEE Code---
        UseReference("%MicrosoftNetPath%System.Data.Common.dll", "System.Data");
        UseReference("Cmf.Foundation.BusinessObjects.dll", "Cmf.Foundation.BusinessObjects.QueryObject");

        var serviceProvider = (IServiceProvider)Input["ServiceProvider"];
        var entityFactory = serviceProvider.GetService<IEntityFactory>();
        IFolder targetFolder = null;
        var eventNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PipelineRun", "PipelineStep", "Approval", "RunChange", "TestRun", "TestFailure",
            "PullRequest", "PullRequestWorkItem", "WorkItemFlow", "WorkItemHistory", "WorkItemScope",
            "ReleaseSchedule", "ReleaseTag", "PullRequestFiles", "Sprint", "AgentJob"
        };

        foreach (IIoTEventDefinition definition in (IIoTEventDefinitionCollection)Input["IoTEventDefinitionCollection"])
        {
            string path = definition.Folder?.Path?.Replace('/', '\\').Trim('\\');
            if (!eventNames.Contains(definition.Name) || path == null ||
                string.Equals(path, "IoTEventDefinitions\\ADOTelemetry", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(path.Substring(path.LastIndexOf('\\') + 1), "ADOTelemetry", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (targetFolder == null)
            {
                IQueryObject query = entityFactory.Create<IQueryObject>();
                query.Name = "AuditFindIoTEventFolder";
                query.EntityTypeName = "Folder";
                query.Query = new Query
                {
                    Filters = new FilterCollection
                    {
                        new Filter { Name = "Path", ObjectName = "Folder", ObjectAlias = "Folder_1", Operator = FieldOperator.IsEqualTo, Value = "\\IoTEventDefinitions\\ADOTelemetry", LogicalOperator = LogicalOperator.Nothing, FilterType = Cmf.Foundation.BusinessObjects.QueryObject.Enums.FilterType.Normal }
                    },
                    Fields = new FieldCollection
                    {
                        new Field { Name = "Id", Alias = "Id", ObjectName = "Folder", ObjectAlias = "Folder_1", Position = 0 }
                    },
                    Relations = new RelationCollection()
                };
                DataSet result = query.Execute(check: false, queryParameters: null);
                if (result == null || result.Tables.Count == 0 || result.Tables[0].Rows.Count == 0)
                {
                    throw new Exception("ADOTelemetry IoT event definitions folder was not found.");
                }
                targetFolder = entityFactory.Create<IFolder>();
                targetFolder.Load(result.Tables[0].Rows[0].Field<long>("Id"));
            }

            definition.Folder = targetFolder;
        }
        //---End DEE Code---
        return Input;
    }

    public override bool DeeTestCondition(Dictionary<string, object> Input)
    {
        //---Start DEE Condition Code---
        return true;
        //---End DEE Condition Code---
    }
}