namespace Cmf.Community.ExternalSystemsSimulator.Tests.Utilities.Model
{
    public static class SimulatorEndpoints
    {
        public const string BaseURL = "http://externalsystemssimulatormanager:8080";
        public const string SubconValidatorNotifySubconValidatoEndpoint = "/api/SubconValidator/NotifySubconValidator";
        public const string SAPBOMProductTreeStatusEndpoint = "/api/SAP/BOMProductTreeStatus/";
        public const string SAPCloseProductionOrderEndpoint = "/api/SAP/CloseProductionOrder/";
        public const string SAPCreateMetadataEndpoint = "/api/SAP/CreateMetadata/";
        public const string SAPOpenProductionOrderEndpoint = "/api/SAP/OpenProductionOrder/";
        public const string SAPProductMasterDataEndpoint = "/api/SAP/ProductMasterData/";
        public const string SAPRemoveProductionOrderEndpoint = "/api/SAP/RemoveProductionOrder/";
        public const string SAPReportLossAndBonusEndpoint = "/api/SAP/ReportLossAndBonus/";
        public const string SAPReportProductionOrderEndpoint = "/api/SAP/ReportProductionOrder/";
        public const string SAPShipLotEndpoint = "/api/SAP/ShipLot/";
        public const string SamplingSystemEndpoint = "/api/SamplingSystem/";
        public const string BatchManagerAbandoningBatchEndpoint = "/api/BatchManager/AbandoningBatch/";
        public const string BatchManagerCancellingBatchEndpoint = "/api/BatchManager/CancellingBatch/";
        public const string BatchManagerCommittingBatchEndpoint = "/api/BatchManager/CommittingBatch/";
        public const string BatchManagerCompletingBatchEndpoint = "/api/BatchManager/CompletingBatch/";
        public const string BatchManagerCreateBatchEndpoint = "/api/BatchManager/CreateBatch/";
        public const string BatchManagerDataCollectionUploadEndpoint = "/api/BatchManager/DataCollectionUpload/";
        public const string BatchManagerMaterialLoadBatchEndpoint = "/api/BatchManager/MaterialLoadBatch/";
        public const string BatchManagerMaterialUnloadBatchEndpoint = "/api/BatchManager/MaterialUnloadBatch/";
        public const string BatchManagerStagingBatchEndpoint = "/api/BatchManager/StagingBatch/";
        public const string BatchManagerStoppingBatchEndpoint = "/api/BatchManager/StoppingBatch/";
        public const string BatchManagerTrackingOutBatchEndpoint = "/api/BatchManager/TrackingOutBatch/";
        public const string BatchManagerUnstagingBatchEndpoint = "/api/BatchManager/UnstagingBatch/";
        public const string BatchManagerUpdateBatchEndpoint = "/api/BatchManager/UpdateBatch/";
        public const string BatchManagerHostGetEquipConstDefinitionEndpoint = "/api/HostGetEquipConstDefinition/";
        public const string PLMAcknowledgeWorkPackageEndpoint = "/api/PLM/AcknowledgeWorkPackage/";
        public const string PLMGeneratePackageEndpoint = "/api/PLM/GeneratePackage/";
        public const string PLMProcessedWorkPackageEndpoint = "/api/PLM/ProcessedWorkPackage/";
        public const string MaterialSyncMapsEndpoint = "/api/MaterialSyncMaps/";
        public const string MAMGetLotDetailsEndpoint = "/api/MAM/GetLotDetails/";
        public const string DispatchingEndpoint = "/api/Dispatching/";
        public const string CTSSyncMapsEndpoint = "/api/CTSSyncMaps/";
        public const string BlueprintSystemEndpoint = "/api/BlueprintSystem/";
        public const string BarTenderEndpoint = "/api/BarTender/";
        public const string SubstrateMapAPIEndpoint = "/api/SubstrateMapAPI/";
        public const string GSWRExperimentDefinitionCreated = "/api/GSWR/ExperimentDefinitionCreated/";
        public const string GSWRExperimentStepStart = "/api/GSWR/ExperimentStepStart/";
        public const string GSWRExperimentCreated = "/api/GSWR/ExperimentCreated/";
        public const string GSWRExperimentClosed = "/api/GSWR/ExperimentClosed/";
        public const string GSWRExperimentStepCompleted = "/api/GSWR/ExperimentStepCompleted/";
        public const string SDMPORecover = "/api/Material/SDMPORecover";
        public const string MaterialSDMPOIssue = "/api/Material/SDMPOIssue/";
        public const string SDMAssignAPIEndpoint = "/api/SDMAssign/";
        public const string ContainerMapUpdate = "/api/Carrier/ContainerMapUpdate/";
        public const string EnclosureAssemblyMakeMeasurementSigma = "/api/EnclosureAssembly/MakeMeasurementSigma";
        public const string SyncCarrierMaps = "/api/Container/SyncCarrierMaps/";
    }
}
