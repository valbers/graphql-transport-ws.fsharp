namespace GraphQLTransportWS

module MessageMapping =
  open FSharp.Data.GraphQL
  open Rop

  /// From the spec: "Receiving a message of a type or format which is not specified in this document will result in an immediate socket closure with the event 4400: &lt;error-message&gt;.
  /// The &lt;error-message&gt; can be vaguely descriptive on why the received message is invalid."
  let invalidMsg (explanation : string) =
    InvalidMessage (4400, explanation)
    |> fail

  let private requireId (raw : RawMessage) : RopResult<string, ClientMessageProtocolFailure> =
    match raw.Id with
    | Some s -> succeed s
    | None -> invalidMsg <| "property \"id\" is required for this message but was not present."

  let private requirePayloadToBeAnOptionalString (payload : RawPayload option) : RopResult<string option, ClientMessageProtocolFailure> =
    match payload with
    | Some p  ->
      match p with
      | StringPayload strPayload ->
        Some strPayload
        |> succeed
      | SubscribePayload _ ->
        invalidMsg <| "for this message, payload was expected to be an optional string, but it was a \"subscribe\" payload instead."
    | None ->
      succeed None

  let private requireSubscribePayload (executor : Executor<'a>) (payload : RawPayload option) : RopResult<GraphQLQuery, ClientMessageProtocolFailure> =
    match payload with
    | Some p ->
      match p with
      | SubscribePayload rawSubsPayload ->
        match rawSubsPayload.Query with
        | Some query ->
          { ExecutionPlan = executor.CreateExecutionPlan(query)
            Variables = Map.empty }
          |> succeed
        | None ->
          invalidMsg <| sprintf "there was no query in the client's subscribe message!"
      | _ ->
        invalidMsg <| "for this message, payload was expected to be a \"subscribe\" payload object, but it wasn't."
    | None ->
      invalidMsg <| "payload is required for this message, but none was present."

  let toClientMessage (executor : Executor<'a>) (raw : RawMessage) : RopResult<ClientMessage, ClientMessageProtocolFailure> =
    match raw.Type with
    | None ->
      invalidMsg <| sprintf "message type was not specified by client."
    | Some "connection_init" ->
      raw.Payload
      |> requirePayloadToBeAnOptionalString
      |> mapR ConnectionInit
    | Some "ping" ->
      raw.Payload
      |> requirePayloadToBeAnOptionalString
      |> mapR ClientPing
    | Some "pong" ->
      raw.Payload
      |> requirePayloadToBeAnOptionalString
      |> mapR ClientPong
    | Some "complete" ->
      raw
      |> requireId
      |> mapR ClientComplete
    | Some "subscribe" ->
      raw
      |> requireId
      |> bindR
        (fun id ->
          raw.Payload
          |> requireSubscribePayload executor
          |> mapR (fun payload -> (id, payload))
        )
      |> mapR Subscribe
    | Some other ->
      invalidMsg <| sprintf "invalid type \"%s\" specified by client." other

