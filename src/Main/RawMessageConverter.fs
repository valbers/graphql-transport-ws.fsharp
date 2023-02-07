namespace GraphQLTransportWS

open System
open System.Text.Json
open System.Text.Json.Serialization

[<Sealed>]
type RawMessageConverter() =
  inherit JsonConverter<RawMessage>()

  let raiseInvalidMsg explanation =
    raise <| InvalidMessageException explanation

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

  let readSubscribePayload (reader : byref<Utf8JsonReader>, options : JsonSerializerOptions) : RawSubscribePayload =
    let mutable operationName : string option = None
    let mutable query : string option = None
    let mutable variables : JsonDocument option = None
    let mutable extensions : string option = None
    while reader.Read() && (not <| reader.TokenType.Equals(JsonTokenType.EndObject)) do
      match reader.GetString() with
      | "operationName" ->
        operationName <- readPropertyValueAsAString "operationName" &reader
      | "query" ->
        query <- readPropertyValueAsAString "query" &reader
      | "variables" ->
        variables <- Some <| JsonDocument.ParseValue(&reader)
      | "extensions" ->
        extensions <- readPropertyValueAsAString "extensions" &reader
      | other ->
        raiseInvalidMsg <| sprintf "unexpected property \"%s\" in payload object" other
    { OperationName = operationName
      Query = query
      Variables = variables
      Extensions = extensions }

  let readPayload (reader : byref<Utf8JsonReader>, options : JsonSerializerOptions) : RawPayload option =
    if reader.Read() then
      if reader.TokenType.Equals(JsonTokenType.String) then
        StringPayload (reader.GetString())
        |> Some
      elif reader.TokenType.Equals(JsonTokenType.StartObject) then
        SubscribePayload (readSubscribePayload (&reader, options))
        |> Some
      elif reader.TokenType.Equals(JsonTokenType.Null) then
        raiseInvalidMsg <| "was expecting a value for property \"payload\""
      else
        raiseInvalidMsg <| sprintf "payload is a \"%A\", which is not supported" reader.TokenType
    else
      raiseInvalidMsg <| "was expecting a value for property \"payload\""

  override __.Read(reader : byref<Utf8JsonReader>, typeToConvert: Type, options: JsonSerializerOptions) : RawMessage =
    if not (reader.TokenType.Equals(JsonTokenType.StartObject))
      then raise (new JsonException((sprintf "reader's first token was not \"%A\", but \"%A\"" JsonTokenType.StartObject reader.TokenType)))
    else
      let mutable id : string option = None
      let mutable theType : string option = None
      let mutable payload : RawPayload option = None
      while reader.Read() && (not (reader.TokenType.Equals(JsonTokenType.EndObject))) do
        match reader.GetString() with
        | "id" ->
          id <- readPropertyValueAsAString "id" &reader
        | "type" ->
          theType <- readPropertyValueAsAString "type" &reader
        | "payload" ->
          payload <- readPayload (&reader, options)
        | other ->
          raiseInvalidMsg <| sprintf "unknown property \"%s\"" other
      { Id = id
        Type = theType
        Payload = payload }

  override __.Write(writer : Utf8JsonWriter, value : RawMessage, options : JsonSerializerOptions) =
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
      | ServerStringPayload payload ->
        writer.WriteString("payload", payload)
      | ExecutionResult output ->
        writer.WritePropertyName("payload")
        JsonSerializer.Serialize(writer, output, options)
      | ErrorMessages msgs ->
        writer.WriteString("payload", String.Join("; ", msgs))

    writer.WriteEndObject()
