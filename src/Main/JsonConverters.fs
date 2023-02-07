namespace GraphQLTransportWS

open System
open System.Text.Json
open System.Text.Json.Serialization

[<Sealed>]
type GraphQLWsMessageConverter() =
  inherit JsonConverter<GraphQLWsMessageRaw>()

  let getOptionalString (reader : byref<Utf8JsonReader>) =
    if reader.TokenType.Equals(JsonTokenType.Null) then
      None
    else
      Some (reader.GetString())

  let readPropertyValueAsAString (propertyName : string) (reader : byref<Utf8JsonReader>) =
    if reader.Read() then
      getOptionalString(&reader)
    else
      failwithf "was expecting a value for property \"%s\"" propertyName

  let readSubscribePayload (reader : byref<Utf8JsonReader>) : GraphQLWsMessageSubscribePayloadRaw =
    let mutable operationName : string option = None
    let mutable query : string option = None
    let mutable variables : string option = None
    let mutable extensions : string option = None
    while reader.Read() && (not <| reader.TokenType.Equals(JsonTokenType.EndObject)) do
      match reader.GetString() with
      | "operationName" ->
        operationName <- readPropertyValueAsAString "operationName" &reader
      | "query" ->
        query <- readPropertyValueAsAString "query" &reader
      | "variables" ->
        variables <- readPropertyValueAsAString "variables" &reader
      | "extensions" ->
        extensions <- readPropertyValueAsAString "extensions" &reader
      | other ->
        failwithf "unexpected property \"%s\" in payload object" other
    { OperationName = operationName
      Query = query
      Variables = variables
      Extensions = extensions }

  let readPayload (reader : byref<Utf8JsonReader>) : GraphQLWsMessagePayloadRaw option =
    if reader.Read() then
      if reader.TokenType.Equals(JsonTokenType.String) then
        StringPayload (reader.GetString())
        |> Some
      elif reader.TokenType.Equals(JsonTokenType.StartObject) then
        SubscribePayload (readSubscribePayload &reader)
        |> Some
      elif reader.TokenType.Equals(JsonTokenType.Null) then
        failwith "was expecting a value for property \"payload\""
      else
        failwith "Not implemented yet. Uh-oh, this is a bug."
    else
      failwith "was expecting a value for property \"payload\""

  override __.Read(reader : byref<Utf8JsonReader>, typeToConvert: Type, options: JsonSerializerOptions) : GraphQLWsMessageRaw =
    if not (reader.TokenType.Equals(JsonTokenType.StartObject))
      then raise (new JsonException((sprintf "reader's first token was not \"%A\", but \"%A\"" JsonTokenType.StartObject reader.TokenType)))
    else
      let mutable id : string option = None
      let mutable theType : string option = None
      let mutable payload : GraphQLWsMessagePayloadRaw option = None
      while reader.Read() && (not (reader.TokenType.Equals(JsonTokenType.EndObject))) do
        match reader.GetString() with
        | "id" ->
          id <- readPropertyValueAsAString "id" &reader
        | "type" ->
          theType <- readPropertyValueAsAString "type" &reader
        | "payload" ->
          payload <- readPayload &reader
        | other ->
          failwithf "unknown property \"%s\"" other
      { Id = id
        Type = theType
        Payload = payload }

  override __.Write(writer : Utf8JsonWriter, value : GraphQLWsMessageRaw, options : JsonSerializerOptions) =
    failwith "serializing a WebSocketClientMessage is not supported (yet(?))"

[<Sealed>]
type WebSocketServerMessageConverter() =
  inherit JsonConverter<WebSocketServerMessage>()

  override  __.Read(reader: byref<Utf8JsonReader>, typeToConvert: Type, options: JsonSerializerOptions) =
    failwith "deserializing a WebSocketServerMessage is not supported (yet(?))"

  override __.Write(writer: Utf8JsonWriter, value: WebSocketServerMessage, options: JsonSerializerOptions) =
    let serializeAny x = JsonSerializer.Serialize(x, options)
    let thenAddProperty (propName: string) (propStrValue: string) =
      writer.WritePropertyName(propName)
      writer.WriteStringValue(propStrValue)

    let beginSerializedObjWith (propName: string) (propStrValue: string) =
      writer.WriteStartObject()
      thenAddProperty propName propStrValue

    let endSerializedObj () =
      writer.WriteEndObject()

    let addOptionalPayloadProperty (optionalPayload: string option) =
      match optionalPayload with
      | Some p ->
          thenAddProperty "payload" p
      | None ->
          ()

    match value with
    | Error (id, errs) ->
        beginSerializedObjWith "type" "error"
        thenAddProperty "id" id
        writer.WriteStartArray("payload")
        errs
        |> List.iter
            (fun err ->
              writer.WriteStartObject()
              writer.WritePropertyName("message")
              writer.WriteStringValue(err)
              writer.WriteEndObject()
            )
        writer.WriteEndArray()
        endSerializedObj()
    | ConnectionAck ->
        beginSerializedObjWith "type" "connection_ack"
        endSerializedObj()
    | ServerPing p ->
        beginSerializedObjWith "type" "ping"
        addOptionalPayloadProperty p
        endSerializedObj()
    | ServerPong p ->
        beginSerializedObjWith "type" "pong"
        addOptionalPayloadProperty p
        endSerializedObj()
    | Next(id, result) ->
        beginSerializedObjWith "type" "next"
        thenAddProperty "id" id
        writer.WritePropertyName("payload")
        writer.WriteRawValue(serializeAny result, skipInputValidation = true)
        endSerializedObj()
    | Complete(id) ->
        beginSerializedObjWith "type" "complete"
        thenAddProperty "id" id
        endSerializedObj()


    
    // writer.WriteStartObject(propertyName = )