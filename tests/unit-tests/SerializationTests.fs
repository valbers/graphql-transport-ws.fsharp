module Tests

open GraphQLTransportWS
open System
open System.Text.Json
open Xunit

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
    let result = resultRaw |> GraphQLWsMessageRawMapping.toWebSocketClientMessage

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
    let result = resultRaw |> GraphQLWsMessageRawMapping.toWebSocketClientMessage

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
    let result = resultRaw |> GraphQLWsMessageRawMapping.toWebSocketClientMessage

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
    let result = resultRaw |> GraphQLWsMessageRawMapping.toWebSocketClientMessage

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
    let result = resultRaw |> GraphQLWsMessageRawMapping.toWebSocketClientMessage

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
    let result = resultRaw |> GraphQLWsMessageRawMapping.toWebSocketClientMessage

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
    let result = resultRaw |> GraphQLWsMessageRawMapping.toWebSocketClientMessage

    match result with
    | ClientComplete id ->
        Assert.Equal("65fca2b5-f149-4a70-a055-5123dea4628f", id)
    | other ->
        Assert.Fail(sprintf "unexpected actual value: '%A'" other)
