namespace star_wars_api_server

open Giraffe
open Microsoft.AspNetCore.Http
open FSharp.Data.GraphQL.Execution
open FSharp.Data.GraphQL
open FSharp.Data.GraphQL.Types
open System.Text.Json
open System.Threading

type HttpHandler = HttpFunc -> HttpContext -> HttpFuncResult

module HttpHandlers =
    open Microsoft.Extensions.Logging
    open GraphQLTransportWS
    let private httpOk (cancellationToken : CancellationToken) (serializerOptions : JsonSerializerOptions) payload : HttpHandler =
        setStatusCode 200
        >=> (setHttpHeader "Content-Type" "application/json")
        >=> (fun _ ctx ->
                JsonSerializer
                    .SerializeAsync(
                        ctx.Response.Body,
                        payload,
                        options = serializerOptions,
                        cancellationToken = cancellationToken
                    )
                    .ContinueWith(fun _ -> Some ctx) // what about when serialization fails? Maybe it will never at this stage anyway...
            )

    let private prepareGenericErrors (errorMessages : string list) =
        (NameValueLookup.ofList
            [ "errors",
                upcast
                    ( errorMessages
                        |> List.map
                            (fun msg ->
                                NameValueLookup.ofList ["message", upcast msg]
                            )
                    )
            ]
        )

    let handleGraphQL
        (cancellationToken : CancellationToken)
        (logger : ILogger)
        (executor : Executor<'Root>)
        (next : HttpFunc) (ctx : HttpContext) =
        task {
            let cancellationToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ctx.RequestAborted).Token
            if cancellationToken.IsCancellationRequested then
                return (fun _ -> None) ctx
            else
                let options = (ctx.GetService<GraphQLTransportWS.GraphQLWebsocketMiddlewareOptions<'Root>>())
                let serializerOptions = options.SerializerOptions
                let! graphqlRequest =
                    JsonSerializer.DeserializeAsync<GraphQLTransportWS.GraphQLRequest>(
                        ctx.Request.Body,
                        serializerOptions
                    ).AsTask()

                let applyPlanExecutionResult (result : GQLResponse) =
                    task {
                        match result with
                        | Direct (data, _) ->
                            return! httpOk cancellationToken serializerOptions data next ctx
                        | other ->
                            let error =
                                prepareGenericErrors ["subscriptions are not supported here (use the websocket endpoint instead)."]
                            return! httpOk cancellationToken serializerOptions error next ctx
                    }

                match graphqlRequest.Query with
                | None ->
                    let! result = Schema.executor.AsyncExecute (Introspection.IntrospectionQuery) |> Async.StartAsTask
                    if logger.IsEnabled(LogLevel.Debug) then
                        logger.LogDebug(sprintf "Result metadata: %A" result.Metadata)
                    else
                        ()
                    return! result |> applyPlanExecutionResult
                | Some queryAsStr ->
                    let graphQLQueryDecodingResult =
                        queryAsStr
                        |> GraphQLQueryDecoding.decodeGraphQLQuery
                            serializerOptions
                            executor
                            graphqlRequest.OperationName
                            graphqlRequest.Variables
                    match graphQLQueryDecodingResult with
                    | Rop.Failure errMsgs ->
                        return!
                            httpOk cancellationToken serializerOptions (prepareGenericErrors errMsgs) next ctx
                    | Rop.Success (query, _) ->
                        if logger.IsEnabled(LogLevel.Debug) then
                            logger.LogDebug(sprintf "Received query: %A" query)
                        else
                            ()
                        let root = { RequestId = System.Guid.NewGuid().ToString () }
                        let! result =
                            Schema.executor.AsyncExecute(
                                query.ExecutionPlan,
                                data = root,
                                variables = query.Variables
                            )|> Async.StartAsTask
                        if logger.IsEnabled(LogLevel.Debug) then
                            logger.LogDebug(sprintf "Result metadata: %A" result.Metadata)
                        else
                            ()
                        return! result |> applyPlanExecutionResult
        }
