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

  override __.Read(reader : byref<Utf8JsonReader>, typeToConvert: Type, options: JsonSerializerOptions) : RawMessage =
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
      | Some _ ->
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
        JsonSerializer.Serialize(writer, msgs, options)

    writer.WriteEndObject()
