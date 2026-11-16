import { EndpointConfig } from "./EndpointConfig";

export class EndpointStatistic {
    RequestId : string | undefined;
    EndpointConfig: EndpointConfig|undefined;
    ReceivedBody: any;
    ReplySent: any;
    StatusCodeSent: number|undefined;
    Error: any
}