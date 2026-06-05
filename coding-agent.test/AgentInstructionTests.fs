module CodingAgent.AgentInstructionTests

open Xunit
open CodingAgent
open TestHelpers

let validChatResponseJson =
    """{"id":"chatcmpl-123","choices":[{"index":0,"message":{"role":"assistant","content":"Hello!"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}"""

[<Fact>]
let ``processResponse returns Stop for assistant message without tool calls`` () =
    task {
        let responseMsg: LlmClient.ResponseMessage =
            { role = "assistant"
              content = "I have completed the task."
              tool_calls = null }

        let! action = AgentInstruction.processResponse mockAgentConfig [] responseMsg

        match action with
        | AgentInstruction.Stop(content, nextMessages) ->
            Assert.Equal("I have completed the task.", content)
            let msg = Assert.Single nextMessages
            Assert.Equal("assistant", msg.role)
            Assert.Equal("I have completed the task.", msg.content)
        | AgentInstruction.Continue _ -> Assert.Fail "Expected Stop, but got Continue"
    }

[<Fact>]
let ``processResponse returns Stop when tool_calls is empty array`` () =
    task {
        let responseMsg: LlmClient.ResponseMessage =
            { role = "assistant"
              content = "Done."
              tool_calls = [||] }

        let! action = AgentInstruction.processResponse mockAgentConfig [] responseMsg

        match action with
        | AgentInstruction.Stop(content, _) -> Assert.Equal("Done.", content)
        | AgentInstruction.Continue _ -> Assert.Fail "Expected Stop, but got Continue"
    }

[<Fact>]
let ``processResponse appends assistant message to existing messages`` () =
    task {
        let responseMsg: LlmClient.ResponseMessage =
            { role = "assistant"
              content = "Hi there."
              tool_calls = null }

        let existingMsg = LlmClient.userMessage "Hello"
        let! action = AgentInstruction.processResponse mockAgentConfig [ existingMsg ] responseMsg

        match action with
        | AgentInstruction.Stop(_, msgs) ->
            Assert.Equal(2, msgs.Length)
            Assert.Equal("user", msgs.[0].role)
            Assert.Equal("assistant", msgs.[1].role)
        | AgentInstruction.Continue _ -> Assert.Fail "Expected Stop, but got Continue"
    }

[<Fact>]
let ``processResponse returns Continue for assistant message with tool calls`` () =
    task {
        let mutable output = []

        let config =
            { mockAgentConfig with
                writeLine = fun s -> output <- output @ [ s ]
                confirmToolCall = fun _ _ -> true
                tools =
                    { mockAgentConfig.tools with
                        readFile = fun _ -> Ok "file content" } }

        let toolCall: LlmClient.ToolCall =
            { id = "call_ok"
              ``type`` = "function"
              ``function`` =
                { name = "read_file"
                  arguments = "{\"file_path\": \"test.txt\"}" } }

        let responseMsg: LlmClient.ResponseMessage =
            { role = "assistant"
              content = null
              tool_calls = [| toolCall |] }

        let! action = AgentInstruction.processResponse config [] responseMsg

        match action with
        | AgentInstruction.Continue nextMessages ->
            Assert.True(output |> List.exists (fun s -> s.Contains "✅ [Success]"))
            Assert.Equal(2, nextMessages.Length)
            let assistantMsg = nextMessages.[0]
            Assert.Equal("assistant", assistantMsg.role)
            Assert.Equal(1, assistantMsg.tool_calls.Length)
            let resultMsg = nextMessages.[1]
            Assert.Equal("tool", resultMsg.role)
            Assert.Equal("call_ok", resultMsg.tool_call_id)
            Assert.Contains("file content", resultMsg.content)
        | AgentInstruction.Stop _ -> Assert.Fail "Expected Continue, but got Stop"
    }

[<Fact>]
let ``processResponse writes failure message when tool call returns Error`` () =
    task {
        let mutable output = []

        let config =
            { mockAgentConfig with
                writeLine = fun s -> output <- output @ [ s ]
                confirmToolCall = fun _ _ -> true
                tools =
                    { mockAgentConfig.tools with
                        readFile = fun _ -> Error "Something went wrong" } }

        let toolCall: LlmClient.ToolCall =
            { id = "call_fail"
              ``type`` = "function"
              ``function`` =
                { name = "read_file"
                  arguments = "{\"file_path\": \"bad.txt\"}" } }

        let responseMsg: LlmClient.ResponseMessage =
            { role = "assistant"
              content = null
              tool_calls = [| toolCall |] }

        let! action = AgentInstruction.processResponse config [] responseMsg

        match action with
        | AgentInstruction.Continue nextMessages ->
            Assert.True(output |> List.exists (fun s -> s.Contains "❌ [Failure]"))
            let toolResult = nextMessages.[1]
            Assert.Equal("tool", toolResult.role)
            Assert.Contains("Something went wrong", toolResult.content)
        | AgentInstruction.Stop _ -> Assert.Fail "Expected Continue, got Stop"
    }

[<Fact>]
let ``accumulateUsage adds tokens to state`` () =
    let msgs = [ LlmClient.userMessage "test" ]

    let state: AgentInstruction.LoopState =
        { messages = msgs
          promptTokens = 10
          completionTokens = 5
          iterationCount = 2
          result = AgentInstruction.InProgress }

    let usage: LlmClient.Usage =
        { prompt_tokens = 20
          completion_tokens = 8
          total_tokens = 28 }

    let result = AgentInstruction.accumulateUsage state usage
    Assert.Equal(30, result.promptTokens)
    Assert.Equal(13, result.completionTokens)
    Assert.Equal<LlmClient.ChatMessage list>(msgs, result.messages)
    Assert.Equal(2, result.iterationCount)
    Assert.Equal(AgentInstruction.InProgress, result.result)

[<Fact>]
let ``accumulateUsage with zero usage returns same token counts`` () =
    let state: AgentInstruction.LoopState =
        { messages = []
          promptTokens = 10
          completionTokens = 5
          iterationCount = 0
          result = AgentInstruction.InProgress }

    let usage: LlmClient.Usage =
        { prompt_tokens = 0
          completion_tokens = 0
          total_tokens = 0 }

    let result = AgentInstruction.accumulateUsage state usage
    Assert.Equal(10, result.promptTokens)
    Assert.Equal(5, result.completionTokens)

[<Fact>]
let ``processResponseResult returns Completed when finish_reason is stop`` () =
    task {
        let state: AgentInstruction.LoopState =
            { messages = [ LlmClient.userMessage "Hello" ]
              promptTokens = 10
              completionTokens = 5
              iterationCount = 0
              result = AgentInstruction.InProgress }

        let response: LlmClient.ChatResponse =
            { id = "chatcmpl-123"
              choices =
                [| { index = 0
                     message =
                       { role = "assistant"
                         content = "Done!"
                         tool_calls = null }
                     finish_reason = "stop" } |]
              usage =
                { prompt_tokens = 8
                  completion_tokens = 4
                  total_tokens = 12 } }

        let! newState = AgentInstruction.processResponseResult mockAgentConfig state (Ok response)
        Assert.Empty newState.messages

        match newState.result with
        | AgentInstruction.Completed(content, nextMessages, pt, ct) ->
            Assert.Equal("Done!", content)
            Assert.Equal(2, nextMessages.Length)
            Assert.Equal("user", nextMessages.[0].role)
            Assert.Contains("Hello", nextMessages.[0].content)
            Assert.Equal("assistant", nextMessages.[1].role)
            Assert.Contains("Done!", nextMessages.[1].content)
            Assert.Equal(18, pt)
            Assert.Equal(9, ct)
        | _ -> Assert.Fail "Expected Completed result"
    }

[<Fact>]
let ``processResponseResult returns InProgress for tool_call finish_reason`` () =
    task {
        let state: AgentInstruction.LoopState =
            { messages = [ LlmClient.userMessage "Hello" ]
              promptTokens = 10
              completionTokens = 5
              iterationCount = 1
              result = AgentInstruction.InProgress }

        let toolCall: LlmClient.ToolCall =
            { id = "call_1"
              ``type`` = "function"
              ``function`` =
                { name = "read_file"
                  arguments = "{\"file_path\":\"test.txt\"}" } }

        let response: LlmClient.ChatResponse =
            { id = "chatcmpl-123"
              choices =
                [| { index = 0
                     message =
                       { role = "assistant"
                         content = null
                         tool_calls = [| toolCall |] }
                     finish_reason = "tool_calls" } |]
              usage =
                { prompt_tokens = 8
                  completion_tokens = 4
                  total_tokens = 12 } }

        let! newState = AgentInstruction.processResponseResult mockAgentConfig state (Ok response)
        Assert.Equal(3, newState.messages.Length)
        Assert.Equal(2, newState.iterationCount)
        Assert.Equal(18, newState.promptTokens)
        Assert.Equal(9, newState.completionTokens)

        match newState.result with
        | AgentInstruction.InProgress -> ()
        | _ -> Assert.Fail "Expected InProgress"
    }

[<Fact>]
let ``processResponseResult returns Failed when response is Error`` () =
    task {
        let state: AgentInstruction.LoopState =
            { messages = [ LlmClient.userMessage "Hello" ]
              promptTokens = 10
              completionTokens = 5
              iterationCount = 0
              result = AgentInstruction.InProgress }

        let! newState = AgentInstruction.processResponseResult mockAgentConfig state (Error "Network error")
        Assert.Empty newState.messages

        match newState.result with
        | AgentInstruction.Failed(err, p, c) ->
            Assert.Equal("Network error", err)
            Assert.Equal(10, p)
            Assert.Equal(5, c)
        | _ -> Assert.Fail "Expected Failed result"
    }

[<Fact>]
let ``processResponseResult returns Failed when choices array is empty`` () =
    task {
        let state: AgentInstruction.LoopState =
            { messages = [ LlmClient.userMessage "Hello" ]
              promptTokens = 10
              completionTokens = 5
              iterationCount = 0
              result = AgentInstruction.InProgress }

        let response: LlmClient.ChatResponse =
            { id = "chatcmpl-empty"
              choices = [||]
              usage =
                { prompt_tokens = 8
                  completion_tokens = 4
                  total_tokens = 12 } }

        let! newState = AgentInstruction.processResponseResult mockAgentConfig state (Ok response)
        Assert.Empty newState.messages

        match newState.result with
        | AgentInstruction.Failed(err, p, c) ->
            Assert.Contains("no choices", err)
            Assert.Equal(18, p)
            Assert.Equal(9, c)
        | _ -> Assert.Fail "Expected Failed result"
    }

[<Fact>]
let ``processResponseResult preserves previous token counts when usage is null`` () =
    task {
        let state: AgentInstruction.LoopState =
            { messages = [ LlmClient.userMessage "Hello" ]
              promptTokens = 5
              completionTokens = 3
              iterationCount = 0
              result = AgentInstruction.InProgress }

        let toolCall: LlmClient.ToolCall =
            { id = "call_1"
              ``type`` = "function"
              ``function`` =
                { name = "read_file"
                  arguments = "{\"file_path\":\"test.txt\"}" } }

        let response: LlmClient.ChatResponse =
            { id = "chatcmpl-nousage"
              choices =
                [| { index = 0
                     message =
                       { role = "assistant"
                         content = null
                         tool_calls = [| toolCall |] }
                     finish_reason = "tool_calls" } |]
              usage = null }

        let! newState = AgentInstruction.processResponseResult mockAgentConfig state (Ok response)
        Assert.Equal(5, newState.promptTokens)
        Assert.Equal(3, newState.completionTokens)

        match newState.result with
        | AgentInstruction.InProgress -> ()
        | _ -> Assert.Fail "Expected InProgress"
    }

[<Fact>]
let ``processResponseResult returns Failed when maxToolCallIterations is exceeded`` () =
    task {
        let mutable output = []

        let config =
            { mockAgentConfig with
                maxToolCallIterations = 2
                writeLine = fun s -> output <- output @ [ s ]
                confirmToolCall = fun _ _ -> true
                tools =
                    { mockAgentConfig.tools with
                        readFile = fun _ -> Ok "content" } }

        let state: AgentInstruction.LoopState =
            { messages = [ LlmClient.userMessage "Hello" ]
              promptTokens = 10
              completionTokens = 5
              iterationCount = 1
              result = AgentInstruction.InProgress }

        let toolCall: LlmClient.ToolCall =
            { id = "call_1"
              ``type`` = "function"
              ``function`` =
                { name = "read_file"
                  arguments = "{\"file_path\": \"test.txt\"}" } }

        let response: LlmClient.ChatResponse =
            { id = "chatcmpl-123"
              choices =
                [| { index = 0
                     message =
                       { role = "assistant"
                         content = null
                         tool_calls = [| toolCall |] }
                     finish_reason = "tool_calls" } |]
              usage =
                { prompt_tokens = 8
                  completion_tokens = 4
                  total_tokens = 12 } }

        let! newState = AgentInstruction.processResponseResult config state (Ok response)
        Assert.Empty newState.messages

        match newState.result with
        | AgentInstruction.Failed(err, p, c) ->
            Assert.Contains("Exceeded maximum tool call iterations", err)
            Assert.Contains("2", err)
            Assert.Equal(18, p)
            Assert.Equal(9, c)
        | _ -> Assert.Fail "Expected Failed result"

        Assert.True(output |> List.exists (fun s -> s.Contains "Exceeded 2 tool call iterations"))
    }

[<Fact>]
let ``instructionLoop accumulates prompt and completion token counts across multiple iterations`` () =
    task {
        let toolCallJson =
            """{"id":"chatcmpl-1","choices":[{"index":0,"message":{"role":"assistant","content":"","tool_calls":[{"id":"call_1","type":"function","function":{"name":"read_file","arguments":"{\"file_path\":\"/nonexistent.txt\"}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":10,"completion_tokens":3,"total_tokens":13}}"""

        let finalJson =
            """{"id":"chatcmpl-2","choices":[{"index":0,"message":{"role":"assistant","content":"All done!"},"finish_reason":"stop"}],"usage":{"prompt_tokens":20,"completion_tokens":8,"total_tokens":28}}"""

        let mutable callCount = 0

        let mockClient =
            fun _json ->
                callCount <- callCount + 1
                let json = if callCount = 1 then toolCallJson else finalJson
                System.Threading.Tasks.Task.FromResult(makeSuccessResponse json)

        let messages = [ LlmClient.userMessage "Do something" ]

        let state: AgentInstruction.LoopState =
            { messages = messages
              promptTokens = 0
              completionTokens = 0
              iterationCount = 0
              result = AgentInstruction.InProgress }

        let! result = AgentInstruction.instructionLoop mockAgentConfig mockClient state

        match result with
        | Ok(content, updatedMessages, pTokens, cTokens) ->
            Assert.Equal("All done!", content)
            Assert.Equal(4, updatedMessages.Length)
            Assert.Equal(10 + 20, pTokens)
            Assert.Equal(3 + 8, cTokens)
        | Error(err, _, _) -> Assert.Fail(sprintf "Expected Ok but got Error: %s" err)
    }

[<Fact>]
let ``instructionLoop returns Error when API call fails`` () =
    task {
        let mockClient =
            fun _json ->
                System.Threading.Tasks.Task.FromResult(
                    makeErrorResponse System.Net.HttpStatusCode.InternalServerError "Server Error" "{}"
                )

        let messages = [ LlmClient.userMessage "Hello" ]

        let state: AgentInstruction.LoopState =
            { messages = messages
              promptTokens = 5
              completionTokens = 3
              iterationCount = 0
              result = AgentInstruction.InProgress }

        let! result = AgentInstruction.instructionLoop mockAgentConfig mockClient state

        match result with
        | Error(errMsg, pTokens, cTokens) ->
            Assert.Contains("API Error:", errMsg)
            Assert.Equal(5, pTokens)
            Assert.Equal(3, cTokens)
        | Ok _ -> Assert.Fail "Expected Error but got Ok"
    }

[<Fact>]
let ``processInstruction prints nothing when response content is blank`` () =
    task {
        let mutable output = []

        let config =
            { mockAgentConfig with
                writeLine = fun s -> output <- output @ [ s ]
                write = ignore }

        let blankContentJson =
            """{"id":"chatcmpl-blank","choices":[{"index":0,"message":{"role":"assistant","content":"   "},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}}"""

        let mockClient =
            fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse blankContentJson)

        let messages = [ LlmClient.userMessage "Hello" ]
        let! resultMsgs, pTokens, cTokens = AgentInstruction.processInstruction config mockClient messages messages
        Assert.False(output |> List.exists (fun s -> s.StartsWith "\n🤖"))
        Assert.Equal(2, resultMsgs.Length)
        Assert.Equal("user", resultMsgs.[0].role)
        Assert.Contains("Hello", resultMsgs.[0].content)
        Assert.Equal("assistant", resultMsgs.[1].role)
        Assert.Contains("   ", resultMsgs.[1].content)
        Assert.Equal(1, pTokens)
        Assert.Equal(2, cTokens)
    }

[<Fact>]
let ``processInstruction prints error message when instructionLoop returns Error`` () =
    task {
        let mutable output = []

        let config =
            { mockAgentConfig with
                writeLine = fun s -> output <- output @ [ s ]
                write = ignore }

        let mockClient =
            fun _json ->
                System.Threading.Tasks.Task.FromResult(
                    makeErrorResponse System.Net.HttpStatusCode.BadRequest "Bad Request" "{}"
                )

        let messages = [ LlmClient.userMessage "Hello" ]
        let! resultMsgs, pTokens, cTokens = AgentInstruction.processInstruction config mockClient messages messages
        Assert.True(output |> List.exists (fun s -> s.Contains "An error occurred"))
        Assert.Equal<LlmClient.ChatMessage list>(messages, resultMsgs)
        Assert.Equal(0, pTokens)
        Assert.Equal(0, cTokens)
    }

[<Fact>]
let ``processInstruction catches exceptions and shows unexpected error`` () =
    task {
        let mutable output = []

        let config =
            { mockAgentConfig with
                writeLine = fun s -> output <- output @ [ s ]
                write = fun _ -> raise (new System.InvalidOperationException "Write failed") }

        let mockClient =
            fun (_json: string) -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

        let messages = [ LlmClient.userMessage "Hello" ]
        let! resultMsgs, pTokens, cTokens = AgentInstruction.processInstruction config mockClient messages messages
        Assert.Equal<LlmClient.ChatMessage list>(messages, resultMsgs)
        Assert.Equal(0, pTokens)
        Assert.Equal(0, cTokens)

        Assert.True(
            output
            |> List.exists (fun s -> s.Contains "unexpected error" && s.Contains "Write failed")
        )
    }
