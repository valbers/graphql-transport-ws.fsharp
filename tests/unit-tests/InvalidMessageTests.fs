module InvalidMessageTests

open GraphQLTransportWS.Rop
open UnitTest
open GraphQLTransportWS
open System
open System.Text.Json
open Xunit
open FSharp.Data.GraphQL.Ast

let toClientMessage (theInput : string) =
    let serializerOptions = new JsonSerializerOptions()
    serializerOptions.Converters.Add(new RawMessageConverter())
    JsonSerializer.Deserialize<RawMessage>(theInput, serializerOptions)
    |> MessageMapping.toClientMessage TestSchema.executor

let willResultInInvalidMessage expectedExplanation input =
    try
        let result =
            input
            |> toClientMessage
        match result with
        | Failure msgs ->
            match msgs |> List.head with
            | InvalidMessage (code, explanation) ->
                Assert.Equal(4400, code)
                Assert.Equal(expectedExplanation, explanation)
        | other ->
            Assert.Fail(sprintf "unexpected actual value: '%A'" other)
    with
    | :? InvalidMessageException as ex ->
        Assert.Equal(expectedExplanation, ex.Message)

let willResultInJsonException input =
    try
        input
        |> toClientMessage
        |> ignore
        Assert.Fail("expected that a JsonException would have already been thrown at this point")
    with
    | :? JsonException as ex ->
        Assert.True(true)

[<Fact>]
let ``unknown message type`` () =
    """{
          "type": "connection_start"
       }
    """
    |> willResultInInvalidMessage "invalid type \"connection_start\" specified by client."

[<Fact>]
let ``type not specified`` () =
    """{
          "payload": "hello, let us connect"
       }
    """
    |> willResultInInvalidMessage "message type was not specified by client."

[<Fact>]
let ``no payload in subscribe message`` () =
    """{
          "type": "subscribe",
          "id": "b5d4d2ff-d262-4882-a7b9-d6aec5e4faa6"
       }
    """
    |> willResultInInvalidMessage "payload is required for this message, but none was present."

[<Fact>]
let ``null payload json in subscribe message`` () =
    """{
          "type": "subscribe",
          "id": "b5d4d2ff-d262-4882-a7b9-d6aec5e4faa6",
          "payload": null
       }
    """
    |> willResultInInvalidMessage "was expecting a value for property \"payload\""

[<Fact>]
let ``payload type of number in subscribe message`` () =
    """{
        "type": "subscribe",
        "id": "b5d4d2ff-d262-4882-a7b9-d6aec5e4faa6",
        "payload": 42
    }
    """
    |> willResultInInvalidMessage "payload is a \"Number\", which is not supported"

[<Fact>]
let ``payload type of number in connection_init message is not supported`` () =
    """{
        "type": "connection_init",
        "payload": 42
    }
    """
    |> willResultInInvalidMessage "payload is a \"Number\", which is not supported"

[<Fact>]
let ``no id in subscribe message`` () =
    """{
          "type": "subscribe",
          "payload": {
              "query": "subscription { watchMoon(id: \"1\") { id name isMoon } }"
          }
       }
    """
    |> willResultInInvalidMessage "property \"id\" is required for this message but was not present."

[<Fact>]
let ``subscribe payload format wrongly used in connection_init`` () =
    """{
          "type": "connection_init",
          "payload": {
            "query": "subscription { watchMoon(id: \"1\") { id name isMoon } }"
          }
       }
    """
    |> willResultInInvalidMessage "for this message, payload was expected to be an optional string, but it was a \"subscribe\" payload instead."

[<Fact>]
let ``string payload wrongly used in subscribe`` () =
    """{
          "type": "subscribe",
          "id": "b5d4d2ff-d262-4882-a7b9-d6aec5e4faa6",
          "payload": "{\"query\": \"subscription { watchMoon(id: \\\"1\\\") { id name isMoon } }\"}"
       }
    """
    |> willResultInInvalidMessage "for this message, payload was expected to be a \"subscribe\" payload object, but it wasn't."

[<Fact>]
let ``ping payload object is totally unknown`` () =
    """{
          "type": "ping",
          "payload": { "isEmergency": true }
       }
    """
    |> willResultInInvalidMessage "unexpected property \"isEmergency\" in payload object"

[<Fact>]
let ``subscribe payload object is wrongly used in ping``() =
    """{
        "type": "ping",
        "payload": {
        "query": "subscription { watchMoon(id: \"1\") { id name isMoon } }"
        }
    }
    """
    |> willResultInInvalidMessage "for this message, payload was expected to be an optional string, but it was a \"subscribe\" payload instead."

[<Fact>]
let ``id is incorrectly a number in a subscribe message`` () =
    """{
          "type": "subscribe",
          "id": 42,
          "payload": {
            "query": "subscription { watchMoon(id: \"1\") { id name isMoon } }"
          }
       }
    """
    |> willResultInJsonException

[<Fact>]
let ``typo in one of the messages root properties`` () =
    """{
        "typo": "subscribe",
        "id": "b5d4d2ff-d262-4882-a7b9-d6aec5e4faa6",
        "payload": {
        "query": "subscription { watchMoon(id: \"1\") { id name isMoon } }"
        }
    }
    """
    |> willResultInInvalidMessage "unknown property \"typo\""

[<Fact>]
let ``complete message without an id`` () =
    """{
        "type": "complete"
       }
    """
    |> willResultInInvalidMessage "property \"id\" is required for this message but was not present."

[<Fact>]
let ``complete message with a null id`` () =
    """{
        "type": "complete",
        "id": null
       }
    """
    |> willResultInInvalidMessage "property \"id\" is required for this message but was not present."





