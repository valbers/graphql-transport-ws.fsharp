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
open GraphQLTransportWS.Giraffe

type Startup private () =
    let graphqlEndpointUrl = "/ws"

    let setCorsHeaders : HttpHandler =
        setHttpHeader "Access-Control-Allow-Origin" "*"
        >=> setHttpHeader "Access-Control-Allow-Headers" "content-type"

    let rootFactory () =
        { RequestId = Guid.NewGuid().ToString() }

    new (configuration: IConfiguration) as this =
        Startup() then
        this.Configuration <- configuration

    member _.ConfigureServices(services: IServiceCollection) =
        services.AddGiraffe()
                .Configure(Action<KestrelServerOptions>(fun x -> x.AllowSynchronousIO <- true))
                .AddGraphQLTransportWS<Root>(
                    Schema.executor,
                    rootFactory,
                    "/ws"
                )
        |> ignore

    member _.Configure(app: IApplicationBuilder, applicationLifetime : IHostApplicationLifetime, loggerFactory : ILoggerFactory) =
        let errorHandler (ex : Exception) (log : ILogger) =
            log.LogError(EventId(), ex, "An unhandled exception has occurred while executing the request.")
            clearResponse >=> setStatusCode 500
        app
            .UseGiraffeErrorHandler(errorHandler)
            .UseWebSockets()
            .UseWebSocketsForGraphQL<Root>()
            .UseGiraffe
                ( setCorsHeaders
                  >=> (HttpHandlers.handleGraphQL<Root>
                            applicationLifetime.ApplicationStopping
                            (loggerFactory.CreateLogger("HttpHandlers.handlerGraphQL")))
                )

    member val Configuration : IConfiguration = null with get, set
