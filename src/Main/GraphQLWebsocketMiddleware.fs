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

type SubscriptionId = SubsId of string
type SubscriptionUnsubscriber = Unsubscriber of IDisposable
type OnUnsubscribeAction = OnUnsubAction of (SubscriptionId -> unit)
type SubscriptionsDict = SubsDict of IDictionary<SubscriptionId, SubscriptionUnsubscriber * OnUnsubscribeAction>

module internal GraphQLSubscriptionsManagement =
  let addSubscription (id : SubscriptionId, unsubscriber : SubscriptionUnsubscriber, onUnsubscribe : OnUnsubscribeAction)
                      (subscriptions : SubscriptionsDict) =
    match subscriptions with
    | SubsDict subscriptions ->
      printfn "GraphQLSubscriptionsManagement: new subscription (id: \"%s\". Total: %d)" (id |> string) subscriptions.Count
      subscriptions.Add(id, (unsubscriber, onUnsubscribe))

  let isIdTaken (id : SubscriptionId) (subscriptions : SubscriptionsDict) =
    match subscriptions with
    | SubsDict subscriptions ->
      subscriptions.ContainsKey(id)

  let executeOnUnsubscribeAndDispose (id : SubscriptionId) (subscription : SubscriptionUnsubscriber * OnUnsubscribeAction) =
      match subscription with
      | Unsubscriber unsubscriber, OnUnsubAction onUnsubscribe ->
        id |> onUnsubscribe
        unsubscriber.Dispose()

  let removeSubscription (id: SubscriptionId) (subscriptions : SubscriptionsDict) =
    match subscriptions with
    | SubsDict subscriptions ->
      if subscriptions.ContainsKey(id) then
        subscriptions.[id]
        |> executeOnUnsubscribeAndDispose id
        subscriptions.Remove(id) |> ignore

  let removeAllSubscriptions (subscriptions : SubscriptionsDict) =
    match subscriptions with
    | SubsDict subscriptions ->
      subscriptions
      |> Seq.iter
          (fun subscription ->
              subscription.Value
              |> executeOnUnsubscribeAndDispose subscription.Key
          )
      subscriptions.Clear()

type GraphQLWebSocketMiddleware<'Root>(next : RequestDelegate, applicationLifetime : IHostApplicationLifetime, options : GraphQLWebsocketMiddlewareOptions<'Root>) =

  let serializeServerMessage (jsonSerializerOptions: JsonSerializerOptions) (serverMessage : ServerMessage) =
      task {
        let raw =
          match serverMessage with
          | ConnectionAck ->
            { Id = None
              Type = "connection_ack"
              Payload = None }
          | ServerPing ->
            { Id = None
              Type = "ping"
              Payload = None }
          | ServerPong ->
            { Id = None
              Type = "pong"
              Payload = None }
          | Next (id, payload) ->
            { Id = Some id
              Type = "next"
              Payload = Some <| ExecutionResult payload }
          | Complete id ->
            { Id = Some id
              Type = "complete"
              Payload = None }
          | Error (id, errMsgs) ->
            { Id = Some id
              Type = "error"
              Payload = Some <| ErrorMessages errMsgs }
        return JsonSerializer.Serialize(raw, jsonSerializerOptions)
      }

  let deserializeClientMessage (serializerOptions : JsonSerializerOptions) (executor : Executor<'Root>) (msg: string) =
    task {
      return
        JsonSerializer.Deserialize<RawMessage>(msg, serializerOptions)
        |> MessageMapping.toClientMessage executor
    }

  let isSocketOpen (theSocket : WebSocket) =
    not (theSocket.State = WebSocketState.Aborted) &&
    not (theSocket.State = WebSocketState.Closed) &&
    not (theSocket.State = WebSocketState.CloseReceived)

  let canCloseSocket (theSocket : WebSocket) =
    not (theSocket.State = WebSocketState.Aborted) &&
    not (theSocket.State = WebSocketState.Closed)

  let receiveMessageViaSocket (cancellationToken : CancellationToken) (serializerOptions: JsonSerializerOptions) (executor : Executor<'Root>) (replacements : Map<string, obj>) (socket : WebSocket) : Task<RopResult<ClientMessage, ClientMessageProtocolFailure> option> =
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
            |> deserializeClientMessage serializerOptions executor
          return Some result
        with :? JsonException as e ->
          printfn "%s" (e.ToString())
          return Some (MessageMapping.invalidMsg <| "invalid json in client message")
    }

  let sendMessageViaSocket (jsonSerializerOptions) (socket : WebSocket) (message : ServerMessage) =
    task {
      if not (socket.State = WebSocketState.Open) then
        printfn "ignoring message to be sent via socket, since its state is not 'Open', but '%A'" socket.State
      else
        let! serializedMessage = message |> serializeServerMessage jsonSerializerOptions
        let segment =
          new ArraySegment<byte>(
            System.Text.Encoding.UTF8.GetBytes(serializedMessage)
          )
        if not (socket.State = WebSocketState.Open) then
          printfn "ignoring message to be sent via socket, since its state is not 'Open', but '%A'" socket.State
        else
          do! socket.SendAsync(segment, WebSocketMessageType.Text, endOfMessage = true, cancellationToken = new CancellationToken())
        printfn "<- %A" message
    }

  let addClientSubscription (id : SubscriptionId) (jsonSerializerOptions) (socket) (howToSendDataOnNext: SubscriptionId -> Output -> Task<unit>) (streamSource: IObservable<Output>) (subscriptions : SubscriptionsDict)  =
    let observer = new Reactive.AnonymousObserver<Output>(
      onNext =
        (fun theOutput ->
          theOutput
          |> howToSendDataOnNext id
          |> Async.AwaitTask
          |> Async.RunSynchronously
        ),
      onError =
        (fun ex ->
          printfn "[Error on subscription \"%A\"]: %s" id (ex.ToString())
        ),
      onCompleted =
        (fun () ->
          match id with
          | SubsId theId ->
            Complete theId
            |> sendMessageViaSocket jsonSerializerOptions (socket)
            |> Async.AwaitTask
            |> Async.RunSynchronously
          subscriptions
          |> GraphQLSubscriptionsManagement.removeSubscription(id)
        )
    )

    let unsubscriber = streamSource.Subscribe(observer)

    subscriptions
    |> GraphQLSubscriptionsManagement.addSubscription(id, Unsubscriber unsubscriber, OnUnsubAction (fun _ -> ()))

  let tryToGracefullyCloseSocket (code, message) theSocket =
    task {
        if theSocket |> canCloseSocket
        then
          do! theSocket.CloseAsync(code, message, new CancellationToken())
        else
          ()
    }

  let tryToGracefullyCloseSocketWithDefaultBehavior =
    tryToGracefullyCloseSocket (WebSocketCloseStatus.NormalClosure, "Normal Closure")

  let handleMessages (cancellationToken: CancellationToken) (serializerOptions: JsonSerializerOptions) (executor : Executor<'Root>) (root: unit -> 'Root) (socket : WebSocket) =
    let subscriptions = SubsDict <| new Dictionary<SubscriptionId, SubscriptionUnsubscriber * OnUnsubscribeAction>()
    // ---------->
    // Helpers -->
    // ---------->
    let safe_ReceiveMessageViaSocket = receiveMessageViaSocket (new CancellationToken())

    let safe_Send = sendMessageViaSocket serializerOptions socket
    let safe_Receive() =
      socket
      |> safe_ReceiveMessageViaSocket serializerOptions executor Map.empty

    let safe_SendQueryOutput id output =
      match id with
      | SubsId theId ->
        let outputAsDict = output :> IDictionary<string, obj>
        match outputAsDict.TryGetValue("errors") with
        | true, theValue ->
          // The specification says: "This message terminates the operation and no further messages will be sent."
          subscriptions
          |> GraphQLSubscriptionsManagement.removeSubscription(id)
          safe_Send (Error (theId, unbox theValue))
        | false, _ ->
          safe_Send (Next (theId, output))

    let sendQueryOutputDelayedBy (cancToken: CancellationToken) (ms: int) id output =
      task {
            do! Async.StartAsTask(Async.Sleep ms, cancellationToken = cancToken)
            do! output
                |> safe_SendQueryOutput id
        }
    let safe_SendQueryOutputDelayedBy = sendQueryOutputDelayedBy cancellationToken

    let safe_ApplyPlanExecutionResult (id: SubscriptionId) (socket) (executionResult: GQLResponse)  =
      task {
            match executionResult with
            | Stream observableOutput ->
                subscriptions
                |> addClientSubscription id serializerOptions socket safe_SendQueryOutput observableOutput
            | Deferred (data, errors, observableOutput) ->
                do! data
                    |> safe_SendQueryOutput id
                if errors.IsEmpty then
                  subscriptions
                  |> addClientSubscription id serializerOptions socket (safe_SendQueryOutputDelayedBy 5000) observableOutput
                else
                  ()
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
                      do! socket.CloseAsync(enum code, explanation, new CancellationToken())
                | Success (ConnectionInit p, _) ->
                    "ConnectionInit" |> logMsgReceivedWithOptionalPayload p
                    do! socket.CloseAsync(
                      enum CustomWebSocketStatus.tooManyInitializationRequests,
                      "too many initialization requests",
                      new CancellationToken())
                | Success (ClientPing p, _) ->
                    "ClientPing" |> logMsgReceivedWithOptionalPayload p
                    do! ServerPong |> safe_Send
                | Success (ClientPong p, _) ->
                    "ClientPong" |> logMsgReceivedWithOptionalPayload p
                | Success (Subscribe (id, query), _) ->
                    "Subscribe" |> logMsgWithIdReceived id
                    let subsId = SubsId id
                    if subscriptions |> GraphQLSubscriptionsManagement.isIdTaken subsId then
                      do! socket.CloseAsync(
                        enum CustomWebSocketStatus.subscriberAlreadyExists,
                        sprintf "Subscriber for %s already exists" id,
                        new CancellationToken())
                    else
                      let! planExecutionResult =
                        executor.AsyncExecute(query.ExecutionPlan, root(), query.Variables)
                        |> Async.StartAsTask
                      do! planExecutionResult
                          |> safe_ApplyPlanExecutionResult subsId socket
                | Success (ClientComplete id, _) ->
                    "ClientComplete" |> logMsgWithIdReceived id
                    subscriptions |> GraphQLSubscriptionsManagement.removeSubscription (SubsId id)
                    do! Complete id |> safe_Send
        printfn "Leaving graphql-ws connection loop..."
        do! socket |> tryToGracefullyCloseSocketWithDefaultBehavior
      with
      | ex ->
        printfn "Unexpected exception \"%s\" in GraphQLWebsocketMiddleware (handleMessages). More:\n%s" (ex.GetType().Name) (ex.ToString())
        // at this point, only something really weird must have happened.
        // In order to avoid faulty state scenarios and unimagined damages,
        // just close the socket without further ado.
        do! socket |> tryToGracefullyCloseSocketWithDefaultBehavior
    }

  let waitForConnectionInitAndRespondToClient (serializerOptions : JsonSerializerOptions) (schemaExecutor : Executor<'Root>) (replacements : Map<string, obj>) (connectionInitTimeoutInMs : int) (socket : WebSocket) : Task<RopResult<unit, string>> =
    task {
      let timerTokenSource = new CancellationTokenSource()
      timerTokenSource.CancelAfter(connectionInitTimeoutInMs)
      let detonationRegistration = timerTokenSource.Token.Register(fun _ ->
        socket
        |> tryToGracefullyCloseSocket (enum CustomWebSocketStatus.connectionTimeout, "Connection initialisation timeout")
        |> Async.AwaitTask
        |> Async.RunSynchronously
      )
      let! connectionInitSucceeded = Task.Run<bool>((fun _ ->
        task {
          printfn "Waiting for ConnectionInit..."
          let! receivedMessage = receiveMessageViaSocket (new CancellationToken()) serializerOptions schemaExecutor replacements socket
          match receivedMessage with
          | Some (Success (ConnectionInit payload, _)) ->
            printfn "Valid connection_init received! Responding with ACK!"
            detonationRegistration.Unregister() |> ignore
            do! ConnectionAck |> sendMessageViaSocket serializerOptions socket
            return true
          | Some (Success (Subscribe _, _)) ->
            do!
              socket
              |> tryToGracefullyCloseSocket (enum CustomWebSocketStatus.unauthorized, "Unauthorized")
            return false
          | Some (Failure [InvalidMessage (code, explanation)]) ->
            do!
              socket
              |> tryToGracefullyCloseSocket (enum code, explanation)
            return false
          | _ ->
            do!
              socket
              |> tryToGracefullyCloseSocketWithDefaultBehavior
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
      let jsonOptions = new JsonOptions()
      jsonOptions.SerializerOptions.Converters.Add(new RawMessageConverter())
      jsonOptions.SerializerOptions.Converters.Add(new RawServerMessageConverter())
      let serializerOptions = jsonOptions.SerializerOptions
      if false && not (ctx.Request.Path = PathString (options.EndpointUrl)) then
        do! next.Invoke(ctx)
      else
        if ctx.WebSockets.IsWebSocketRequest then
          use! socket = ctx.WebSockets.AcceptWebSocketAsync("graphql-transport-ws")
          let! connectionInitResult =
            socket
            |> waitForConnectionInitAndRespondToClient serializerOptions options.SchemaExecutor Map.empty options.ConnectionInitTimeoutInMs
          match connectionInitResult with
          | Failure errMsg ->
            printfn "%A" errMsg
          | Success _ ->
            let longRunningCancellationToken =
              (CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, applicationLifetime.ApplicationStopping).Token)
            longRunningCancellationToken.Register(fun _ ->
                socket
                |> tryToGracefullyCloseSocketWithDefaultBehavior
                |> Async.AwaitTask
                |> Async.RunSynchronously
            ) |> ignore
            let safe_HandleMessages = handleMessages longRunningCancellationToken
            try
              do! socket
                  |> safe_HandleMessages serializerOptions options.SchemaExecutor options.RootFactory
            with
              | ex ->
                printfn "Unexpected exception \"%s\" in GraphQLWebsocketMiddleware. More:\n%s" (ex.GetType().Name) (ex.ToString())
        else
          do! next.Invoke(ctx)
    }