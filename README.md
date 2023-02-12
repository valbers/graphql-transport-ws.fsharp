# graphql-transport-ws.fsharp

An FSharp implementation of the [graphql-transport-ws subprotocol](https://github.com/enisdenjo/graphql-ws/blob/master/PROTOCOL.md)
for use with [FSharp.Data.GraphQL](https://github.com/fsprojects/FSharp.Data.GraphQL).
This repository is not an affiliate of FSharp.Data.GraphQL.

## Usage

### Server

In a `Startup` class...
```fsharp
namespace MyApp

open Giraffe
open Microsoft.AspNetCore.Server.Kestrel.Core
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open System
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

        // STEP 1: Setting the options
        services.AddSingleton<GraphQLTransportWS.GraphQLWebsocketMiddlewareOptions<Root>>(implementationInstance =
             {
                SchemaExecutor = Schema.executor // --> `Schema.executor` you define somewhere else as usual with FSharp.Data.GraphQL -- check out the "samples/" folder for an example
                RootFactory = fun () -> { RequestId = Guid.NewGuid().ToString() }
                EndpointUrl = graphqlEndpointUrl
                ConnectionInitTimeoutInMs = 3000
                CustomPingHandler = None
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
            .UseMiddleware<GraphQLTransportWS.GraphQLWebSocketMiddleware<Root>>() // STEP 2: using the middleware
            .UseGiraffe (HttpHandlers.webApp applicationLifetime.ApplicationStopping) // --> `HttpHandlers.webApp` you define somewhere else as usual with Giraffe (and FSharp.Data.GraphQL) -- check out the "samples/" folder for an example

    member val Configuration : IConfiguration = null with get, set

```

In your schema, you'll want to define a subscription, like in (example taken from the star-wars-api sample in the "samples/" folder):

```fsharp
    let Subscription =
        Define.SubscriptionObject<Root>(
            name = "Subscription",
            fields = [
                Define.SubscriptionField(
                    "watchMoon",
                    RootType,
                    PlanetType,
                    "Watches to see if a planet is a moon.",
                    [ Define.Input("id", String) ],
                    (fun ctx _ p -> if ctx.Arg("id") = p.Id then Some p else None)) ])
```

Don't forget to notify subscribers about new values:

```fsharp
    let Mutation =
        Define.Object<Root>(
            name = "Mutation",
            fields = [
                Define.Field(
                    "setMoon",
                    Nullable PlanetType,
                    "Defines if a planet is actually a moon or not.",
                    [ Define.Input("id", String); Define.Input("isMoon", Boolean) ],
                    fun ctx _ ->
                        getPlanet (ctx.Arg("id"))
                        |> Option.map (fun x ->
                            x.SetMoon(Some(ctx.Arg("isMoon"))) |> ignore
                            schemaConfig.SubscriptionProvider.Publish<Planet> "watchMoon" x // here you notify the subscribers upon a mutation
                            x))])
```

Finally run the server (e.g. make it listen at `localhost:8086`).

### Client
Using your favorite (or not :)) client library (e.g.: [Apollo Client](https://www.apollographql.com/docs/react/get-started), [Relay](https://relay.dev), [Strawberry Shake](https://chillicream.com/docs/strawberryshake/v13), [elm-graphql](https://github.com/dillonkearns/elm-graphql) ❤️), just point to `localhost:8086/graphql` (as per the example above) and, as long as the client implements the `graphql-transport-ws` subprotocol, subscriptions should work.
