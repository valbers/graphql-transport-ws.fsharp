namespace GraphQLTransportWS

open FSharp.Data.GraphQL.Execution
open FSharp.Data.GraphQL.Types
open System.Text.Json
open System.Collections.Generic

type RawSubscribePayload =
    { OperationName : string option
      Query : string option
      Variables : JsonDocument option
      Extensions : string option }

type RawMessage =
    { Id : string option
      Type : string option
      Payload : JsonDocument option }

type ServerRawPayload =
    | ServerStringPayload of string
    | ExecutionResult of Output
    | ErrorMessages of NameValueLookup list

type RawServerMessage =
    { Id : string option
      Type : string
      Payload : ServerRawPayload option }

type GraphQLQuery =
    { ExecutionPlan : ExecutionPlan
      Variables : Map<string, obj> }

type ClientMessage =
    | ConnectionInit of payload: JsonDocument option
    | ClientPing of payload: JsonDocument option
    | ClientPong of payload: JsonDocument option
    | Subscribe of id: string * query: GraphQLQuery
    | ClientComplete of id: string

type ClientMessageProtocolFailure =
    | InvalidMessage of code: int * explanation: string

type ServerMessage =
    | ConnectionAck
    | ServerPing
    | ServerPong
    | Next of id : string * payload : Output
    | Error of id : string * err : NameValueLookup list
    | Complete of id : string

module CustomWebSocketStatus =
    let invalidMessage = 4400
    let unauthorized = 4401
    let connectionTimeout = 4408
    let subscriberAlreadyExists = 4409
    let tooManyInitializationRequests = 4429