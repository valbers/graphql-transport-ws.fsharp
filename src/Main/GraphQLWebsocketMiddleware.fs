namespace GraphQLTransportWS

open FSharp.Data.GraphQL
open Microsoft.AspNetCore.Http
open GraphQLTransportWS.Rop
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Net.WebSockets
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FSharp.Data.GraphQL.Execution
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Options
open Microsoft.AspNetCore.Http.Json

module internal GraphQLSubscriptionsManagement =
  let subscriptions =
    ConcurrentDictionary<string, IDisposable * (string -> unit)>()
      :> IDictionary<string, IDisposable * (string -> unit)>

  let addSubscription (id : string, unsubscriber : IDisposable, onUnsubscribe : string -> unit) =
    printfn "GraphQLSubscriptionsManagement: new subscription (id: \"%s\". Total: %d)" (id |> string) subscriptions.Count
    subscriptions.Add(id, (unsubscriber, onUnsubscribe))

  let executeOnUnsubscribeAndDispose (id : string) (subscription : IDisposable * (string -> unit)) =
      let (unsubscriber, onUnsubscribe) = subscription
      id |> onUnsubscribe
      unsubscriber.Dispose()

  let removeSubscription (id: string) =
    if subscriptions.ContainsKey(id) then
      subscriptions.[id]
      |> executeOnUnsubscribeAndDispose id
      subscriptions.Remove(id) |> ignore

  let removeAllSubscriptions() =
    subscriptions
    |> Seq.iter
        (fun subscription ->
            subscription.Value
            |> executeOnUnsubscribeAndDispose subscription.Key
        )
    subscriptions.Clear()

type GraphQLWebSocketMiddleware<'Root>(next : RequestDelegate, applicationLifetime : IHostApplicationLifetime, jsonOptions : IOptions<JsonOptions>, options : GraphQLWebsocketMiddlewareOptions<'Root>) =

  let serializeServerMessage (jsonOptions: JsonOptions) serverMessage =
      task { return "dummySerializedServerMessage" }

  let deserializeClientMessage (jsonOptions: JsonOptions) (msg: string) =
    task { return ConnectionInit None }

  let receiveMessageViaSocket (cancellationToken : CancellationToken) (jsonOptions: JsonOptions) (executor : Executor<'Root>) (replacements : Map<string, obj>) (socket : WebSocket) =
    task {
      let buffer = Array.zeroCreate 4096
      let completeMessage = new List<byte>()
      let mutable segmentResponse : WebSocketReceiveResult = null
      while (segmentResponse = null) || (not segmentResponse.EndOfMessage) do
        let! r = socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
        segmentResponse <- r
        completeMessage.AddRange(new ArraySegment<byte>(buffer, 0, r.Count))

      let message =
        completeMessage
        |> Seq.filter (fun x -> x > 0uy)
        |> Array.ofSeq
        |> System.Text.Encoding.UTF8.GetString
      if String.IsNullOrWhiteSpace message then
        return None
      else
        let! deserializedMsg = deserializeClientMessage jsonOptions message
        return Some deserializedMsg
    }

  let sendMessageViaSocket (cancellationToken : CancellationToken) (jsonOptions: JsonOptions) (socket : WebSocket) (message : WebSocketServerMessage) =
    task {
      if not (socket.State = WebSocketState.Open) then
        printfn "ignoring message to be sent via socket, since its state is not 'Open', but '%A'" socket.State
      else
        let! serializedMessage = message |> serializeServerMessage jsonOptions
        let segment =
          new ArraySegment<byte>(
            System.Text.Encoding.UTF8.GetBytes(serializedMessage)
          )
        do! socket.SendAsync(segment, WebSocketMessageType.Text, endOfMessage = true, cancellationToken = cancellationToken)
        printfn "<- %A" message
    }

  let addClientSubscription (cancellationToken : CancellationToken) (id : string) (howToSendDataOnNext: string -> Output -> Task<unit>) (streamSource: IObservable<Output>)  =
    task {
      let unsubscribing = new Task ((fun () -> ()), cancellationToken)
      let onUnsubscribe _ = if not (unsubscribing.IsCanceled || unsubscribing.IsCompleted || unsubscribing.IsFaulted) then unsubscribing.Start()
      let unsubscriber =
          streamSource
          |> Observable.subscribe
              (fun theOutput ->
                  let task = theOutput |> howToSendDataOnNext id
                  task.Wait()
              )
      GraphQLSubscriptionsManagement.addSubscription(id, unsubscriber, onUnsubscribe)
      return! unsubscribing
  }

  let handleMessages (cancellationToken: CancellationToken) (jsonOptions: JsonOptions) (executor : Executor<'Root>) (root: unit -> 'Root) (socket : WebSocket) =
    // ---------->
    // Helpers -->
    // ---------->
    let safe_SendMessageViaSocket = sendMessageViaSocket cancellationToken
    let safe_ReceiveMessageViaSocket = receiveMessageViaSocket cancellationToken
    let safe_AddClientSubscription = addClientSubscription cancellationToken

    let safe_Send = safe_SendMessageViaSocket jsonOptions socket
    let safe_Receive() =
      socket
      |> safe_ReceiveMessageViaSocket jsonOptions executor Map.empty

    let safe_SendQueryOutput id output =
      task { do! safe_Send (Next (id, output)) }
    let sendQueryOutputDelayedBy (cancToken: CancellationToken) (ms: int) id output =
      task {
            do! Async.StartAsTask(Async.Sleep ms, cancellationToken = cancToken)
            do! output
                |> safe_SendQueryOutput id
        }
    let safe_SendQueryOutputDelayedBy = sendQueryOutputDelayedBy cancellationToken

    let safe_ApplyPlanExecutionResult (id: string) (executionResult: GQLResponse)  =
      task {
            match executionResult with
            | Stream observableOutput ->
                do! observableOutput
                    |> safe_AddClientSubscription id safe_SendQueryOutput
            | Deferred (data, _, observableOutput) ->
                do! data
                    |> safe_SendQueryOutput id
                do! observableOutput
                    |> safe_AddClientSubscription id (safe_SendQueryOutputDelayedBy 5000)
            | Direct (data, _) ->
                do! data
                    |> safe_SendQueryOutput id
        }

    let getStrAddendumOfOptionalPayload optionalPayload =
        optionalPayload
        |> Option.map (fun payloadStr -> sprintf " with payload: %A" payloadStr)
        |> Option.defaultWith (fun () -> "")

    let logMsgReceivedWithOptionalPayload optionalPayload msgAsStr =
        printfn "%s%s" msgAsStr (optionalPayload |> getStrAddendumOfOptionalPayload)

    let logMsgWithIdReceived id msgAsStr =
        printfn "%s (id: %s)" msgAsStr id

    let mutable socketClosed = false
    // <--------------
    // <-- Helpers --|
    // <--------------

    // ------->
    // Main -->
    // ------->
    task {
      try
          while not cancellationToken.IsCancellationRequested && not socketClosed do
              let! receivedMessage = safe_Receive()
              match receivedMessage with
              | None ->
                  printfn "Warn: websocket socket received empty message!"
              | Some msg ->
                  match msg with
                  | ConnectionInit p ->
                      "ConnectionInit" |> logMsgReceivedWithOptionalPayload p
                      do! ConnectionAck |> safe_Send
                  | ClientPing p ->
                      "ClientPing" |> logMsgReceivedWithOptionalPayload p
                      do! ServerPong None |> safe_Send
                  | ClientPong p ->
                      "ClientPong" |> logMsgReceivedWithOptionalPayload p
                  | Subscribe (id, query) ->
                      "Subscribe" |> logMsgWithIdReceived id
                      let! planExecutionResult =
                          Async.StartAsTask (
                              executor.AsyncExecute(query.ExecutionPlan, root(), query.Variables),
                              cancellationToken = cancellationToken
                          )
                      do! planExecutionResult
                          |> safe_ApplyPlanExecutionResult id
                  | ClientComplete id ->
                      "ClientComplete" |> logMsgWithIdReceived id
                      id |> GraphQLSubscriptionsManagement.removeSubscription
                      do! Complete id |> safe_Send
                  | InvalidMessage explanation ->
                      "InvalidMessage" |> logMsgReceivedWithOptionalPayload None
                      do! socket.CloseAsync(enum CustomWebSocketStatus.invalidMessage, explanation, cancellationToken)
                      socketClosed <- true
      with // TODO: MAKE A PROPER GRAPHQL ERROR HANDLING!
      | ex ->
        printfn "Unexpected exception \"%s\" in GraphQLWebsocketMiddleware (handleMessages). More:\n%s" (ex.GetType().Name) (ex.ToString())
    }

  member __.InvokeAsync(ctx : HttpContext) =
    task {
      if false && not (ctx.Request.Path = PathString (options.EndpointUrl)) then
        do! next.Invoke(ctx)
      else
        if ctx.WebSockets.IsWebSocketRequest then
          use! socket = ctx.WebSockets.AcceptWebSocketAsync("graphql-transport-ws")
          let cancellationToken =
            (CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, applicationLifetime.ApplicationStopping).Token)
          let safe_HandleMessages = handleMessages cancellationToken
          try
            do! socket
                |> safe_HandleMessages jsonOptions.Value options.SchemaExecutor options.RootFactory
          with
            | ex ->
              printfn "Unexpected exception \"%s\" in GraphQLWebsocketMiddleware. More:\n%s" (ex.GetType().Name) (ex.ToString())
        else
          do! next.Invoke(ctx)
    }