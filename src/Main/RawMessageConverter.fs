namespace GraphQLTransportWS

open System
open System.Text.Json
open System.Text.Json.Serialization

[<Sealed>]
type RawMessageConverter() =
  inherit JsonConverter<RawMessage>()

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

  let readSubscribePayload (reader : byref<Utf8JsonReader>) : RawSubscribePayload =
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

  let readPayload (reader : byref<Utf8JsonReader>) : RawPayload option =
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
          payload <- readPayload &reader
        | other ->
          failwithf "unknown property \"%s\"" other
      { Id = id
        Type = theType
        Payload = payload }

  override __.Write(writer : Utf8JsonWriter, value : RawMessage, options : JsonSerializerOptions) =
    failwith "serializing a WebSocketClientMessage is not supported (yet(?))"
