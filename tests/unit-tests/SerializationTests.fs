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
    serializerOptions.Converters.Add(new WebSocketClientMessageConverter())

    let input = "{\"type\":\"connection_init\"}"

    let result = JsonSerializer.Deserialize<WebSocketClientMessage>(input, serializerOptions)

    Assert.Equivalent(ConnectionInit None, result, strict = true)

[<Fact>]
let ``Deserializes ClientPing correctly`` () =
    let serializerOptions = new JsonSerializerOptions()
    serializerOptions.Converters.Add(new WebSocketClientMessageConverter())

    let input = "{\"type\":\"ping\"}"
    
    let result = JsonSerializer.Deserialize<WebSocketClientMessage>(input, serializerOptions)

    Assert.Equivalent(ClientPing None, result, strict = true)

[<Fact>]
let ``Deserializes ClientPong correctly`` () =
    let serializerOptions = new JsonSerializerOptions()
    serializerOptions.Converters.Add(new WebSocketClientMessageConverter())

    let input = "{\"type\":\"pong\"}"
    
    let result = JsonSerializer.Deserialize<WebSocketClientMessage>(input, serializerOptions)

    Assert.Equivalent(ClientPong None, result, strict = true)
