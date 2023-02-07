namespace GraphQLTransportWS

module GraphQLWsMessageRawMapping =
  open FSharp.Data.GraphQL

  let requireId (raw : GraphQLWsMessageRaw) : string =
    match raw.Id with
    | Some s -> s
    | None -> failwith "property \"id\" is required but was not there"

  let requirePayloadToBeAnOptionalString (payload : GraphQLWsMessagePayloadRaw option) : string option =
    match payload with
    | Some p  ->
      match p with
      | StringPayload strPayload -> Some strPayload
      | _ -> failwith "payload was expected to be a string, but it wasn't"
    | None -> None

  let requireSubscribePayload (executor : Executor<'a>) (payload : GraphQLWsMessagePayloadRaw option) : GraphQLQuery =
    match payload with
    | Some p ->
      match p with
      | SubscribePayload rawSubsPayload ->
        match rawSubsPayload.Query with
        | Some query ->
          { ExecutionPlan = executor.CreateExecutionPlan(query)
            Variables = Map.empty }
        | None ->
          failwith "there was no query in subscribe message!"
      | _ ->
        failwith "payload was expected to be a subscribe payload object, but it wasn't."
    | None ->
      failwith "payload is required for this message, but none was available"

  let toWebSocketClientMessage (executor : Executor<'a>) (raw : GraphQLWsMessageRaw) : WebSocketClientMessage =
    match raw.Type with
    | None ->
      failwithf "property \"type\" was not found in the client message"
    | Some "connection_init" ->
      ConnectionInit (raw.Payload |> requirePayloadToBeAnOptionalString)
    | Some "ping" ->
      ClientPing (raw.Payload |> requirePayloadToBeAnOptionalString)
    | Some "pong" ->
      ClientPong (raw.Payload |> requirePayloadToBeAnOptionalString)
    | Some "complete" ->
      ClientComplete (raw |> requireId)
    | Some "subscribe" ->
      let id = raw |> requireId
      let payload = raw.Payload |> requireSubscribePayload executor
      Subscribe (id, payload)
    | Some other ->
      failwithf "type \"%s\" is not supported as a client message type" other

