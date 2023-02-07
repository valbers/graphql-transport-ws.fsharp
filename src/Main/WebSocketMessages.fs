namespace GraphQLTransportWS

open FSharp.Data.GraphQL.Execution
open FSharp.Data.GraphQL.Types

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
    | Error of id : string option * err : string
    | Complete of id : string