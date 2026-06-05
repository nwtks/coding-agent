module CodingAgent.LlmClientTests

open Xunit
open CodingAgent
open TestHelpers

let mockConfig =
    { apiKey = "test-key"
      model = "gpt-4o"
      endpoint = "https://api.example.com/v1/chat/completions"
      maxRetries = 0
      timeoutSeconds = 30 }

let mockRetryConfig = { mockConfig with maxRetries = 2 }
let emptyTools: LlmClient.ToolDef array = [||]

let validChatResponseJson =
    """{"id":"chatcmpl-123","choices":[{"index":0,"message":{"role":"assistant","content":"Hello!"},"finish_reason":"stop"}]}"""

[<Fact>]
let ``userMessage creates a user message correctly`` () =
    let content = "Hello World"
    let msg = LlmClient.userMessage content
    Assert.Equal("user", msg.role)
    Assert.Equal(content, msg.content)
    Assert.Null msg.name
    Assert.Null msg.tool_call_id
    Assert.Null msg.tool_calls

[<Fact>]
let ``systemMessage creates a system message correctly`` () =
    let content = "You are an AI"
    let msg = LlmClient.systemMessage content
    Assert.Equal("system", msg.role)
    Assert.Equal(content, msg.content)
    Assert.Null msg.name
    Assert.Null msg.tool_call_id
    Assert.Null msg.tool_calls

[<Fact>]
let ``assistantMessage creates an assistant message correctly`` () =
    let content = "I can help you with that"
    let msg = LlmClient.assistantMessage content
    Assert.Equal("assistant", msg.role)
    Assert.Equal(content, msg.content)
    Assert.Null msg.name
    Assert.Null msg.tool_call_id
    Assert.Null msg.tool_calls

[<Fact>]
let ``toolCallMessage creates an assistant message with tool calls`` () =
    let toolCall: LlmClient.ToolCall =
        { id = "call_123"
          ``type`` = "function"
          ``function`` = { name = "read_file"; arguments = "{}" } }

    let msg = LlmClient.toolCallMessage [| toolCall |]
    Assert.Equal("assistant", msg.role)
    Assert.Equal("", msg.content)
    Assert.NotNull msg.tool_calls
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
    Assert.Null msg.tool_calls

[<Fact>]
let ``sendChatRequest returns Ok with parsed response on success`` () =
    task {
        let mutable capturedJson = ""

        let mockClient =
            fun json ->
                capturedJson <- json
                System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

        let messages =
            [ LlmClient.systemMessage "You are helpful"
              LlmClient.userMessage "First message"
              LlmClient.assistantMessage "First response"
              LlmClient.userMessage "Second message" ]

        let! result = LlmClient.sendChatRequest mockClient mockConfig emptyTools messages
        Assert.Contains("gpt-4o", capturedJson)
        Assert.Contains("Second message", capturedJson)
        Assert.Contains("user", capturedJson)

        match result with
        | Ok response ->
            Assert.Equal("chatcmpl-123", response.id)
            Assert.Equal(1, response.choices.Length)
            Assert.Equal("assistant", response.choices.[0].message.role)
            Assert.Equal("Hello!", response.choices.[0].message.content)
        | Error err -> Assert.Fail(sprintf "Expected Ok but got Error: %s" err)
    }

[<Fact>]
let ``sendChatRequest includes tool definitions in API request`` () =
    task {
        let mockClient =
            fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

        let tools: LlmClient.ToolDef array =
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
        | Ok _ -> Assert.Fail "Expected Error but got Ok"
    }

[<Fact>]
let ``sendChatRequest returns Error with status code and body on HTTP error`` () =
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
        | Ok _ -> Assert.Fail "Expected Error but got Ok"
    }

[<Fact>]
let ``sendChatRequest retries on 429 status and succeeds`` () =
    task {
        let mutable callCount = 0

        let mockClient =
            fun _json ->
                callCount <- callCount + 1

                if callCount <= 1 then
                    System.Threading.Tasks.Task.FromResult(
                        makeErrorResponse (enum<System.Net.HttpStatusCode> 429) "Too Many Requests" "{}"
                    )
                else
                    System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

        let messages = [ LlmClient.userMessage "Hello" ]
        let! result = LlmClient.sendChatRequest mockClient mockRetryConfig emptyTools messages

        match result with
        | Ok response ->
            Assert.Equal("chatcmpl-123", response.id)
            Assert.Equal(2, callCount)
        | Error _ -> Assert.Fail "Expected Ok after retry"
    }

[<Fact>]
let ``sendChatRequest returns error after exhausting retries on 429`` () =
    task {
        let mutable callCount = 0

        let mockClient =
            fun _json ->
                callCount <- callCount + 1

                System.Threading.Tasks.Task.FromResult(
                    makeErrorResponse (enum<System.Net.HttpStatusCode> 429) "Too Many Requests" "{}"
                )

        let messages = [ LlmClient.userMessage "Hello" ]
        let! result = LlmClient.sendChatRequest mockClient mockRetryConfig emptyTools messages

        match result with
        | Error errMsg ->
            Assert.Equal(3, callCount)
            Assert.Contains("429", errMsg)
            Assert.Contains("Too Many Requests", errMsg)
        | Ok _ -> Assert.Fail "Expected Error after exhausting retries"
    }

[<Fact>]
let ``sendChatRequest with maxRetries=0 does not retry on 429`` () =
    task {
        let mutable callCount = 0

        let mockClient =
            fun _json ->
                callCount <- callCount + 1

                System.Threading.Tasks.Task.FromResult(
                    makeErrorResponse (enum<System.Net.HttpStatusCode> 429) "Too Many Requests" "{}"
                )

        let messages = [ LlmClient.userMessage "Hello" ]
        let! result = LlmClient.sendChatRequest mockClient mockConfig emptyTools messages

        match result with
        | Error _ ->
            Assert.Equal(1, callCount)
            ()
        | Ok _ -> Assert.Fail "Expected Error"
    }

[<Fact>]
let ``sendChatRequest returns Error on HTTP request exception`` () =
    task {
        let mockClient =
            fun _json ->
                let tcs =
                    System.Threading.Tasks.TaskCompletionSource<System.Net.Http.HttpResponseMessage>()

                tcs.SetException(System.Net.Http.HttpRequestException "Connection refused")
                tcs.Task

        let messages = [ LlmClient.userMessage "Hello" ]
        let! result = LlmClient.sendChatRequest mockClient mockConfig emptyTools messages

        match result with
        | Error errMsg ->
            Assert.Contains("HTTP request failed:", errMsg)
            Assert.Contains("Connection refused", errMsg)
        | Ok _ -> Assert.Fail "Expected Error but got Ok"
    }

[<Fact>]
let ``sendChatRequest retries on HTTP exception and succeeds`` () =
    task {
        let mutable callCount = 0

        let mockClient =
            fun _json ->
                callCount <- callCount + 1

                if callCount <= 1 then
                    let tcs =
                        System.Threading.Tasks.TaskCompletionSource<System.Net.Http.HttpResponseMessage>()

                    tcs.SetException(System.Net.Http.HttpRequestException "Connection refused")
                    tcs.Task
                else
                    System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

        let messages = [ LlmClient.userMessage "Hello" ]
        let! result = LlmClient.sendChatRequest mockClient mockRetryConfig emptyTools messages

        match result with
        | Ok response ->
            Assert.Equal("chatcmpl-123", response.id)
            Assert.Equal(2, callCount)
        | Error _ -> Assert.Fail "Expected Ok after retry"
    }
