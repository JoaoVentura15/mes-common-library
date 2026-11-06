using Cmf.Community.ExternalSystemsSimulator.Tests.Utilities.Model;
using Cmf.Custom.TestUtilities;
using Cmf.Foundation.BusinessObjects;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cmf.Community.ExternalSystemsSimulator.Tests.Utilities
{
    /// <summary>
    /// Used to set up Simulator Endpoints.
    /// </summary>
    public static class SimulatorEndpointSetup
    {
        /// <summary>
        /// Set up all endpoints used in the simulator.
        /// </summary>
        /// <param name="configurationEntryManager"></param>
        /// <returns></returns>
        public static ConfigurationEntryManager SetupAll(ConfigurationEntryManager configurationEntryManager = null)
        {
            ValidateControllerRunning();

            configurationEntryManager ??= new ConfigurationEntryManager();

            SetupMAM(configurationEntryManager);
            SetupPLM(configurationEntryManager);
            SetupSAP(configurationEntryManager);
            SetupBatchManager(configurationEntryManager);
            SetupSubconValidator(configurationEntryManager);
            SetupBlueprintSystem(configurationEntryManager);
            SetupBarTender(configurationEntryManager);
            SetupDispatching(configurationEntryManager);
            SetupCTSSyncMaps(configurationEntryManager);
            SetupMaterialSyncMaps(configurationEntryManager);
            SetupSamplingSystem(configurationEntryManager);
            SetupSubstrateMapping(configurationEntryManager);
            SetupSDMPORecover(configurationEntryManager);
            SetupSDMPOIssue(configurationEntryManager);
            SetupSDMAssign(configurationEntryManager);

            return configurationEntryManager;
        }

        /// <summary>
        /// Validates that the simulator controller is running
        /// </summary>
        public static void ValidateControllerRunning()
        {
            Site site = new Site() { Name = "ExternalSystemsSimulatorSite" };
            site.Load();
            AutomationControllerInstance aci =
                AutomationControllerInstanceUtilities.GetAutomationControllerInstance(site);

            Assert.IsTrue(aci?.SystemState == AutomationSystemState.Running,
                "Simulator is not running. Please check your system configuration");
        }

        /// <summary>
        /// Set up MAM endpoints used in the simulator.
        /// </summary>
        /// <param name="configurationEntryManager"></param>
        public static void SetupMAM(ConfigurationEntryManager configurationEntryManager)
        {
            configurationEntryManager.SetConfigValue("/Micron/MAM/BasicURL", SimulatorEndpoints.BaseURL);
            configurationEntryManager.SetConfigValue("/Micron/MAM/Routing/GetLotDetails",
                SimulatorEndpoints.MAMGetLotDetailsEndpoint);
        }

        /// <summary>
        /// Set up PLM endpoints used in the simulator.
        /// </summary>
        /// <param name="configurationEntryManager"></param>
        public static void SetupPLM(ConfigurationEntryManager configurationEntryManager)
        {
            configurationEntryManager.SetConfigValue("/Micron/PLM/ResponseEndpoint/BaseURL",
                SimulatorEndpoints.BaseURL);
            configurationEntryManager.SetConfigValue("/Micron/PLM/ResponseEndpoint/Routing/Acknowledge",
                SimulatorEndpoints.PLMAcknowledgeWorkPackageEndpoint);
            configurationEntryManager.SetConfigValue("/Micron/PLM/ResponseEndpoint/Routing/ProcessedStatus",
                SimulatorEndpoints.PLMProcessedWorkPackageEndpoint);
        }

        /// <summary>
        /// Set up SAP endpoints used in the simulator.
        /// </summary>
        /// <param name="configurationEntryManager"></param>
        public static void SetupSAP(ConfigurationEntryManager configurationEntryManager)
        {
            configurationEntryManager.SetConfigValue("/Micron/SAP/BasicURL", SimulatorEndpoints.BaseURL);
            configurationEntryManager.SetConfigValue("/Micron/SAP/Routing/BOMProductTreeStatus",
                SimulatorEndpoints.SAPBOMProductTreeStatusEndpoint);
            configurationEntryManager.SetConfigValue("/Micron/SAP/Routing/CloseProductionOrder",
                SimulatorEndpoints.SAPCloseProductionOrderEndpoint);
            configurationEntryManager.SetConfigValue("/Micron/SAP/Routing/CreateMetadata",
                SimulatorEndpoints.SAPCreateMetadataEndpoint);
            configurationEntryManager.SetConfigValue("/Micron/SAP/Routing/OpenProductionOrder",
                SimulatorEndpoints.SAPOpenProductionOrderEndpoint);
            configurationEntryManager.SetConfigValue("/Micron/SAP/Routing/ProductMasterData",
                SimulatorEndpoints.SAPProductMasterDataEndpoint);
            configurationEntryManager.SetConfigValue("/Micron/SAP/Routing/RemoveProductionOrder",
                SimulatorEndpoints.SAPRemoveProductionOrderEndpoint);
            configurationEntryManager.SetConfigValue("/Micron/SAP/Routing/ReportLossAndBonus",
                SimulatorEndpoints.SAPReportLossAndBonusEndpoint);
            configurationEntryManager.SetConfigValue("/Micron/SAP/Routing/ReportProductionOrder",
                SimulatorEndpoints.SAPReportProductionOrderEndpoint);
            configurationEntryManager.SetConfigValue("/Micron/SAP/Routing/ShipLot",
                SimulatorEndpoints.SAPShipLotEndpoint);
        }


        /// <summary>
        /// Set up BatchManager endpoints used in the simulator.
        /// </summary>
        /// <param name="configurationEntryManager"></param>
        public static void SetupBatchManager(ConfigurationEntryManager configurationEntryManager)
        {
            configurationEntryManager.SetConfigValue("/Micron/BatchManager/BaseURL", SimulatorEndpoints.BaseURL);
            configurationEntryManager.SetConfigValue("/Micron/BatchManager/Routing/AbandoningBatch",
                SimulatorEndpoints.BatchManagerAbandoningBatchEndpoint);
            configurationEntryManager.SetConfigValue("/Micron/BatchManager/Routing/CancellingBatch",
                SimulatorEndpoints.BatchManagerCancellingBatchEndpoint);
            configurationEntryManager.SetConfigValue("/Micron/BatchManager/Routing/CommittingBatch",
                SimulatorEndpoints.BatchManagerCommittingBatchEndpoint);
            configurationEntryManager.SetConfigValue("/Micron/BatchManager/Routing/CompletingBatch",
                SimulatorEndpoints.BatchManagerCompletingBatchEndpoint);
            configurationEntryManager.SetConfigValue("/Micron/BatchManager/Routing/CreateBatch",
                SimulatorEndpoints.BatchManagerCreateBatchEndpoint);
            configurationEntryManager.SetConfigValue("/Micron/BatchManager/Routing/DataCollectionUpload",
                SimulatorEndpoints.BatchManagerDataCollectionUploadEndpoint);
            configurationEntryManager.SetConfigValue("/Micron/BatchManager/Routing/MaterialLoadBatch",
                SimulatorEndpoints.BatchManagerMaterialLoadBatchEndpoint);
            configurationEntryManager.SetConfigValue("/Micron/BatchManager/Routing/MaterialUnloadBatch",
                SimulatorEndpoints.BatchManagerMaterialUnloadBatchEndpoint);
            configurationEntryManager.SetConfigValue("/Micron/BatchManager/Routing/StagingBatch",
                SimulatorEndpoints.BatchManagerStagingBatchEndpoint);
            configurationEntryManager.SetConfigValue("/Micron/BatchManager/Routing/StoppingBatch",
                SimulatorEndpoints.BatchManagerStoppingBatchEndpoint);
            configurationEntryManager.SetConfigValue("/Micron/BatchManager/Routing/TrackingOutBatch",
                SimulatorEndpoints.BatchManagerTrackingOutBatchEndpoint);
            configurationEntryManager.SetConfigValue("/Micron/BatchManager/Routing/UnstagingBatch",
                SimulatorEndpoints.BatchManagerUnstagingBatchEndpoint);
            configurationEntryManager.SetConfigValue("/Micron/BatchManager/Routing/UpdateBatch",
                SimulatorEndpoints.BatchManagerUpdateBatchEndpoint);
            configurationEntryManager.SetConfigValue("/Micron/BatchManager/Routing/HostGetEquipConstDefinition",
                SimulatorEndpoints.BatchManagerHostGetEquipConstDefinitionEndpoint);
        }

        /// <summary>
        /// Set up SubconValidator endpoints used in the simulator.
        /// </summary>
        /// <param name="configurationEntryManager"></param>
        public static void SetupSubconValidator(ConfigurationEntryManager configurationEntryManager)
        {
            configurationEntryManager.SetConfigValue("/Micron/SubconValidator/BaseURL", SimulatorEndpoints.BaseURL);
            configurationEntryManager.SetConfigValue("/Micron/SubconValidator/Routing/NotifySubconValidator",
                SimulatorEndpoints.SubconValidatorNotifySubconValidatoEndpoint);
        }

        /// <summary>
        /// Set up Blueprint endpoints used in the simulator.
        /// </summary>
        /// <param name="configurationEntryManager"></param>
        public static void SetupBlueprintSystem(ConfigurationEntryManager configurationEntryManager)
        {
            configurationEntryManager.SetConfigValue("/Micron/BlueprintSystem/BasicURL",
                SimulatorEndpoints.BaseURL + SimulatorEndpoints.BlueprintSystemEndpoint);
        }

        /// <summary>
        /// Set up Bartender endpoints used in the simulator.
        /// </summary>
        /// <param name="configurationEntryManager"></param>
        public static void SetupBarTender(ConfigurationEntryManager configurationEntryManager)
        {
            configurationEntryManager.SetConfigValue("/Micron/BarTender/WebServiceURL",
                SimulatorEndpoints.BaseURL + SimulatorEndpoints.BarTenderEndpoint);
        }

        /// <summary>
        /// Set up Dispatching endpoints used in the simulator.
        /// </summary>
        /// <param name="configurationEntryManager"></param>
        public static void SetupDispatching(ConfigurationEntryManager configurationEntryManager)
        {
            configurationEntryManager.SetConfigValue("/Micron/Dispatching/WebServiceURL",
                SimulatorEndpoints.BaseURL + SimulatorEndpoints.DispatchingEndpoint);
        }

        /// <summary>
        /// Set up CTSSyncMaps endpoints used in the simulator.
        /// </summary>
        /// <param name="configurationEntryManager"></param>
        public static void SetupCTSSyncMaps(ConfigurationEntryManager configurationEntryManager)
        {
            configurationEntryManager.SetConfigValue("/Micron/CTSSyncMaps/Endpoint",
                SimulatorEndpoints.BaseURL + SimulatorEndpoints.CTSSyncMapsEndpoint);
        }

        /// <summary>
        /// Set up MaterialSyncMaps endpoints used in the simulator.
        /// </summary>
        /// <param name="configurationEntryManager"></param>
        public static void SetupMaterialSyncMaps(ConfigurationEntryManager configurationEntryManager)
        {
            configurationEntryManager.SetConfigValue("/Micron/MaterialSyncMaps/Endpoint",
                SimulatorEndpoints.BaseURL + SimulatorEndpoints.MaterialSyncMapsEndpoint);
        }

        /// <summary>
        /// Set up SamplingSystem endpoints used in the simulator.
        /// </summary>
        /// <param name="configurationEntryManager"></param>
        public static void SetupSamplingSystem(ConfigurationEntryManager configurationEntryManager)
        {
            configurationEntryManager.SetConfigValue("/Micron/SamplingSystem/BasicURL",
                SimulatorEndpoints.BaseURL + SimulatorEndpoints.SamplingSystemEndpoint);
        }

        /// <summary>
        /// Set up SubstrateMapping endpoints used in the simulator.
        /// </summary>
        /// <param name="configurationEntryManager"></param>
        public static void SetupSubstrateMapping(ConfigurationEntryManager configurationEntryManager)
        {
            configurationEntryManager.SetConfigValue("/Micron/SubstrateMapping/SubstrateMapRequest/Endpoint",
                SimulatorEndpoints.BaseURL + SimulatorEndpoints.SubstrateMapAPIEndpoint);
        }

        /// <summary>
        /// Set up SDM endpoints used in the simulator.
        /// </summary>
        /// <param name="configurationEntryManager"></param>
        public static void SetupSDMPORecover(ConfigurationEntryManager configurationEntryManager)
        {
            configurationEntryManager.SetConfigValue("/Micron/SDM/MESAuto/BaseURL", SimulatorEndpoints.BaseURL);
            configurationEntryManager.SetConfigValue("/Micron/SDM/MESAuto/Routing/SDMPORecover",
                SimulatorEndpoints.SDMPORecover);
        }

        /// <summary>
        /// Set up SDMAssign endpoints used in the simulator.
        /// </summary>
        /// <param name="configurationEntryManager"></param>
        public static void SetupSDMPOIssue(ConfigurationEntryManager configurationEntryManager)
        {
            configurationEntryManager.SetConfigValue("/Micron/SDM/MESAuto/BaseURL",
                SimulatorEndpoints.BaseURL);
            configurationEntryManager.SetConfigValue("/Micron/SDM/MESAuto/Routing/SDMPOIssue",
                SimulatorEndpoints.MaterialSDMPOIssue);
        }

        /// <summary>
        /// Set up SDMAssign endpoints used in the simulator.
        /// </summary>
        /// <param name="configurationEntryManager"></param>
        public static void SetupSDMAssign(ConfigurationEntryManager configurationEntryManager)
        {
            configurationEntryManager.SetConfigValue("/Micron/SDM/MESAuto/BaseURL",
                SimulatorEndpoints.BaseURL);
            configurationEntryManager.SetConfigValue("/Micron/SDM/MESAuto/Routing/SDMAssign",
                SimulatorEndpoints.SDMAssignAPIEndpoint);
        }
    }
}
