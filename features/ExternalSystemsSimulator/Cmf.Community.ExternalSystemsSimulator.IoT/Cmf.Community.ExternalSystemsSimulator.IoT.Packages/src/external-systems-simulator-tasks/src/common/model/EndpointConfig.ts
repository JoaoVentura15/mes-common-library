import { Action } from "./Action";

export class EndpointConfig {
    Id: string|undefined;
    StatusCode : number|undefined;
    BodyToSend: any;
    ActionsToExecute: Action[] | undefined;

    constructor(init?: Partial<EndpointConfig>) {
        Object.assign(this, init);
        if(!this.Id){
            this.Id = crypto.randomUUID();
        }
    }

    static GetNew(statusCodeToSet: number, bodyToSet: any): EndpointConfig {
        return new EndpointConfig({
            Id: crypto.randomUUID(),
            StatusCode: statusCodeToSet,
            BodyToSend: bodyToSet,
        });
    }
}