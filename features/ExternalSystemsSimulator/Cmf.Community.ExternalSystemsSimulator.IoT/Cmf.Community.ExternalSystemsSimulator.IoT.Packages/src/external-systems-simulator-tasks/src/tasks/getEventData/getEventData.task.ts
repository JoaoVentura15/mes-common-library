import { Task, System, TaskBase } from "@criticalmanufacturing/connect-iot-controller-engine";
import { EndpointConfig } from "../../common/EndpointConfig";
import { Action } from "../../common/Action";
import { CustomExtendedData } from "../../common/CustomExtendedData";
import { Cmf } from "cmf-lbos";

/** Default values for settings */
export const SETTINGS_DEFAULTS: GetEventDataSettings = {
	example: "Hello World",
};

import GetObjectByNameInput = Cmf.Foundation.BusinessOrchestration.GenericServiceManagement.InputObjects.GetObjectByNameInput;
import { DataStoreLocation } from "@criticalmanufacturing/connect-iot-controller-engine";

/**
 * @whatItDoes
 *
 * This task retrieves event data from the Automation Event raw data, processes it, and emits relevant outputs such as:
 *  - Request ID
 *  - Subject
 *  - Body
 *  - Running endpoint configuration
 *  - Actions to be executed
 *
 * @howToUse
 *
 * Right after an OnEquipmentEvent of a RestServer event, add this task with RequestId and EventRawData inputs
 *
 * ### Inputs
 * * `String` : **RequestId** - Automation Event Request Id
 * * `Object` : **EventRawData** - Event raw data to be processed
 * * `any` : **activate** - Activate the task 
 *
 * ### Outputs
 * * `String` : **RequestId** - Automation Event Request Id
 * * `String` : **Subject** - Subject of the event
 * * `Object` : **Body** - Body of the event
 * * `Object` : **RunningEndpointConfig** - Current running endpoint configuration
 * * `bool` : **NoActionToExecute** - Indicates if there are no actions to be executed
 * * `Object` : **Actions** - Actions to be executed
 * 
 * * `bool`  : ** success ** - Triggered when the the task is executed with success
 * * `Error` : ** error ** - Triggered when the task failed for some reason
 *
 * ### Settings
 * See {@see GetEventDataSettings}
 */
@Task.Task()
export class GetEventDataTask extends TaskBase implements GetEventDataSettings {

    /** Accessor helper for untyped properties and output emitters. */
    // [key: string]: any;

    /** **Inputs** */
	/** Request Id */
	public inRequestId: string = "";
	/** EventRawData */
	public eventRawData: object = null;

    /** **Outputs** */
	/** RequestId */
	public outRequestId: Task.Output<string> = new Task.Output<string>();
	/** Subject */
	public subject: Task.Output<string> = new Task.Output<string>();
	/** Body */
	public body: Task.Output<object> = new Task.Output<object>();
	/** RunningEndpointConfig */
	public runningEndpointConfig: Task.Output<object> = new Task.Output<object>();
	/** NoActionToExecute */
	public noActionToExecute: Task.Output<boolean> = new Task.Output<boolean>();
	/** Actions */
	public actions: Task.Output<object> = new Task.Output<object>();

    /** Properties Settings */
	/** Information about the example setting */
	example: string;

    /**
     * When one or more input values is changed this will be triggered,
     * @param changes Task changes
     */
    public override async onChanges(changes: Task.Changes): Promise<void> {
        if (changes["activate"]) {
            // It is advised to reset the activate to allow being reactivated without the value being different
            this.activate = undefined;

            var eventData : any = this.eventRawData;    
            
            if(!eventData){
                this._logger.error(`Event raw data is missing!`);
                this.error.emit(new Error("Event raw data is missing!"));
                return;
            }
        
            var subjectName = eventData.event.Name;
            var messageBody = eventData.values.find((y: { property: { Name: string; }; }) => y.property.Name === "MessageBody").value;
            var extendedData : CustomExtendedData = JSON.parse(eventData?.event?.ExtendedData) as CustomExtendedData;
            var actions : Action[];
            var endpointConfigList = await this._dataStore.retrieve(subjectName, undefined) as EndpointConfig[]
            var endpointConfig : EndpointConfig = undefined;
            var requestId = this.inRequestId;

            if (!requestId){
                requestId  = eventData.values.find((y: { property: { Name: string; }; }) => y.property.Name === "RequestId").value;
            }           
            
            if(endpointConfigList){
                endpointConfig = endpointConfigList.pop();
                await this._dataStore.store(subjectName, endpointConfigList.length > 0 ? endpointConfigList : undefined, DataStoreLocation.Persistent);
            }

            if(!endpointConfig && extendedData){
                if(extendedData.DocumentName){
                    //Get Document Id

                    var getInput = new GetObjectByNameInput();
                    getInput.Name = extendedData.DocumentName;
                    getInput.Type = "Cmf.Foundation.BusinessObjects.Document";

                    var document = (await this._systemAPI.call(getInput) as any).Instance;

                    //Get Document data
                    var DownloadDocumentStreamingInput = Cmf.Services.GenericServiceManagement.DownloadDocumentStreamingInput;
                    
                    var serviceInput : any = {};
                    serviceInput["constructor"] = {
                        "_CMFInternal_HTTPMethod": DownloadDocumentStreamingInput._CMFInternal_HTTPMethod,
                        "_CMFInternal_URLSuffix": DownloadDocumentStreamingInput._CMFInternal_URLSuffix + "/" + document.Id 
                    }    

                    extendedData = await this._systemAPI.call(serviceInput) as any;
                } 
                
                if(extendedData?.DefaultBody || extendedData?.DefaultStatusCode || extendedData.DefaultActions ){
                    endpointConfig = {
                        Id: requestId,
                        BodyToSend: extendedData.DefaultBody,
                        StatusCode: extendedData.DefaultStatusCode,  
                        ActionsToExecute: extendedData.DefaultActions          
                    } as EndpointConfig;
                }
            }

            endpointConfig = this.interpolateObject(endpointConfig, messageBody);
            
            if(endpointConfig?.ActionsToExecute){
                actions = endpointConfig.ActionsToExecute;
            }        

            // emit output during execution: outputs.output1.emit("something");            
            
            if(actions && actions.length > 0){
                this.actions.emit(actions);    
            } else {
                this.noActionToExecute.emit(true);
                this.actions.emit(undefined);
            }

            this.outRequestId.emit(requestId);
            this.subject.emit(subjectName);
            this.body.emit(messageBody);
            this.runningEndpointConfig.emit(endpointConfig);

            this.success.emit(true);
        }
    }

    public getByPath(obj: any, path: string): any {
        // Normalize bracket notation: "Materials[0].Name" -> "Materials.0.Name"
        const normalizedPath = path.replace(/\[(\d+)\]/g, ".$1");
        return normalizedPath.split(".").reduce((acc, key) => (acc == null ? undefined : acc[key]), obj);
    }

    public interpolateString(
        template: string,
        params: Record<string, any>,
        start = "{{",
        end = "}}"
    ): string {
        const esc = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
        const pattern = new RegExp(`${esc(start)}([^${esc(end[0])}]+)${esc(end)}`, "g");

        return template.replace(pattern, (_, rawKey: string) => {
            const key = rawKey.trim();               // e.g. "material.Id"
            const value = this.getByPath(params, key);    // look up in request body
            return value != null ? String(value) : "";
        });
    }

    public interpolateObject<T>(template: T, params: Record<string, any>): T {
        if (typeof template === "string") {
            return  this.interpolateString(template, params) as unknown as T;
        }

        if (Array.isArray(template)) {
            return template.map(item =>  this.interpolateObject(item, params)) as unknown as T;
        }

        if (template && typeof template === "object") {
            const result: any = {};
            for (const [k, v] of Object.entries(template)) {
            result[k] =  this.interpolateObject(v as any, params);
            }
            return result;
        }

        return template;
    }


    /** Right after settings are loaded, create the needed dynamic outputs. */
    public override async onBeforeInit(): Promise<void> {
    }

    /** Initialize this task, register any event handler, etc */
    public override async onInit(): Promise<void> {
        this.sanitizeSettings(SETTINGS_DEFAULTS);
    }

    /** Cleanup internal data, unregister any event handler, etc */
    public override async onDestroy(): Promise<void> {
    }
}

// Add settings here
/** GetEventData Settings object */
export interface GetEventDataSettings extends System.TaskDefaultSettings {
	/** Information about the example setting */
	example: string;
}


