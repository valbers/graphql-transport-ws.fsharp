namespace GraphQLTransportWS

open System
open System.Text.Json
open System.Text.Json.Serialization

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