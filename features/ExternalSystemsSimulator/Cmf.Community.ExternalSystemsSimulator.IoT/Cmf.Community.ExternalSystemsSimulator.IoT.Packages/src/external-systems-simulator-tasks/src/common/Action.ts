import { ActionStates } from "./ActionStates";

export class Action {
    Id: string|undefined;
    ActionType: string;
    ActionState: ActionStates;
    ActionParameter: any;
    DependsOn: string[] | undefined;

     constructor(init?: Partial<Action>) {
        if(!init || !init.ActionType ||!init.ActionParameter){
            throw new Error("Invalid input. Recieved: " + JSON.stringify(init));
        }
        this.Id = init.Id ?? crypto.randomUUID();
        this.ActionType = init.ActionType;
        this.ActionState = init.ActionState ?? ActionStates.Created;
        this.ActionParameter = init.ActionParameter;
    }
}