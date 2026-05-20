module CodingAgent.LlmClientTests

open Xunit
open CodingAgent

let mockConfig =
    { apiKey = "test-key"
      model = "gpt-4o"
      endpoint = "https://api.example.com/v1/chat/completions" }

let emptyTools: ToolDef array = [||]

let validChatResponseJson =
    """{"id":"chatcmpl-123","choices":[{"index":0,"message":{"role":"assistant","content":"Hello!","tool_calls":null},"finish_reason":"stop"}]}"""

let makeSuccessResponse body =
    let response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
    response.Content <- new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json")
    response

let makeErrorResponse statusCode reason body =
    let response = new System.Net.Http.HttpResponseMessage(statusCode)
    response.ReasonPhrase <- reason
    response.Content <- new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json")
    response

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

[<Fact>]
let ``sendChatRequest returns Ok with parsed response on success`` () =
    task {
        let mockClient =
            fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

        let messages = [ LlmClient.userMessage "Hello" ]
        let! result = LlmClient.sendChatRequest mockClient mockConfig emptyTools messages

        match result with
        | Ok response ->
            Assert.Equal("chatcmpl-123", response.id)
            Assert.Equal(1, response.choices.Length)
            Assert.Equal("assistant", response.choices.[0].message.role)
            Assert.Equal("Hello!", response.choices.[0].message.content)
        | Error err -> Assert.Fail(sprintf "Expected Ok but got Error: %s" err)
    }

[<Fact>]
let ``sendChatRequest sends request body containing model and messages`` () =
    task {
        let mutable capturedJson = ""

        let mockClient =
            fun json ->
                capturedJson <- json
                System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

        let messages = [ LlmClient.userMessage "test message" ]
        let! _ = LlmClient.sendChatRequest mockClient mockConfig emptyTools messages
        Assert.Contains("gpt-4o", capturedJson)
        Assert.Contains("test message", capturedJson)
        Assert.Contains("user", capturedJson)
    }

[<Fact>]
let ``sendChatRequest returns Error with status info on non-success HTTP response`` () =
    task {
        let mockClient =
            fun _json ->
                System.Threading.Tasks.Task.FromResult(
                    makeErrorResponse
                        System.Net.HttpStatusCode.InternalServerError
                        "Internal Server Error"
                        "{\"error\":\"something went wrong\"}"
                )

        let messages = [ LlmClient.userMessage "Hello" ]
        let! result = LlmClient.sendChatRequest mockClient mockConfig emptyTools messages

        match result with
        | Error errMsg ->
            Assert.Contains("API Error:", errMsg)
            Assert.Contains("500", errMsg)
            Assert.Contains("Internal Server Error", errMsg)
            Assert.Contains("something went wrong", errMsg)
        | Ok _ -> Assert.Fail("Expected Error but got Ok")
    }

[<Fact>]
let ``sendChatRequest returns Error with status 401 and reason phrase`` () =
    task {
        let mockClient =
            fun _json ->
                System.Threading.Tasks.Task.FromResult(
                    makeErrorResponse
                        System.Net.HttpStatusCode.Unauthorized
                        "Unauthorized"
                        "{\"error\":\"invalid api key\"}"
                )

        let messages = [ LlmClient.userMessage "Hello" ]
        let! result = LlmClient.sendChatRequest mockClient mockConfig emptyTools messages

        match result with
        | Error errMsg ->
            Assert.Contains("API Error:", errMsg)
            Assert.Contains("401", errMsg)
            Assert.Contains("Unauthorized", errMsg)
        | Ok _ -> Assert.Fail("Expected Error but got Ok")
    }

[<Fact>]
let ``sendChatRequest returns Error on invalid JSON deserialization`` () =
    task {
        let mockClient =
            fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse "this is not valid json at all!!!")

        let messages = [ LlmClient.userMessage "Hello" ]
        let! result = LlmClient.sendChatRequest mockClient mockConfig emptyTools messages

        match result with
        | Error errMsg ->
            Assert.Contains("Failed to deserialize response:", errMsg)
            Assert.Contains("this is not valid json at all!!!", errMsg)
        | Ok _ -> Assert.Fail("Expected Error but got Ok")
    }

[<Fact>]
let ``sendChatRequest returns Error on empty response body deserialization`` () =
    task {
        let mockClient =
            fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse "")

        let messages = [ LlmClient.userMessage "Hello" ]
        let! result = LlmClient.sendChatRequest mockClient mockConfig emptyTools messages

        match result with
        | Error errMsg -> Assert.Contains("Failed to deserialize response:", errMsg)
        | Ok _ -> Assert.Fail("Expected Error but got Ok")
    }

[<Fact>]
let ``sendChatRequest returns Error on HTTP request exception`` () =
    task {
        let mockClient =
            fun _json ->
                let tcs =
                    System.Threading.Tasks.TaskCompletionSource<System.Net.Http.HttpResponseMessage>()

                tcs.SetException(System.Net.Http.HttpRequestException("Connection refused"))
                tcs.Task

        let messages = [ LlmClient.userMessage "Hello" ]
        let! result = LlmClient.sendChatRequest mockClient mockConfig emptyTools messages

        match result with
        | Error errMsg ->
            Assert.Contains("HTTP request failed:", errMsg)
            Assert.Contains("Connection refused", errMsg)
        | Ok _ -> Assert.Fail("Expected Error but got Ok")
    }

[<Fact>]
let ``sendChatRequest returns Error on generic exception`` () =
    task {
        let mockClient =
            fun _json ->
                let tcs =
                    System.Threading.Tasks.TaskCompletionSource<System.Net.Http.HttpResponseMessage>()

                tcs.SetException(System.TimeoutException("Request timed out"))
                tcs.Task

        let messages = [ LlmClient.userMessage "Hello" ]
        let! result = LlmClient.sendChatRequest mockClient mockConfig emptyTools messages

        match result with
        | Error errMsg ->
            Assert.Contains("HTTP request failed:", errMsg)
            Assert.Contains("Request timed out", errMsg)
        | Ok _ -> Assert.Fail("Expected Error but got Ok")
    }

[<Fact>]
let ``sendChatRequest works with multiple messages in the list`` () =
    task {
        let mockClient =
            fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

        let messages =
            [ LlmClient.systemMessage "You are helpful"
              LlmClient.userMessage "First message"
              LlmClient.assistantMessage "First response"
              LlmClient.userMessage "Second message" ]

        let! result = LlmClient.sendChatRequest mockClient mockConfig emptyTools messages

        match result with
        | Ok response -> Assert.Equal("chatcmpl-123", response.id)
        | Error err -> Assert.Fail(sprintf "Expected Ok but got Error: %s" err)
    }

[<Fact>]
let ``sendChatRequest works with tool definitions`` () =
    task {
        let mockClient =
            fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

        let tools =
            [| { ``type`` = "function"
                 ``function`` =
                   { name = "read_file"
                     description = "Read a file"
                     parameters =
                       {| ``type`` = "object"
                          properties = {| |} |} } } |]

        let messages = [ LlmClient.userMessage "Read the file" ]
        let! result = LlmClient.sendChatRequest mockClient mockConfig tools messages

        match result with
        | Ok response -> Assert.Equal("chatcmpl-123", response.id)
        | Error err -> Assert.Fail(sprintf "Expected Ok but got Error: %s" err)
    }
