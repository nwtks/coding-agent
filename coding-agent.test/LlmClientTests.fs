module CodingAgent.LlmClientTests

open Xunit
open CodingAgent

[<Fact>]
let ``userMessage creates a user message correctly`` () =
    let content = "Hello World"
    let msg = LlmClient.userMessage content
    Assert.Equal("user", msg.role)
    Assert.Equal(content, msg.content)
    Assert.Null(msg.name)
    Assert.Null(msg.tool_call_id)
    Assert.Null(msg.tool_calls)

[<Fact>]
let ``systemMessage creates a system message correctly`` () =
    let content = "You are an AI"
    let msg = LlmClient.systemMessage content
    Assert.Equal("system", msg.role)
    Assert.Equal(content, msg.content)
    Assert.Null(msg.name)
    Assert.Null(msg.tool_call_id)
    Assert.Null(msg.tool_calls)

[<Fact>]
let ``assistantMessage creates an assistant message correctly`` () =
    let content = "I can help you with that"
    let msg = LlmClient.assistantMessage content
    Assert.Equal("assistant", msg.role)
    Assert.Equal(content, msg.content)
    Assert.Null(msg.name)
    Assert.Null(msg.tool_call_id)
    Assert.Null(msg.tool_calls)

[<Fact>]
let ``toolCallMessage creates an assistant message with tool calls`` () =
    let toolCall =
        { id = "call_123"
          ``type`` = "function"
          ``function`` = { name = "read_file"; arguments = "{}" } }

    let msg = LlmClient.toolCallMessage [| toolCall |]
    Assert.Equal("assistant", msg.role)
    Assert.Equal("", msg.content)
    Assert.NotNull(msg.tool_calls)
    Assert.Equal(1, msg.tool_calls.Length)
    Assert.Equal("call_123", msg.tool_calls.[0].id)

[<Fact>]
let ``toolResultMessage creates a tool message correctly`` () =
    let toolCallId = "call_123"
    let name = "read_file"
    let content = "file contents here"
    let msg = LlmClient.toolResultMessage toolCallId name content
    Assert.Equal("tool", msg.role)
    Assert.Equal(name, msg.name)
    Assert.Equal(toolCallId, msg.tool_call_id)
    Assert.Equal(content, msg.content)
    Assert.Null(msg.tool_calls)
