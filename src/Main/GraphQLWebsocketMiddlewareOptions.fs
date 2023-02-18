namespace GraphQLTransportWS
open FSharp.Data.GraphQL
open System
open System.Text.Json
open System.Threading.Tasks

type PingHandler =
  IServiceProvider -> JsonDocument option -> Task<JsonDocument option>

type GraphQLWebsocketMiddlewareOptions<'Root> =
 { SchemaExecutor: Executor<'Root>
   RootFactory: unit -> 'Root
   EndpointUrl: string
   SerializerOptions : JsonSerializerOptions
   ConnectionInitTimeoutInMs: int
   CustomPingHandler : PingHandler option
 }