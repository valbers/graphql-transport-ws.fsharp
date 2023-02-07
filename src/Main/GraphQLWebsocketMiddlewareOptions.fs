namespace GraphQLTransportWS
open FSharp.Data.GraphQL

type GraphQLWebsocketMiddlewareOptions<'Root> =
 { SchemaExecutor: Executor<'Root>
   RootFactory: unit -> 'Root
   EndpointUrl: string
 }