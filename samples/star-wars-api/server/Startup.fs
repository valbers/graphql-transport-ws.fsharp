namespace star_wars_api_server

open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Giraffe
open Microsoft.Extensions.Logging
open System
open Microsoft.AspNetCore.Server.Kestrel.Core
open Microsoft.Extensions.Hosting
open System.Text.Json

type Startup private () =
    let graphqlEndpointUrl = "/graphql"

    new (configuration: IConfiguration) as this =
        Startup() then
        this.Configuration <- configuration

    member _.ConfigureServices(services: IServiceCollection) =
        services.AddGiraffe()
                .Configure(Action<KestrelServerOptions>(fun x -> x.AllowSynchronousIO <- true))
                .Configure(Action<IISServerOptions>(fun x -> x.AllowSynchronousIO <- true))
        |> ignore

        services.AddSingleton(Schema.executor) |> ignore

        services.AddSingleton<GraphQLTransportWS.GraphQLWebsocketMiddlewareOptions<Root>>(implementationInstance =
             {
                SchemaExecutor = Schema.executor
                RootFactory = fun () -> { RequestId = Guid.NewGuid().ToString() }
                EndpointUrl = graphqlEndpointUrl
                ConnectionInitTimeoutInMs = 3000
                CustomPingHandler =
                    Some (fun _ payload -> task {
                            printfn "custom ping handling..."
                            return
                                Some <|
                                JsonSerializer.SerializeToDocument(
                                    {| MyRandomCustomMessage = "asdf"
                                       OriginalMessage = payload
                                    |}
                                )
                         })
             }
        )
        |> ignore

    member _.Configure(app: IApplicationBuilder, applicationLifetime : IHostApplicationLifetime) =
        let errorHandler (ex : Exception) (log : ILogger) =
            log.LogError(EventId(), ex, "An unhandled exception has occurred while executing the request.")
            clearResponse >=> setStatusCode 500
        app
            .UsePathBase(graphqlEndpointUrl)
            .UseGiraffeErrorHandler(errorHandler)
            .UseWebSockets()
            .UseMiddleware<GraphQLTransportWS.GraphQLWebSocketMiddleware<Root>>()
            .UseGiraffe (HttpHandlers.webApp applicationLifetime.ApplicationStopping)

    member val Configuration : IConfiguration = null with get, set
