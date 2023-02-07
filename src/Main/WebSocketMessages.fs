namespace GraphQLTransportWS

open FSharp.Data.GraphQL.Execution
open FSharp.Data.GraphQL.Types

type GraphQLWsMessageSubscribePayloadRaw =
    { OperationName : string option
      Query : string option
      Variables : string option
      Extensions : string option }

type GraphQLWsMessagePayloadRaw =
    | StringPayload of string
    | SubscribePayload of GraphQLWsMessageSubscribePayloadRaw

type GraphQLWsMessageRaw =
    { Id : string option
      Type : string option
      Payload : GraphQLWsMessagePayloadRaw option }

type GraphQLQuery =
    { ExecutionPlan : ExecutionPlan
      Variables : Map<string, obj> }

type WebSocketClientMessage =
    | ConnectionInit of payload: string option
    | ClientPing of payload: string option
    | ClientPong of payload: string option
    | Subscribe of id: string * query: GraphQLQuery
    | ClientComplete of id: string
    | InvalidMessage of explanation: string

type WebSocketServerMessage =
    | ConnectionAck
    | ServerPing of payload: string option
    | ServerPong of payload: string option
    | Next of id : string * payload : Output
    | Error of id : string * err : string list
    | Complete of id : string

module CustomWebSocketStatus =
    let invalidMessage = 4400