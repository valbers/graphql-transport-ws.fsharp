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

type GraphQLWebSocketMiddleware<'Root>(next : RequestDelegate, executor : Executor<'Root>, root : unit -> 'Root, url: string) =

  let serializeServerMessage obj =
      task { return "dummySerializedServerMessage" }

  let deserializeClientMessage (msg: string) =
    task { return ConnectionInit None }

  let receiveMessageViaSocket (executor : Executor<'Root>) (replacements : Map<string, obj>) (cancellationToken : CancellationToken) (socket : WebSocket) =
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
        let! deserializedMsg = deserializeClientMessage message
        return Some deserializedMsg
    }

  let sendMessageViaSocket (socket : WebSocket) (cancellationToken : CancellationToken) (message : WebSocketServerMessage) =
    task {
      if not (socket.State = WebSocketState.Open) then
        printfn "ignoring message to be sent via socket, since its state is not 'Open', but '%A'" socket.State
      else
        let! serializedMessage = message |> serializeServerMessage
        let segment =
          new ArraySegment<byte>(
            System.Text.Encoding.UTF8.GetBytes(serializedMessage)
          )
        do! socket.SendAsync(segment, WebSocketMessageType.Text, endOfMessage = true, cancellationToken = cancellationToken)
        printfn "<- %A" message
    }

  let handleMessages (executor : Executor<'Root>) (root: unit -> 'Root) (cancellationToken: CancellationToken) (socket : WebSocket) =
    // ---------->
    // Helpers -->
    // ---------->
    let send = sendMessageViaSocket socket cancellationToken
    let receive = receiveMessageViaSocket executor Map.empty cancellationToken

    let sendQueryOutput id output =
      task { do! send (Next (id, output)) }
    let sendQueryOutputDelayedBy (ms: int) id output =
      task {
            do! Async.Sleep ms |> Async.StartAsTask
            do! output |> sendQueryOutput id
        }

    let addClientSubscription (id: string) (howToSendDataOnNext: string -> Output -> Task<unit>) (streamSource: IObservable<Output>, cancellationToken: CancellationToken)  = task {
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

    let applyPlanExecutionResultToSocket (theSocket: WebSocket) (id: string) (cancellationToken: CancellationToken) (executionResult: GQLResponse)  =
      task {
            match executionResult with
            | Stream observableOutput ->
                do! (observableOutput, cancellationToken)
                    |> addClientSubscription id sendQueryOutput
            | Deferred (data, _, observableOutput) ->
                do! data |> sendQueryOutput id
                do! (observableOutput, cancellationToken)
                    |> addClientSubscription id (sendQueryOutputDelayedBy 5000)
            | Direct (data, _) ->
                do! data |> sendQueryOutput id
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
          while not cancellationToken.IsCancellationRequested do
              let! receivedMessage =
                socket |> receive
              match receivedMessage with
              | None ->
                  printfn "Warn: websocket socket received empty message!"
              | Some msg ->
                  match msg with
                  | ConnectionInit p ->
                      "ConnectionInit" |> logMsgReceivedWithOptionalPayload p
                      do! ConnectionAck |> send
                  | ClientPing p ->
                      "ClientPing" |> logMsgReceivedWithOptionalPayload p
                      do! ServerPong None |> send
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
                          |> applyPlanExecutionResultToSocket socket id cancellationToken
                  | ClientComplete id ->
                      "ClientComplete" |> logMsgWithIdReceived id
                      id |> GraphQLSubscriptionsManagement.removeSubscription
                      do! Complete id |> send
                  | InvalidMessage explanation ->
                      "InvalidMessage" |> logMsgReceivedWithOptionalPayload None
                      do! Error (None, explanation) |> send
      with // TODO: MAKE A PROPER GRAPHQL ERROR HANDLING!
      | ex ->
        printfn "Unexpected exception \"%s\" in GraphQLWebsocketMiddleware (handleMessages). More:\n%s" (ex.GetType().Name) (ex.ToString())
    }

  member __.InvokeAsync(ctx : HttpContext) =
    task {
      if not (ctx.Request.Path = PathString (url)) then
        do! next.Invoke(ctx)
      else
        if ctx.WebSockets.IsWebSocketRequest then
          use! socket = ctx.WebSockets.AcceptWebSocketAsync("graphql-transport-ws")
          try
            do! socket |> handleMessages executor root ctx.RequestAborted
          with
            | ex ->
              printfn "Unexpected exception \"%s\" in GraphQLWebsocketMiddleware. More:\n%s" (ex.GetType().Name) (ex.ToString())
        else
          do! next.Invoke(ctx)
    }