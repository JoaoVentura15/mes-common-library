import { Task, System, TaskBase, DataStoreLocation } from "@criticalmanufacturing/connect-iot-controller-engine";
import { EndpointConfig } from "../../common/model/EndpointConfig";
import { RunningConfig } from "../../common/model/RunningConfig";
import { RunningMode } from "../../common/model/RunningMode";
import { EndpointStatistic } from "../../common/model/EndpointStatistics";
import { InterpolationUtilities } from "../../common/utilities/interpolationUtilities";

/** Default values for settings */
export const SETTINGS_DEFAULTS: CreateAndSendBodyResponseSettings = {
	timeoutKey: 60000,
};

/**
 * @whatItDoes
 *
 * This task does something ... describe here
 *
 * @howToUse
 *
 * yada yada yada
 *
 * ### Inputs
 * * `any` : **activate** - Activate the task
 *
 * ### Outputs
 *
 * * `bool`  : ** success ** - Triggered when the the task is executed with success
 * * `Error` : ** error ** - Triggered when the task failed for some reason
 *
 * ### Settings
 * See {@see CreateAndSendBodyResponseSettings}
 */
@Task.Task()
export class CreateAndSendBodyResponseTask extends TaskBase implements CreateAndSendBodyResponseSettings {

    /** Accessor helper for untyped properties and output emitters. */
    // [key: string]: any;

    /** **Inputs** */
	/** RequestId */
	public requestId: string = "";
	/** Subject */
	public subject: string = "";
	/** Body */
	public body: object = null;
	/** RunningEndpointConfig */
	public runningEndpointConfig: object = null;
	/** StatusCode */
	public statusCode: number = 500;
	/** Error */
	public Error: object = null;

    /** **Outputs** */


    /** Properties Settings */
	/** Maximum ammount of time (in ms) to wait for the event to occur before disable the handler */
	timeoutKey: number;

    /**
     * When one or more input values is changed this will be triggered,
     * @param changes Task changes
     */
    public override async onChanges(changes: Task.Changes): Promise<void> {
        if (changes["activate"]) {
  
            var endpointConfig: EndpointConfig = this.runningEndpointConfig as EndpointConfig;
            var currentRunningConfig : RunningConfig = await this._dataStore.retrieve("RunningConfig", undefined) ?? new RunningConfig();
            
            var subject: string = this.subject;
            var statSubject = subject+"Stat";    

            var currentEndpointStat: EndpointStatistic[] = await this._dataStore.retrieve(statSubject, undefined) ?? [];
            this._logger.info("Current Stat Data " + JSON.stringify(currentEndpointStat));
            
            var statistic = new EndpointStatistic();

            if(this.Error){
                statistic.Error = this.Error;
                statistic.StatusCodeSent = 500;          
                if(currentRunningConfig.RunningMode != RunningMode.NoStatistics){
                    currentEndpointStat.push(statistic)
                }
                
                await this._dataStore.store(statSubject, currentEndpointStat, DataStoreLocation.Persistent);
                try {
                    await this.sendReplyAsync(this.requestId, this.Error, 500);
                    this.success.emit(true);
                } catch (error) {
                    this._logger.error("Failed to send reply: " + error);
                    this.error.emit(error instanceof Error ? error : new Error(String(error)));     
                }   
                return;
            }

            if(currentRunningConfig.RunningMode == RunningMode.NoStatistics){
                try {
                    await this.sendReplyAsync(this.requestId, endpointConfig.BodyToSend ?? currentRunningConfig.DefaultResponseBody, endpointConfig.StatusCode ?? currentRunningConfig.DefaultStatusCode);
                    this.success.emit(true);
                } catch (error) {
                    this._logger.error("Failed to send reply: " + error);
                    this.error.emit(error instanceof Error ? error : new Error(String(error)));     
                }
                return;
            }

            statistic.ReceivedBody = this.body;

            if(endpointConfig){
                statistic.EndpointConfig = endpointConfig;
                statistic.ReplySent = endpointConfig.BodyToSend ?? {};
                statistic.StatusCodeSent = endpointConfig.StatusCode ?? 200;            
            }

            statistic.ReplySent = statistic.ReplySent ?? this.body ?? {};
            statistic.StatusCodeSent = statistic.StatusCodeSent ?? this.statusCode ?? 200;

            statistic.ReplySent = InterpolationUtilities.interpolateObject(statistic.ReplySent, statistic.ReceivedBody);

            currentEndpointStat.push(statistic)
            await this._dataStore.store(statSubject, currentEndpointStat, DataStoreLocation.Persistent);
            this._logger.info("Stored on " + statSubject + " the data " + JSON.stringify(currentEndpointStat)   );
            
            try{
                await this.sendReplyAsync(this.requestId, statistic.ReplySent, statistic.StatusCodeSent)
                this.success.emit(true);
            } catch (error) {
                this._logger.error("Failed to send reply: " + error);
                this.error.emit(error instanceof Error ? error : new Error(String(error)));     
            }          
            
            
            return; 

            // emit output during execution: outputs.output1.emit("something");
            // return example: return { output1: inputs.input1, output2: "Hello World" };
        }
    }

    public async sendReplyAsync(requestId: string, body: any, statusCode: number): Promise<any> {
        return await this._driverProxy.executeCommand("Request Reply Command", new Map<string, any>([
            ["RequestId", requestId],
            ["Body", body],
            ["StatusCode", statusCode]
        ]));
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
/** CreateAndSendBodyResponse Settings object */
export interface CreateAndSendBodyResponseSettings extends System.TaskDefaultSettings {
	/** Maximum ammount of time (in ms) to wait for the event to occur before disable the handler */
	timeoutKey: number;
}


