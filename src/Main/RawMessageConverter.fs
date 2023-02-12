namespace GraphQLTransportWS

open FSharp.Data.GraphQL
open Rop
open System
open System.Text.Json
open System.Text.Json.Serialization

[<Sealed>]
type ClientMessageConverter<'Root>(executor : Executor<'Root>) =
  inherit JsonConverter<ClientMessage>()

  let raiseInvalidMsg explanation =
    raise <| InvalidMessageException explanation

  /// From the spec: "Receiving a message of a type or format which is not specified in this document will result in an immediate socket closure with the event 4400: &lt;error-message&gt;.
  /// The &lt;error-message&gt; can be vaguely descriptive on why the received message is invalid."
  let invalidMsg (explanation : string) =
    InvalidMessage (4400, explanation)
    |> fail

  let unpackRopResult ropResult =
    match ropResult with
    | Success (x, _) -> x
    | Failure (failures : ClientMessageProtocolFailure list) ->
      System.String.Join("\n\n", (failures |> Seq.map (fun (InvalidMessage (_, explanation)) -> explanation)))
      |> raiseInvalidMsg

  let getOptionalString (reader : byref<Utf8JsonReader>) =
    if reader.TokenType.Equals(JsonTokenType.Null) then
      None
    else
      Some (reader.GetString())

  let readPropertyValueAsAString (propertyName : string) (reader : byref<Utf8JsonReader>) =
    if reader.Read() then
      getOptionalString(&reader)
    else
      raiseInvalidMsg <| sprintf "was expecting a value for property \"%s\"" propertyName

  let requireId (raw : RawMessage) : RopResult<string, ClientMessageProtocolFailure> =
    match raw.Id with
    | Some s -> succeed s
    | None -> invalidMsg <| "property \"id\" is required for this message but was not present."

  let resolveVariables (serializerOptions : JsonSerializerOptions) (expectedVariables : Types.VarDef list) (variableValuesObj : JsonDocument) =
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
                      JsonSerializer.Deserialize(providedValue.Value, serializerOptions) :> obj
                  (expectedVariable.Name, boxedValue)
                )
            )
        |> succeed
    finally
      variableValuesObj.Dispose()

  let decodeGraphQLQuery (serializerOptions : JsonSerializerOptions) (executor : Executor<'a>) (operationName : string option) (variables : JsonDocument option) (query : string) =
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
        |> resolveVariables serializerOptions executionPlan.Variables
        |> mapMessagesR (fun errMsg -> InvalidMessage (CustomWebSocketStatus.invalidMessage, errMsg))
        |> mapR Map.ofList
    variablesResult
    |> mapR (fun variables ->
              { ExecutionPlan = executionPlan
                Variables = variables })

  let requireSubscribePayload (serializerOptions : JsonSerializerOptions) (executor : Executor<'a>) (payload : JsonDocument option) : RopResult<GraphQLQuery, ClientMessageProtocolFailure> =
    match payload with
    | None ->
      invalidMsg <| "payload is required for this message, but none was present."
    | Some p ->
      let rawSubsPayload = JsonSerializer.Deserialize<RawSubscribePayload option>(p, serializerOptions)
      match rawSubsPayload with
      | None ->
        invalidMsg <| "payload is required for this message, but none was present."
      | Some subscribePayload ->
        match subscribePayload.Query with
        | None ->
          invalidMsg <| sprintf "there was no query in the client's subscribe message!"
        | Some query ->
          query
          |> decodeGraphQLQuery serializerOptions executor subscribePayload.OperationName subscribePayload.Variables


  let readRawMessage (reader : byref<Utf8JsonReader>, options: JsonSerializerOptions) : RawMessage =
    if not (reader.TokenType.Equals(JsonTokenType.StartObject))
      then raise (new JsonException((sprintf "reader's first token was not \"%A\", but \"%A\"" JsonTokenType.StartObject reader.TokenType)))
    else
      let mutable id : string option = None
      let mutable theType : string option = None
      let mutable payload : JsonDocument option = None
      while reader.Read() && (not (reader.TokenType.Equals(JsonTokenType.EndObject))) do
        match reader.GetString() with
        | "id" ->
          id <- readPropertyValueAsAString "id" &reader
        | "type" ->
          theType <- readPropertyValueAsAString "type" &reader
        | "payload" ->
          payload <- Some <| JsonDocument.ParseValue(&reader)
        | other ->
          raiseInvalidMsg <| sprintf "unknown property \"%s\"" other

      match theType with
      | None ->
        raiseInvalidMsg "property \"type\" is missing"
      | Some msgType ->
        { Id = id
          Type = msgType
          Payload = payload }

  override __.Read(reader : byref<Utf8JsonReader>, typeToConvert: Type, options: JsonSerializerOptions) : ClientMessage =
    let raw = readRawMessage(&reader, options)
    match raw.Type with
    | "connection_init" ->
      ConnectionInit raw.Payload
    | "ping" ->
      ClientPing raw.Payload
    | "pong" ->
      ClientPong raw.Payload
    | "complete" ->
      raw
      |> requireId
      |> mapR ClientComplete
      |> unpackRopResult
    | "subscribe" ->
      raw
      |> requireId
      |> bindR
        (fun id ->
          raw.Payload
          |> requireSubscribePayload options executor
          |> mapR (fun payload -> (id, payload))
        )
      |> mapR Subscribe
      |> unpackRopResult
    | other ->
      raiseInvalidMsg <| sprintf "invalid type \"%s\" specified by client." other



  override __.Write(writer : Utf8JsonWriter, value : ClientMessage, options : JsonSerializerOptions) =
    failwith "serializing a WebSocketClientMessage is not supported (yet(?))"

[<Sealed>]
type RawServerMessageConverter() =
  inherit JsonConverter<RawServerMessage>()

  override __.Read(reader : byref<Utf8JsonReader>, typeToConvert: Type, options : JsonSerializerOptions) : RawServerMessage =
    failwith "deserializing a RawServerMessage is not supported (yet(?))"

  override __.Write(writer : Utf8JsonWriter, value : RawServerMessage, options : JsonSerializerOptions) =
    writer.WriteStartObject()
    writer.WriteString("type", value.Type)
    match value.Id with
    | None ->
      ()
    | Some id ->
      writer.WriteString("id", id)
    
    match value.Payload with
    | None ->
      ()
    | Some serverRawPayload ->
      match serverRawPayload with
      | ExecutionResult output ->
        writer.WritePropertyName("payload")
        JsonSerializer.Serialize(writer, output, options)
      | ErrorMessages msgs ->
        JsonSerializer.Serialize(writer, msgs, options)
      | CustomResponse jsonDocument ->
        jsonDocument.WriteTo(writer)

    writer.WriteEndObject()
