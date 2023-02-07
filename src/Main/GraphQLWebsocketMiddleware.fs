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

  let serializeServerMessage (jsonOptions: JsonOptions) (serverMessage : ServerMessage) =
      task {
        let raw =
          match serverMessage with
          | ConnectionAck ->
            { Id = None
              Type = Some "connection_ack"
              Payload = None }
          | ServerPing ->
            { Id = None
              Type = Some "ping"
              Payload = None }
          | ServerPong ->
            { Id = None
              Type = Some "pong"
              Payload = None }
          | Next (id, payload) ->
            { Id = Some id
              Type = Some "next"
              Payload = Some <| ExecutionResult payload }
          | Complete id ->
            { Id = Some id
              Type = Some "complete"
              Payload = None }
          | Error (id, errMsgs) ->
            { Id = Some id
              Type = Some "error"
              Payload = Some <| ErrorMessages errMsgs }
        return JsonSerializer.Serialize(raw, jsonOptions.SerializerOptions)
      }

  let deserializeClientMessage (jsonOptions: JsonOptions) (executor : Executor<'Root>) (msg: string) =
    task {
      return
        JsonSerializer.Deserialize<RawMessage>(msg, jsonOptions.SerializerOptions)
        |> MessageMapping.toClientMessage executor
    }

  let isSocketOpen (theSocket : WebSocket) =
    not (theSocket.State = WebSocketState.Aborted) &&
    not (theSocket.State = WebSocketState.Closed) &&
    not (theSocket.State = WebSocketState.CloseReceived)

  let canCloseSocket (theSocket : WebSocket) =
    not (theSocket.State = WebSocketState.Aborted) &&
    not (theSocket.State = WebSocketState.Closed)

  let receiveMessageViaSocket (cancellationToken : CancellationToken) (jsonOptions: JsonOptions) (executor : Executor<'Root>) (replacements : Map<string, obj>) (socket : WebSocket) : Task<RopResult<ClientMessage, ClientMessageProtocolFailure> option> =
    task {
      let buffer = Array.zeroCreate 4096
      let completeMessage = new List<byte>()
      let mutable segmentResponse : WebSocketReceiveResult = null
      while (not cancellationToken.IsCancellationRequested) &&
            socket |> isSocketOpen &&
            ((segmentResponse = null) || (not segmentResponse.EndOfMessage)) do
        try
          let! r = socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
          segmentResponse <- r
          completeMessage.AddRange(new ArraySegment<byte>(buffer, 0, r.Count))
        with :? OperationCanceledException ->
          ()

      let message =
        completeMessage
        |> Seq.filter (fun x -> x > 0uy)
        |> Array.ofSeq
        |> System.Text.Encoding.UTF8.GetString
      if String.IsNullOrWhiteSpace message then
        return None
      else
        try
          let! result =
            message
            |> deserializeClientMessage jsonOptions executor
          return Some result
        with :? JsonException as e ->
          printfn "%s" (e.ToString())
          return Some (MessageMapping.invalidMsg <| "invalid json in client message")
    }

  let sendMessageViaSocket (cancellationToken : CancellationToken) (jsonOptions: JsonOptions) (socket : WebSocket) (message : ServerMessage) =
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
    let safe_ReceiveMessageViaSocket = receiveMessageViaSocket (new CancellationToken())
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

    let tryToGracefullyCloseSocket theSocket =
      task {
        if theSocket |> canCloseSocket
        then
          do! theSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "NormalClosure", new CancellationToken())
        else
          ()
      }

    // <--------------
    // <-- Helpers --|
    // <--------------

    // ------->
    // Main -->
    // ------->
    task {
      try
        while not cancellationToken.IsCancellationRequested && socket |> isSocketOpen do
            let! receivedMessage = safe_Receive()
            match receivedMessage with
            | None ->
                printfn "Warn: websocket socket received empty message! (socket state = %A)" socket.State
            | Some msg ->
                match msg with
                | Failure failureMsgs ->
                    "InvalidMessage" |> logMsgReceivedWithOptionalPayload None
                    match failureMsgs |> List.head with
                    | InvalidMessage (code, explanation) ->
                      do! socket.CloseAsync(enum code, explanation, cancellationToken)
                | Success (ConnectionInit p, _) ->
                    "ConnectionInit" |> logMsgReceivedWithOptionalPayload p
                    do! socket.CloseAsync(
                      enum CustomWebSocketStatus.tooManyInitializationRequests,
                      "too many initialization requests",
                      cancellationToken)
                | Success (ClientPing p, _) ->
                    "ClientPing" |> logMsgReceivedWithOptionalPayload p
                    do! ServerPong |> safe_Send
                | Success (ClientPong p, _) ->
                    "ClientPong" |> logMsgReceivedWithOptionalPayload p
                | Success (Subscribe (id, query), _) ->
                    "Subscribe" |> logMsgWithIdReceived id
                    let! planExecutionResult =
                        Async.StartAsTask (
                            executor.AsyncExecute(query.ExecutionPlan, root(), query.Variables),
                            cancellationToken = cancellationToken
                        )
                    do! planExecutionResult
                        |> safe_ApplyPlanExecutionResult id
                | Success (ClientComplete id, _) ->
                    "ClientComplete" |> logMsgWithIdReceived id
                    id |> GraphQLSubscriptionsManagement.removeSubscription
                    do! Complete id |> safe_Send
        printfn "Leaving graphql-ws connection loop..."
        do! socket |> tryToGracefullyCloseSocket
      with
      | ex ->
        printfn "Unexpected exception \"%s\" in GraphQLWebsocketMiddleware (handleMessages). More:\n%s" (ex.GetType().Name) (ex.ToString())
        // at this point, only something really weird must have happened.
        // In order to avoid faulty state scenarios and unimagined damages,
        // just close the socket without further ado.
        do! socket |> tryToGracefullyCloseSocket
    }

  let waitForConnectionInit (jsonOptions : JsonOptions) (schemaExecutor : Executor<'Root>) (replacements : Map<string, obj>) (connectionInitTimeoutInMs : int) (socket : WebSocket) : Task<RopResult<unit, string>> =
    task {
      let timerTokenSource = new CancellationTokenSource()
      timerTokenSource.CancelAfter(connectionInitTimeoutInMs)
      let detonationRegistration = timerTokenSource.Token.Register(fun _ ->
        socket.CloseAsync(enum 4408, "ConnectionInit timeout", new CancellationToken())
        |> Async.AwaitTask
        |> Async.RunSynchronously
      )
      let! connectionInitSucceeded = Task.Run<bool>((fun _ ->
        task {
          printfn "Waiting for ConnectionInit..."
          let! receivedMessage = receiveMessageViaSocket (new CancellationToken()) jsonOptions schemaExecutor replacements socket
          match receivedMessage with
          | Some (Success (ConnectionInit payload, _)) ->
            do! ConnectionAck |> sendMessageViaSocket (new CancellationToken()) jsonOptions socket
            detonationRegistration.Unregister() |> ignore
            return true
          | Some (Failure [InvalidMessage (code, explanation)]) ->
            do! socket.CloseAsync(enum code, explanation, new CancellationToken())
            return false
          | _ ->
            do! socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "NormalClosure", new CancellationToken())
            return false
        }), timerTokenSource.Token)
      if (not timerTokenSource.Token.IsCancellationRequested) then
        if connectionInitSucceeded then
          return (succeed ())
        else
          return (fail "ConnectionInit failed (not because of timeout)")
      else
        return (fail "ConnectionInit timeout")
    }

  member __.InvokeAsync(ctx : HttpContext) =
    task {
      if false && not (ctx.Request.Path = PathString (options.EndpointUrl)) then
        do! next.Invoke(ctx)
      else
        if ctx.WebSockets.IsWebSocketRequest then
          use! socket = ctx.WebSockets.AcceptWebSocketAsync("graphql-transport-ws")
          let! connectionInitResult =
            socket
            |> waitForConnectionInit jsonOptions.Value options.SchemaExecutor Map.empty options.ConnectionInitTimeoutInMs
          match connectionInitResult with
          | Failure errMsg ->
            printfn "%A" errMsg
          | Success _ ->
            do! ConnectionAck |> (socket |> sendMessageViaSocket (new CancellationToken()) jsonOptions.Value)
            let longRunningCancellationToken =
              (CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, applicationLifetime.ApplicationStopping).Token)
            longRunningCancellationToken.Register(fun _ ->
              if socket |> canCloseSocket then
                socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "NormalClosure", new CancellationToken())
                |> Async.AwaitTask
                |> Async.RunSynchronously
              else
                ()
            ) |> ignore
            let safe_HandleMessages = handleMessages longRunningCancellationToken
            try
              do! socket
                  |> safe_HandleMessages jsonOptions.Value options.SchemaExecutor options.RootFactory
            with
              | ex ->
                printfn "Unexpected exception \"%s\" in GraphQLWebsocketMiddleware. More:\n%s" (ex.GetType().Name) (ex.ToString())
        else
          do! next.Invoke(ctx)
    }