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

  let private requireSubscribePayload (executor : Executor<'a>) (payload : RawPayload option) : RopResult<GraphQLQuery, ClientMessageProtocolFailure> =
    match payload with
    | Some p ->
      match p with
      | SubscribePayload rawSubsPayload ->
        match rawSubsPayload.Query with
        | Some query ->
          let executionPlan =
            match rawSubsPayload.OperationName with
            | Some operationName ->
              executor.CreateExecutionPlan(query, operationName = operationName)
            | None ->
              executor.CreateExecutionPlan(query)
          let variablesResult : RopResult<Map<string, obj>, ClientMessageProtocolFailure> =
            match executionPlan.Variables with
            | [] ->
              succeed <| Map.empty
            | expectedVariables ->
              match rawSubsPayload.Variables with
              | None -> invalidMsg "No variable values were provided, although some were expected."
              | Some variableValuesObj ->
                if (not (variableValuesObj.RootElement.ValueKind.Equals(JsonValueKind.Object))) then
                  invalidMsg (sprintf "client-specified GraphQL variables should be an object, but here were '%A'" variableValuesObj.RootElement.ValueKind)
                else
                  let providedVariableValues = variableValuesObj.RootElement.EnumerateObject() |> List.ofSeq
                  let expectedVariablesParsingResult =
                    expectedVariables
                    |> List.map
                        (fun expectedVariable ->
                          match providedVariableValues |> List.tryFind(fun x -> x.Name = expectedVariable.Name) with
                          | Some providedValue ->
                            let boxedValue =
                              if providedValue.Value.ValueKind.Equals(JsonValueKind.Null) then
                                null :> obj
                              elif providedValue.Value.ValueKind.Equals(JsonValueKind.String) then
                                providedValue.Value.GetString() :> obj
                              else
                                JsonSerializer.Deserialize(providedValue.Value, new JsonSerializerOptions()) :> obj
                            succeed <| Some (expectedVariable.Name, boxedValue)
                          | None ->
                            match expectedVariable.DefaultValue, expectedVariable.TypeDef with
                            | Some _, _ ->
                              succeed <| None // it has a default value, so this default value will be used later when needed
                            | _, Nullable _ ->
                              succeed <| None // ok, it doesn't have a default value, but it's nullable, so it's a valid case that it's not there
                            | None, _ ->
                              fail <| sprintf "value for required variable \"%s\" was not present in the request!" expectedVariable.Name
                        )
                  let errors =
                    expectedVariablesParsingResult
                    |> List.filter (either (fun _ -> false) (fun _ -> true))
                    |> List.map
                          (either
                            (fun _ -> "")
                            (fun errMsgs -> System.String.Join("\n", errMsgs)))

                  if not errors.IsEmpty then
                    invalidMsg (System.String.Join("\n", errors))
                  else
                    expectedVariablesParsingResult
                    |> List.filter (either (fun _ -> true) (fun _ -> false))
                    |> List.map
                        (either
                        (fun (keyValuePair, _) ->
                                    match keyValuePair with
                                    | None -> None
                                    | Some (key, theValue) ->
                                      Some (key, theValue)
                                  )
                        (fun _ ->
                                    failwith "shouldn't be here"
                                    None
                                  )
                        )
                    |> List.fold
                        (fun acc curr ->
                          match curr with
                          | Some (key, value) -> acc |> Map.add key value
                          | None -> acc
                        )
                        Map.empty
                    |> succeed

          variablesResult
          |> mapR (fun variables ->
                    { ExecutionPlan = executionPlan
                      Variables = variables })

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

