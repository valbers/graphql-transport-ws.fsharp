namespace GraphQLTransportWS

module MessageMapping =
  open FSharp.Data.GraphQL
  open FSharp.Data.GraphQL.Types.Patterns
  open Rop
  open System.Text.Json

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

  let private resolveVariables (expectedVariables : Types.VarDef list) (variableValuesObj : JsonDocument) =
    try
      if (not (variableValuesObj.RootElement.ValueKind.Equals(JsonValueKind.Object))) then
        let offendingValueKind = variableValuesObj.RootElement.ValueKind
        fail (sprintf "\"variables\" must be an object, but here it is \"%A\" instead" offendingValueKind)
      else
        let providedVariableValues = variableValuesObj.RootElement.EnumerateObject() |> List.ofSeq
        expectedVariables
        |> List.choose
            (fun expectedVariable ->
              providedVariableValues
              |> List.tryFind(fun x -> x.Name = expectedVariable.Name)
              |> Option.map
                (fun providedValue ->
                  let boxedValue =
                    if providedValue.Value.ValueKind.Equals(JsonValueKind.Null) then
                      null :> obj
                    elif providedValue.Value.ValueKind.Equals(JsonValueKind.String) then
                      providedValue.Value.GetString() :> obj
                    else
                      JsonSerializer.Deserialize(providedValue.Value, new JsonSerializerOptions()) :> obj
                  (expectedVariable.Name, boxedValue)
                )
            )
        |> succeed
    finally
      variableValuesObj.Dispose()

  let decodeGraphQLQuery (executor : Executor<'a>) (operationName : string option) (variables : JsonDocument option) (query : string) =
    let executionPlan =
      match operationName with
      | Some operationName ->
        executor.CreateExecutionPlan(query, operationName = operationName)
      | None ->
        executor.CreateExecutionPlan(query)
    let variablesResult : RopResult<Map<string, obj>, ClientMessageProtocolFailure> =
      match variables with
      | None -> succeed <| Map.empty // it's none of our business here if some variables are expected. If that's the case, execution of the ExecutionPlan will take care of that later (and issue an error).
      | Some variableValuesObj ->
        variableValuesObj
        |> resolveVariables executionPlan.Variables
        |> mapMessagesR (fun errMsg -> InvalidMessage (CustomWebSocketStatus.invalidMessage, errMsg))
        |> mapR Map.ofList
    variablesResult
    |> mapR (fun variables ->
              { ExecutionPlan = executionPlan
                Variables = variables })

  let private requireSubscribePayload (executor : Executor<'a>) (payload : RawPayload option) : RopResult<GraphQLQuery, ClientMessageProtocolFailure> =
    match payload with
    | None ->
      invalidMsg <| "payload is required for this message, but none was present."
    | Some p ->
      match p with
      | SubscribePayload rawSubsPayload ->
        match rawSubsPayload.Query with
        | None ->
          invalidMsg <| sprintf "there was no query in the client's subscribe message!"
        | Some query ->
          query
          |> decodeGraphQLQuery executor rawSubsPayload.OperationName rawSubsPayload.Variables
      | _ ->
        invalidMsg <| "for this message, payload was expected to be a \"subscribe\" payload object, but it wasn't."


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

