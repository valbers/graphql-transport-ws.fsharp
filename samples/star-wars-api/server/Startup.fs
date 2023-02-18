namespace star_wars_api_server

open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Giraffe
open Microsoft.Extensions.Logging
open System
open Microsoft.AspNetCore.Server.Kestrel.Core
open Microsoft.Extensions.Hosting
open GraphQLTransportWS

type Startup private () =
    let graphqlEndpointUrl = "/graphql"

    let setCorsHeaders : HttpHandler =
        setHttpHeader "Access-Control-Allow-Origin" "*"
        >=> setHttpHeader "Access-Control-Allow-Headers" "content-type"

    new (configuration: IConfiguration) as this =
        Startup() then
        this.Configuration <- configuration

    member _.ConfigureServices(services: IServiceCollection) =
        services.AddGiraffe()
                .Configure(Action<KestrelServerOptions>(fun x -> x.AllowSynchronousIO <- true))
                .Configure(Action<IISServerOptions>(fun x -> x.AllowSynchronousIO <- true))
        |> ignore

        services.AddSingleton(Schema.executor) |> ignore

        services.AddGraphQLTransportWS<Root>(
            executor = Schema.executor,
            rootFactory = (fun () -> { RequestId = Guid.NewGuid().ToString() }),
            endpointUrl = graphqlEndpointUrl)
        |> ignore

    member _.Configure(app: IApplicationBuilder, applicationLifetime : IHostApplicationLifetime, loggerFactory : ILoggerFactory) =
        let errorHandler (ex : Exception) (log : ILogger) =
            log.LogError(EventId(), ex, "An unhandled exception has occurred while executing the request.")
            clearResponse >=> setStatusCode 500
        app
            .UsePathBase(graphqlEndpointUrl)
            .UseGiraffeErrorHandler(errorHandler)
            .UseWebSockets()
            .UseMiddleware<GraphQLWebSocketMiddleware<Root>>()
            .UseGiraffe
                ( setCorsHeaders
                  >=> HttpHandlers.handleGraphQL
                            applicationLifetime.ApplicationStopping
                            (loggerFactory.CreateLogger("HttpHandlers.handlerGraphQL"))
                            Schema.executor
                )

    member val Configuration : IConfiguration = null with get, set
