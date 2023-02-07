namespace GraphQLTransportWS

module GraphQLWsMessageRawMapping =

  let requirePayloadToBeAnOptionalString (payload : GraphQLWsMessagePayloadRaw option) : string option =
    match payload with
    | Some p  ->
      match p with
      | StringPayload strPayload -> Some strPayload
      | _ -> failwith "payload was expected to be a string, but it wasn't"
    | None -> None

  let requireId (raw : GraphQLWsMessageRaw) : string =
    match raw.Id with
    | Some s -> s
    | None -> failwith "property \"id\" is required but was not there"

  let toWebSocketClientMessage (raw : GraphQLWsMessageRaw) : WebSocketClientMessage =
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
    | Some other ->
      failwithf "type \"%s\" is not supported as a client message type" other

