namespace star_wars_api_server

open Giraffe
open Microsoft.AspNetCore.Http
open FSharp.Data.GraphQL.Execution
open FSharp.Data.GraphQL
open FSharp.Data.GraphQL.Types
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks

type HttpHandler = HttpFunc -> HttpContext -> HttpFuncResult

module HttpHandlers =
    let private jsonSerializerOptions = new JsonSerializerOptions()

    let private serialize cancellationToken obj =
        task {
            let memoryStream = new MemoryStream()
            let! _ =
                JsonSerializer
                    .SerializeAsync(memoryStream, obj, options = jsonSerializerOptions, cancellationToken = cancellationToken)
                    .ContinueWith(fun _ -> System.String.Empty)
            let streamReader = new StreamReader(memoryStream, Encoding.UTF8)
            memoryStream.Seek(0L, SeekOrigin.Begin) |> ignore
            return! streamReader.ReadToEndAsync(cancellationToken)
        }

    let private serializeResult cToken result =
        task {
            match result with
            | Direct (data, _) ->
                return! data
                    |> serialize cToken
            | Deferred (data, _, deferred) ->
                deferred
                |> Observable.add (fun d -> printfn "Deferred: %A" d)
                return! data |> serialize cToken
            | Stream data ->
                data |> Observable.add (fun d -> printfn "Subscription data: %A" d)
                return! Task.FromResult("{}")
        }

    let private deserialize cancellationToken (str : string) : Task<Map<string, obj>> =
        let rec deserializeToAMap (jsonLocalRootNode : JsonNode) : Map<string, obj> =
            let rec extractValue (jsonPropName : string) (jsonNode : JsonNode) =
                match jsonPropName, jsonNode with
                | "variables", (:? JsonObject as jObj) ->
                    jObj
                    |> deserializeToAMap
                    |> box
                | name, (:? JsonArray as jArray) ->
                    jArray
                    |> Seq.map
                        (fun (jNode : JsonNode) ->
                            extractValue name jNode
                        )
                    |> Array.ofSeq
                    |> box
                | _, (:? JsonValue as jValue) ->
                    jValue.ToString()
                    |> box
                | _ ->
                    failwithf "at property \"%s\": this JsonNode concrete type is not supported" jsonPropName

            jsonLocalRootNode.AsObject()
            |> Seq.map
                (fun keyValuePair ->
                    let jPropName, jNode = keyValuePair.Key, keyValuePair.Value
                    (jPropName, jNode |> extractValue jPropName)
                )
            |> Map.ofSeq

        task {
            if System.String.IsNullOrWhiteSpace (str)
            then
                return
                    Map.empty
            else
                let mutable jsonNodeOptions = new JsonNodeOptions()
                jsonNodeOptions.PropertyNameCaseInsensitive <- true
                let result = JsonNode.Parse(str, nodeOptions = jsonNodeOptions)
                return
                    result
                    |> deserializeToAMap
        }

    let private removeWhitespacesAndLineBreaks (str : string) = str.Trim().Replace ("\r\n", " ")

    let private readStream (cToken : CancellationToken) (s : Stream) =
        task {
            use ms = new MemoryStream (4096)
            let! _ = s.CopyToAsync(ms, cToken).ContinueWith(fun _ -> Array.empty<byte>)
            return ms.ToArray()
        }

    let setContentTypeAsJson : HttpHandler = setHttpHeader "Content-Type" "application/json"

    let internalServerError : HttpHandler = setStatusCode 500

    let okWithStr str : HttpHandler = setStatusCode 200 >=> setContentTypeAsJson >=> setBodyFromString str

    let setCorsHeaders : HttpHandler =
        setHttpHeader "Access-Control-Allow-Origin" "*"
        >=> setHttpHeader "Access-Control-Allow-Headers" "content-type"

    let private graphQL (cancellationToken : CancellationToken) (next : HttpFunc) (ctx : HttpContext) =
        task {
            let cancellationToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ctx.RequestAborted).Token

            let! streamData =
                ctx.Request.Body
                |> readStream cancellationToken

            let! data =
                streamData
                |> Encoding.UTF8.GetString
                |> deserialize cancellationToken

            let query =
                if data.ContainsKey("query")
                then
                    match data.["query"] with
                    | :? string as x ->
                        Some x
                    | _ ->
                        failwith "Failure deserializing response. Could not read query - it is not stringified in request"
                else
                    None

            let! variables =
                if data.ContainsKey("variables")
                then
                    match data.["variables"] with
                    | null ->
                        Map.empty
                        |> Task.FromResult
                    | :? string as x ->
                        x 
                        |> deserialize cancellationToken
                    | :? Map<string, obj> as x ->
                        x
                        |> Task.FromResult
                    | _ ->
                        failwith "Failure deserializing response. Could not read variables - it is not an object in the request."
                else
                    Map.empty
                    |> Task.FromResult

            match query, variables with
            | Some query, variables when variables.IsEmpty ->
                printfn "Received query: %s" query
                let query = query |> removeWhitespacesAndLineBreaks
                let! result = Schema.executor.AsyncExecute (query) |> Async.StartAsTask
                printfn "Result metadata: %A" result.Metadata
                let! resultJson = result |> serializeResult cancellationToken
                return! okWithStr resultJson next ctx
            | Some query, variables ->
                printfn "Received query: %s" query
                printfn "Received variables: %A" variables
                let query = query |> removeWhitespacesAndLineBreaks
                let root = { RequestId = System.Guid.NewGuid().ToString () }
                let! result = Schema.executor.AsyncExecute (query, data = root, variables = variables) |> Async.StartAsTask
                printfn "Result metadata: %A" result.Metadata
                let! resultJson = result |> serializeResult cancellationToken
                return! okWithStr resultJson next ctx
            | None, _ ->
                let! result = Schema.executor.AsyncExecute (Introspection.IntrospectionQuery) |> Async.StartAsTask
                printfn "Result metadata: %A" result.Metadata
                let! resultJson = result |> serializeResult cancellationToken
                return! okWithStr resultJson next ctx
        }

    let webApp (cancellationToken : CancellationToken) : HttpHandler = setCorsHeaders >=> graphQL cancellationToken
