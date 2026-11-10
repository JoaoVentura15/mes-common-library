import { Task, System, TaskBase } from "@criticalmanufacturing/connect-iot-controller-engine";

/** Default values for settings */
export const SETTINGS_DEFAULTS: GetEventDataSettings = {
	example: "Hello World",
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

            // ... code here
            this.success.emit(true);

            // or
            this._logger.error(`Something very wrong just happened! Log it!`);
            this.error.emit(new Error ("Will stop processing, but Error output will be triggered with this value"));
        }
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


