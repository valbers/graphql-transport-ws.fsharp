namespace GraphQLTransportWS

open FSharp.Data.GraphQL.Execution
open FSharp.Data.GraphQL.Types

type RawSubscribePayload =
    { OperationName : string option
      Query : string option
      Variables : string option
      Extensions : string option }

type RawPayload =
    | StringPayload of string
    | SubscribePayload of RawSubscribePayload

type RawMessage =
    { Id : string option
      Type : string option
      Payload : RawPayload option }

type GraphQLQuery =
    { ExecutionPlan : ExecutionPlan
      Variables : Map<string, obj> }

type ClientMessage =
    | ConnectionInit of payload: string option
    | ClientPing of payload: string option
    | ClientPong of payload: string option
    | Subscribe of id: string * query: GraphQLQuery
    | ClientComplete of id: string

type ClientMessageProtocolFailure =
    | InvalidMessage of code: int * explanation: string

type ServerMessage =
    | ConnectionAck
    | ServerPing of payload: string option
    | ServerPong of payload: string option
    | Next of id : string * payload : Output
    | Error of id : string * err : string list
    | Complete of id : string

module CustomWebSocketStatus =
    let invalidMessage = 4400