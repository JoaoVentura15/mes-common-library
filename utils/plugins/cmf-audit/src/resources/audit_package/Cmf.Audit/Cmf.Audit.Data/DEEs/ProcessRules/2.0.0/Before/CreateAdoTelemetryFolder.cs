using System;
using System.Collections.Generic;
using System.Data;
using Cmf.Foundation.BusinessObjects.Abstractions;
using Cmf.Foundation.BusinessObjects.QueryObject;
using Cmf.Foundation.BusinessOrchestration.Abstractions;
using Cmf.Foundation.BusinessOrchestration.GenericServiceManagement.InputObjects;
using Cmf.Foundation.Common;
using Cmf.Foundation.Common.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Cmf.Audit.Actions.ProcessRules;

public class CreateAdoTelemetryFolder : DeeDevBase
{
    public override Dictionary<string, object> DeeActionCode(Dictionary<string, object> Input)
    {
        //---Start DEE Code---
        UseReference("%MicrosoftNetPath%System.Data.Common.dll", "System.Data");
        UseReference("Cmf.Foundation.BusinessObjects.dll", "Cmf.Foundation.BusinessObjects.QueryObject");
        UseReference("Cmf.Foundation.BusinessOrchestration.dll", "Cmf.Foundation.BusinessOrchestration.Abstractions");
        UseReference("Cmf.Foundation.BusinessOrchestration.dll", "Cmf.Foundation.BusinessOrchestration.GenericServiceManagement.InputObjects");

        var serviceProvider = (IServiceProvider)Input["ServiceProvider"];
        var entityFactory = serviceProvider.GetService<IEntityFactory>();
        var genericService = serviceProvider.GetService<IGenericServiceOrchestration>();

        IFolder FindFolder(string path)
        {
            IQueryObject query = entityFactory.Create<IQueryObject>();
            query.Name = "AuditFindIoTEventFolder";
            query.EntityTypeName = "Folder";
            query.Query = new Query
            {
                Filters = new FilterCollection
                {
                    new Filter { Name = "Path", ObjectName = "Folder", ObjectAlias = "Folder_1", Operator = FieldOperator.IsEqualTo, Value = path, LogicalOperator = LogicalOperator.Nothing, FilterType = Cmf.Foundation.BusinessObjects.QueryObject.Enums.FilterType.Normal }
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
                return null;
            }
            IFolder folder = entityFactory.Create<IFolder>();
            folder.Load(result.Tables[0].Rows[0].Field<long>("Id"));
            return folder;
        }

        void EnsureFolder(string parentPath)
        {
            if (FindFolder(parentPath + "\\ADOTelemetry") != null)
            {
                return;
            }
            IFolder root = FindFolder(parentPath) ?? throw new Exception($"Parent folder {parentPath} was not found.");
            IFolder folder = entityFactory.Create<IFolder>();
            folder.Name = "ADOTelemetry";
            folder.ParentFolder = root;
            var result = genericService.CreateObject(new CreateObjectInput { Object = folder });
            if (result?.Object == null)
            {
                throw new Exception($"Could not create the ADOTelemetry folder under {parentPath}.");
            }
        }

        EnsureFolder("\\IoTEventDefinitions");
        EnsureFolder("\\Datasets\\UserDefined");
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