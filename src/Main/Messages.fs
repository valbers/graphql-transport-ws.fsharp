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

type ServerRawPayload =
    | ServerStringPayload of string
    | ExecutionResult of Output
    | ErrorMessages of string list

type RawServerMessage =
    { Id : string option
      Type : string option
      Payload : ServerRawPayload option }

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
    | ServerPing
    | ServerPong
    | Next of id : string * payload : Output
    | Error of id : string * err : string list
    | Complete of id : string

module CustomWebSocketStatus =
    let invalidMessage = 4400
    let unauthorized = 4401
    let subscriberAlreadyExists = 4409
    let tooManyInitializationRequests = 4429