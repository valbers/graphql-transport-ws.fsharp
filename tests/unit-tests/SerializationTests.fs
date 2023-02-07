module Tests

open UnitTest
open GraphQLTransportWS
open System
open System.Text.Json
open Xunit
open FSharp.Data.GraphQL.Ast

[<Fact>]
let ``Serializes ServerPing correctly`` () =
    let jsonOptions = new JsonSerializerOptions()
    jsonOptions.Converters.Add(new WebSocketServerMessageConverter())
    
    let input: WebSocketServerMessage =
        ServerPing (Some "Peekaboo!")

    let result = JsonSerializer.Serialize(input, jsonOptions)

    Assert.Equal("{\"type\":\"ping\",\"payload\":\"Peekaboo!\"}", result)

[<Fact>]
let ``Serializes Error correctly`` () =
    let jsonOptions = new JsonSerializerOptions()
    jsonOptions.Converters.Add(new WebSocketServerMessageConverter())
    
    let input: WebSocketServerMessage =
        Error ("myId", [ "An error ocurred during GraphQL execution" ])

    let result = JsonSerializer.Serialize(input, jsonOptions)

    Assert.Equal("{\"type\":\"error\",\"id\":\"myId\",\"payload\":[{\"message\":\"An error ocurred during GraphQL execution\"}]}", result)

[<Fact>]
let ``Deserializes ConnectionInit correctly`` () =
    let serializerOptions = new JsonSerializerOptions()
    serializerOptions.Converters.Add(new GraphQLWsMessageConverter())

    let input = "{\"type\":\"connection_init\"}"

    let resultRaw = JsonSerializer.Deserialize<GraphQLWsMessageRaw>(input, serializerOptions)
    let result =
        resultRaw
        |> GraphQLWsMessageRawMapping.toWebSocketClientMessage (TestSchema.executor)

    match result with
    | ConnectionInit None -> () // <-- expected
    | other ->
        Assert.Fail(sprintf "unexpected actual value: '%A'" other)

[<Fact>]
let ``Deserializes ConnectionInit with payload correctly`` () =
    let serializerOptions = new JsonSerializerOptions()
    serializerOptions.Converters.Add(new GraphQLWsMessageConverter())

    let input = "{\"type\":\"connection_init\", \"payload\":\"hello\"}"

    let resultRaw = JsonSerializer.Deserialize<GraphQLWsMessageRaw>(input, serializerOptions)
    let result =
        resultRaw
        |> GraphQLWsMessageRawMapping.toWebSocketClientMessage (TestSchema.executor)

    match result with
    | ConnectionInit (Some "hello") -> () // <-- expected
    | other ->
        Assert.Fail(sprintf "unexpected actual value: '%A'" other)

[<Fact>]
let ``Deserializes ClientPing correctly`` () =
    let serializerOptions = new JsonSerializerOptions()
    serializerOptions.Converters.Add(new GraphQLWsMessageConverter())

    let input = "{\"type\":\"ping\"}"

    let resultRaw = JsonSerializer.Deserialize<GraphQLWsMessageRaw>(input, serializerOptions)
    let result =
        resultRaw
        |> GraphQLWsMessageRawMapping.toWebSocketClientMessage (TestSchema.executor)

    match result with
    | ClientPing None -> () // <-- expected
    | other ->
        Assert.Fail(sprintf "unexpected actual value '%A'" other)

[<Fact>]
let ``Deserializes ClientPing with payload correctly`` () =
    let serializerOptions = new JsonSerializerOptions()
    serializerOptions.Converters.Add(new GraphQLWsMessageConverter())

    let input = "{\"type\":\"ping\", \"payload\":\"ping!\"}"

    let resultRaw = JsonSerializer.Deserialize<GraphQLWsMessageRaw>(input, serializerOptions)
    let result =
        resultRaw
        |> GraphQLWsMessageRawMapping.toWebSocketClientMessage (TestSchema.executor)

    match result with
    | ClientPing (Some "ping!") -> () // <-- expected
    | other ->
        Assert.Fail(sprintf "unexpected actual value '%A" other)

[<Fact>]
let ``Deserializes ClientPong correctly`` () =
    let serializerOptions = new JsonSerializerOptions()
    serializerOptions.Converters.Add(new GraphQLWsMessageConverter())

    let input = "{\"type\":\"pong\"}"
    
    let resultRaw = JsonSerializer.Deserialize<GraphQLWsMessageRaw>(input, serializerOptions)
    let result =
        resultRaw
        |> GraphQLWsMessageRawMapping.toWebSocketClientMessage (TestSchema.executor)

    match result with
    | ClientPong None -> () // <-- expected
    | other ->
        Assert.Fail(sprintf "unexpected actual value: '%A'" other)

[<Fact>]
let ``Deserializes ClientPong with payload correctly`` () =
    let serializerOptions = new JsonSerializerOptions()
    serializerOptions.Converters.Add(new GraphQLWsMessageConverter())

    let input = "{\"type\":\"pong\", \"payload\": \"pong!\"}"
    
    let resultRaw = JsonSerializer.Deserialize<GraphQLWsMessageRaw>(input, serializerOptions)
    let result =
        resultRaw
        |> GraphQLWsMessageRawMapping.toWebSocketClientMessage (TestSchema.executor)

    match result with
    | ClientPong (Some "pong!") -> () // <-- expected
    | other ->
        Assert.Fail(sprintf "unexpected actual value: '%A'" other)

[<Fact>]
let ``Deserializes ClientComplete correctly``() =
    let serializerOptions = new JsonSerializerOptions()
    serializerOptions.Converters.Add(new GraphQLWsMessageConverter())

    let input = "{\"id\": \"65fca2b5-f149-4a70-a055-5123dea4628f\", \"type\":\"complete\"}"

    let resultRaw = JsonSerializer.Deserialize<GraphQLWsMessageRaw>(input, serializerOptions)
    let result =
        resultRaw
        |> GraphQLWsMessageRawMapping.toWebSocketClientMessage (TestSchema.executor)

    match result with
    | ClientComplete id ->
        Assert.Equal("65fca2b5-f149-4a70-a055-5123dea4628f", id)
    | other ->
        Assert.Fail(sprintf "unexpected actual value: '%A'" other)

[<Fact>]
let ``Deserializes client subscription correctly`` () =
    let serializerOptions = new JsonSerializerOptions()
    serializerOptions.Converters.Add(new GraphQLWsMessageConverter())

    let input =
        """{
            "id": "b5d4d2ff-d262-4882-a7b9-d6aec5e4faa6",
            "type": "subscribe",
            "payload" : {
                "query": "subscription { watchMoon(id: \"1\") { id name isMoon } }"
            }
           }
        """

    let resultRaw = JsonSerializer.Deserialize<GraphQLWsMessageRaw>(input, serializerOptions)
    let result =
        resultRaw
        |> GraphQLWsMessageRawMapping.toWebSocketClientMessage (TestSchema.executor)

    match result with
    | Subscribe (id, payload) ->
        Assert.Equal("b5d4d2ff-d262-4882-a7b9-d6aec5e4faa6", id)
        Assert.Equal(1, payload.ExecutionPlan.Operation.SelectionSet.Length)
        let watchMoonSelection = payload.ExecutionPlan.Operation.SelectionSet |> List.head
        match watchMoonSelection with
        | Field watchMoonField ->
            Assert.Equal("watchMoon", watchMoonField.Name)
            Assert.Equal(1, watchMoonField.Arguments.Length)
            let watchMoonFieldArg = watchMoonField.Arguments |> List.head
            Assert.Equal("id", watchMoonFieldArg.Name)
            match watchMoonFieldArg.Value with
            | StringValue theValue ->
                Assert.Equal("1", theValue)
            | other ->
                Assert.Fail(sprintf "expected arg to be a StringValue, but it was: %A" other)
            Assert.Equal(3, watchMoonField.SelectionSet.Length)
            match watchMoonField.SelectionSet.[0] with
            | Field firstField ->
                Assert.Equal("id", firstField.Name)
            | other ->
                Assert.Fail(sprintf "expected field to be a Field, but it was: %A" other)
            match watchMoonField.SelectionSet.[1] with
            | Field secondField ->
                Assert.Equal("name", secondField.Name)
            | other ->
                Assert.Fail(sprintf "expected field to be a Field, but it was: %A" other)
            match watchMoonField.SelectionSet.[2] with
            | Field thirdField ->
                Assert.Equal("isMoon", thirdField.Name)
            | other ->
                Assert.Fail(sprintf "expected field to be a Field, but it was: %A" other)
        | somethingElse ->
            Assert.Fail(sprintf "expected it to be a field, but it was: %A" somethingElse)
    | other ->
        Assert.Fail(sprintf "unexpected actual value: '%A" other)