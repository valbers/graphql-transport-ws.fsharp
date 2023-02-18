namespace GraphQLTransportWS

open System.Runtime.CompilerServices
open Microsoft.Extensions.DependencyInjection
open FSharp.Data.GraphQL
open System.Text.Json

[<Extension>]
type ServiceCollectionExtensions() =

  static let createStandardOptions executor rootFactory endpointUrl =
    { SchemaExecutor = executor
      RootFactory = rootFactory
      EndpointUrl = endpointUrl
      SerializerOptions =
        JsonSerializerOptions()
        |> JsonConverterUtils.configureSerializer executor
      ConnectionInitTimeoutInMs = 3000
      CustomPingHandler = None }

  [<Extension>]
  static member AddGraphQLTransportWS<'Root>(this : IServiceCollection, executor : Executor<'Root>, rootFactory : unit -> 'Root, endpointUrl : string) =
    this.AddSingleton<GraphQLTransportWSOptions<'Root>>(createStandardOptions executor rootFactory endpointUrl)

  [<Extension>]
  static member AddGraphQLTransportWSWith<'Root>
    ( this : IServiceCollection,
      executor : Executor<'Root>,
      rootFactory : unit -> 'Root,
      endpointUrl : string,
      extraConfiguration : GraphQLTransportWSOptions<'Root> -> GraphQLTransportWSOptions<'Root>
    ) =
    this.AddSingleton<GraphQLTransportWSOptions<'Root>>(createStandardOptions executor rootFactory endpointUrl |> extraConfiguration)