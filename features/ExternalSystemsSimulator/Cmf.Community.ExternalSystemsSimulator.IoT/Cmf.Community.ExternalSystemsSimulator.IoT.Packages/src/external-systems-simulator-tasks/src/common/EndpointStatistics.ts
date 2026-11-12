import { EndpointConfig } from "./EndpointConfig";

export class EndpointStatistic {
    RequestId : string | undefined;
    EndpointConfig: EndpointConfig|undefined;
    RecievedBody: any;
    ReplySent: any;
    StatusCodeSent: number|undefined;
    Error: any
}