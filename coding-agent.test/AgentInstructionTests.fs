module CodingAgent.Tests.AgentInstructionTests

open Xunit
open CodingAgent
open TestHelpers

[<Theory>]
[<InlineData("success")>]
[<InlineData("error")>]
let ``formatToolResult returns tool message with content or error depending on result`` (scenario: string) =
    let mutable output = []

    let config =
        let cfg = mockAgentConfig ()

        { cfg with
            interactive =
                { cfg.interactive with
                    writeLine = fun s -> output <- output @ [ s ] } }

    let toolCall: LlmClient.ToolCall =
        { id = "call_1"
          ``type`` = "function"
          ``function`` =
            { name = AgentToolCall.ToolName.toString AgentToolCall.ReadFile
              arguments = "{}" } }

    let result =
        match scenario with
        | "success" -> AgentInstruction.formatToolResult config toolCall (Ok "file content")
        | "error" -> AgentInstruction.formatToolResult config toolCall (Error "Something went wrong")
        | _ -> failwith "unknown scenario"

    Assert.Equal("tool", result.role)
    Assert.Equal("call_1", result.tool_call_id)
    Assert.Equal(AgentToolCall.ToolName.toString AgentToolCall.ReadFile, result.name)

    match scenario with
    | "success" ->
        Assert.Equal("file content", result.content)
        Assert.True(output |> List.exists (fun s -> s.Contains "✅"))
    | "error" ->
        Assert.Equal("Something went wrong", result.content)
        Assert.True(output |> List.exists (fun s -> s.Contains "❌"))
    | _ -> failwith "unknown scenario"

[<Theory>]
[<InlineData("success")>]
[<InlineData("error")>]
let ``executeToolCalls returns tool result or error messages for successful and failed calls`` (scenario: string) =
    async {
        let mutable output = []

        let config =
            let cfg = mockAgentConfig ()

            { cfg with
                interactive =
                    { cfg.interactive with
                        writeLine = fun s -> output <- output @ [ s ]
                        confirmToolCall = fun _ _ _ -> true }
                tools =
                    { cfg.tools with
                        readFile =
                            fun _ ->
                                match scenario with
                                | "success" -> Ok "file content"
                                | "error" -> Error "Access denied"
                                | _ -> failwith "unknown scenario" } }

        let toolCall: LlmClient.ToolCall =
            { id = "call_1"
              ``type`` = "function"
              ``function`` =
                { name = AgentToolCall.ToolName.toString AgentToolCall.ReadFile
                  arguments = "{\"file_path\": \"test.txt\"}" } }

        let responseMsg: LlmClient.ResponseMessage =
            { role = "assistant"
              content = null
              tool_calls = [| toolCall |] }

        let! results = AgentInstruction.executeToolCalls config responseMsg

        let msg = Assert.Single results
        Assert.Equal("tool", msg.role)
        Assert.Equal("call_1", msg.tool_call_id)
        Assert.Equal(AgentToolCall.ToolName.toString AgentToolCall.ReadFile, msg.name)

        match scenario with
        | "success" -> Assert.Contains("file content", msg.content)
        | "error" ->
            Assert.Contains("Access denied", msg.content)
            Assert.True(output |> List.exists (fun s -> s.Contains "❌"))
        | _ -> failwith "unknown scenario"
    }
    |> Async.RunSynchronously

[<Fact>]
let ``executeToolCalls processes multiple tool calls in parallel`` () =
    async {
        let mutable callCount = 0

        let config =
            let cfg = mockAgentConfig ()

            { cfg with
                interactive =
                    { cfg.interactive with
                        confirmToolCall = fun _ _ _ -> true }
                tools =
                    { cfg.tools with
                        readFile =
                            fun _ ->
                                callCount <- callCount + 1
                                Ok $"content {callCount}" } }

        let toolCall1: LlmClient.ToolCall =
            { id = "call_1"
              ``type`` = "function"
              ``function`` =
                { name = AgentToolCall.ToolName.toString AgentToolCall.ReadFile
                  arguments = "{\"file_path\": \"a.txt\"}" } }

        let toolCall2: LlmClient.ToolCall =
            { id = "call_2"
              ``type`` = "function"
              ``function`` =
                { name = AgentToolCall.ToolName.toString AgentToolCall.ReadFile
                  arguments = "{\"file_path\": \"b.txt\"}" } }

        let responseMsg: LlmClient.ResponseMessage =
            { role = "assistant"
              content = null
              tool_calls = [| toolCall1; toolCall2 |] }

        let! results = AgentInstruction.executeToolCalls config responseMsg

        Assert.Equal(2, results.Length)
        Assert.True(results |> List.forall (fun m -> m.role = "tool"))
        Assert.Equal("call_1", results.[0].tool_call_id)
        Assert.Equal("call_2", results.[1].tool_call_id)
    }
    |> Async.RunSynchronously

[<Theory>]
[<InlineData("null-calls")>]
[<InlineData("empty-calls")>]
let ``processResponse returns Stop when tool_calls is null or empty`` (scenario: string) =
    async {
        let content =
            if scenario = "null-calls" then
                "I have completed the task."
            else
                "Done."

        let toolCalls =
            match scenario with
            | "null-calls" -> null
            | "empty-calls" -> [||]
            | _ -> failwith "unknown scenario"

        let responseMsg: LlmClient.ResponseMessage =
            { role = "assistant"
              content = content
              tool_calls = toolCalls }

        let! action = AgentInstruction.processResponse (mockAgentConfig ()) [] responseMsg

        match action with
        | AgentInstruction.Stop(actualContent, nextMessages) ->
            Assert.Equal(content, actualContent)

            if scenario = "null-calls" then
                let msg = Assert.Single nextMessages
                Assert.Equal("assistant", msg.role)
                Assert.Equal(content, msg.content)
        | AgentInstruction.Continue _ -> Assert.Fail "Expected Stop, but got Continue"
    }
    |> Async.RunSynchronously

[<Fact>]
let ``processResponse appends new assistant message to existing message list on Stop`` () =
    async {
        let responseMsg: LlmClient.ResponseMessage =
            { role = "assistant"
              content = "Hi there."
              tool_calls = null }

        let existingMsg = LlmClient.userMessage "Hello"
        let! action = AgentInstruction.processResponse (mockAgentConfig ()) [ existingMsg ] responseMsg

        match action with
        | AgentInstruction.Stop(_, msgs) ->
            Assert.Equal(2, msgs.Length)
            Assert.Equal("user", msgs.[0].role)
            Assert.Equal("assistant", msgs.[1].role)
        | AgentInstruction.Continue _ -> Assert.Fail "Expected Stop, but got Continue"
    }
    |> Async.RunSynchronously

[<Fact>]
let ``processResponse returns Continue for assistant message with tool calls`` () =
    async {
        let mutable output = []

        let config =
            let cfg = mockAgentConfig ()

            { cfg with
                interactive =
                    { cfg.interactive with
                        writeLine = fun s -> output <- output @ [ s ]
                        confirmToolCall = fun _ _ _ -> true }
                tools =
                    { cfg.tools with
                        readFile = fun _ -> Ok "file content" } }

        let toolCall: LlmClient.ToolCall =
            { id = "call_ok"
              ``type`` = "function"
              ``function`` =
                { name = AgentToolCall.ToolName.toString AgentToolCall.ReadFile
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
    |> Async.RunSynchronously

[<Fact>]
let ``processResponse writes failure message when tool call returns Error`` () =
    async {
        let mutable output = []

        let config =
            let cfg = mockAgentConfig ()

            { cfg with
                interactive =
                    { cfg.interactive with
                        writeLine = fun s -> output <- output @ [ s ]
                        confirmToolCall = fun _ _ _ -> true }
                tools =
                    { cfg.tools with
                        readFile = fun _ -> Error "Something went wrong" } }

        let toolCall: LlmClient.ToolCall =
            { id = "call_fail"
              ``type`` = "function"
              ``function`` =
                { name = AgentToolCall.ToolName.toString AgentToolCall.ReadFile
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
    |> Async.RunSynchronously

[<Theory>]
[<InlineData(10, 5, 20, 8, 30, 13)>]
[<InlineData(10, 5, 0, 0, 10, 5)>]
let ``accumulateUsage accumulates incoming token counts or preserves existing when zero``
    (startPt: int, startCt: int, incPt: int, incCt: int, expectedPt: int, expectedCt: int)
    =
    let state: AgentInstruction.LoopState =
        { messages = []
          promptTokens = startPt
          completionTokens = startCt
          iterationCount = 0
          result = AgentInstruction.InProgress }

    let usage: LlmClient.Usage =
        { prompt_tokens = incPt
          completion_tokens = incCt
          total_tokens = incPt + incCt }

    let result = AgentInstruction.accumulateUsage state usage
    Assert.Equal(expectedPt, result.promptTokens)
    Assert.Equal(expectedCt, result.completionTokens)

[<Theory>]
[<InlineData(10, 20, 15, 23)>]
[<InlineData(0, 0, 5, 3)>]
let ``accumulateUsageIfPresent adds usage when present or preserves counts when null``
    (pt: int, ct: int, expectedPt: int, expectedCt: int)
    =
    let state: AgentInstruction.LoopState =
        { messages = []
          promptTokens = 5
          completionTokens = 3
          iterationCount = 0
          result = AgentInstruction.InProgress }

    let response: LlmClient.ChatResponse =
        { id = "1"
          choices = [||]
          usage =
            if pt = 0 && ct = 0 then
                null
            else
                { prompt_tokens = pt
                  completion_tokens = ct
                  total_tokens = pt + ct } }

    let result = AgentInstruction.accumulateUsageIfPresent state response
    Assert.Equal(expectedPt, result.promptTokens)
    Assert.Equal(expectedCt, result.completionTokens)

[<Theory>]
[<InlineData("stop")>]
[<InlineData("continue")>]
[<InlineData("exceeded")>]
let ``handleActionResult handles Stop, Continue, and exceeded iteration scenarios`` (scenario: string) =
    let config =
        if scenario = "exceeded" then
            let cfg = mockAgentConfig ()

            { cfg with
                runtimeConfig =
                    { cfg.runtimeConfig with
                        maxToolCallIterations = 2 } }
        else
            mockAgentConfig ()

    let state: AgentInstruction.LoopState =
        { messages = [ LlmClient.userMessage "Hello" ]
          promptTokens = 10
          completionTokens = 5
          iterationCount = if scenario = "exceeded" then 1 else 0
          result = AgentInstruction.InProgress }

    let action =
        match scenario with
        | "stop" -> AgentInstruction.Stop("Done!", [ LlmClient.assistantMessage "Done!" ])
        | "continue" ->
            AgentInstruction.Continue
                [ LlmClient.toolResultMessage "1" (AgentToolCall.ToolName.toString AgentToolCall.ReadFile) "content" ]
        | "exceeded" ->
            AgentInstruction.Continue
                [ LlmClient.toolResultMessage "1" (AgentToolCall.ToolName.toString AgentToolCall.ReadFile) "content" ]
        | _ -> failwith "unknown scenario"

    let result = AgentInstruction.handleActionResult config state action

    match scenario with
    | "stop" ->
        Assert.Empty result.messages

        match result.result with
        | AgentInstruction.Completed(content, msgs, pt, ct) ->
            Assert.Equal("Done!", content)
            Assert.Single msgs |> ignore
            Assert.Contains("Done!", msgs.[0].content)
            Assert.Equal(10, pt)
            Assert.Equal(5, ct)
        | _ -> Assert.Fail "Expected Completed"
    | "continue" ->
        Assert.Equal(1, result.messages.Length)
        Assert.Equal(1, result.iterationCount)

        match result.result with
        | AgentInstruction.InProgress -> ()
        | _ -> Assert.Fail "Expected InProgress"
    | "exceeded" ->
        Assert.Empty result.messages

        match result.result with
        | AgentInstruction.Failed(err, pt, ct) ->
            Assert.Contains("Exceeded maximum", err)
            Assert.Equal(10, pt)
            Assert.Equal(5, ct)
        | _ -> Assert.Fail "Expected Failed"
    | _ -> failwith "unknown scenario"

[<Theory>]
[<InlineData("stop")>]
[<InlineData("tool-call")>]
let ``handleOkResponseWithChoices returns Completed for stop or InProgress for tool call`` (scenario: string) =
    async {
        let toolCall: LlmClient.ToolCall =
            { id = "call_1"
              ``type`` = "function"
              ``function`` =
                { name = AgentToolCall.ToolName.toString AgentToolCall.ReadFile
                  arguments = "{\"file_path\":\"test.txt\"}" } }

        let config =
            match scenario with
            | "stop" -> mockAgentConfig ()
            | "tool-call" ->
                let cfg = mockAgentConfig ()

                { cfg with
                    runtimeConfig =
                        { cfg.runtimeConfig with
                            maxToolCallIterations = 25 }
                    interactive =
                        { cfg.interactive with
                            confirmToolCall = fun _ _ _ -> true }
                    tools =
                        { cfg.tools with
                            readFile = fun _ -> Ok "content" } }
            | _ -> failwith "unknown scenario"

        let state: AgentInstruction.LoopState =
            { messages = [ LlmClient.userMessage "Hello" ]
              promptTokens = 10
              completionTokens = 5
              iterationCount = 0
              result = AgentInstruction.InProgress }

        let message: LlmClient.ResponseMessage =
            { role = "assistant"
              content = if scenario = "stop" then "Done!" else null
              tool_calls = if scenario = "stop" then null else [| toolCall |] }

        let! result = AgentInstruction.handleOkResponseWithChoices config state message

        match scenario with
        | "stop" ->
            Assert.Empty result.messages

            match result.result with
            | AgentInstruction.Completed(content, _, pt, ct) ->
                Assert.Equal("Done!", content)
                Assert.Equal(10, pt)
                Assert.Equal(5, ct)
            | _ -> Assert.Fail "Expected Completed"
        | "tool-call" ->
            Assert.Equal(3, result.messages.Length)
            Assert.Equal(1, result.iterationCount)

            match result.result with
            | AgentInstruction.InProgress -> ()
            | _ -> Assert.Fail "Expected InProgress"
        | _ -> failwith "unknown scenario"
    }
    |> Async.RunSynchronously

[<Fact>]
let ``handleOkResponse returns Failed when choices array is empty`` () =
    async {
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

        let! result = AgentInstruction.handleOkResponse (mockAgentConfig ()) state response
        Assert.Empty result.messages

        match result.result with
        | AgentInstruction.Failed(err, pt, ct) ->
            Assert.Contains("no choices", err)
            Assert.Equal(18, pt)
            Assert.Equal(9, ct)
        | _ -> Assert.Fail "Expected Failed"
    }
    |> Async.RunSynchronously

[<Fact>]
let ``handleOkResponse accumulates usage when response has usage data`` () =
    async {
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
                { name = AgentToolCall.ToolName.toString AgentToolCall.ReadFile
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

        let config =
            let cfg = mockAgentConfig ()

            { cfg with
                runtimeConfig =
                    { cfg.runtimeConfig with
                        maxToolCallIterations = 25 }
                interactive =
                    { cfg.interactive with
                        confirmToolCall = fun _ _ _ -> true }
                tools =
                    { cfg.tools with
                        readFile = fun _ -> Ok "content" } }

        let! result = AgentInstruction.handleOkResponse config state response
        Assert.Equal(13, result.promptTokens)
        Assert.Equal(7, result.completionTokens)
        Assert.Equal(1, result.iterationCount)
        Assert.Equal(3, result.messages.Length)

        match result.result with
        | AgentInstruction.InProgress -> ()
        | _ -> Assert.Fail "Expected InProgress"
    }
    |> Async.RunSynchronously

[<Fact>]
let ``processResponseResult returns Completed state when finish_reason is 'stop'`` () =
    async {
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

        let! newState = AgentInstruction.processResponseResult (mockAgentConfig ()) state (Ok response)
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
    |> Async.RunSynchronously

[<Fact>]
let ``processResponseResult returns InProgress state when finish_reason is 'tool_calls'`` () =
    async {
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
                { name = AgentToolCall.ToolName.toString AgentToolCall.ReadFile
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

        let! newState = AgentInstruction.processResponseResult (mockAgentConfig ()) state (Ok response)
        Assert.Equal(3, newState.messages.Length)
        Assert.Equal(2, newState.iterationCount)
        Assert.Equal(18, newState.promptTokens)
        Assert.Equal(9, newState.completionTokens)

        match newState.result with
        | AgentInstruction.InProgress -> ()
        | _ -> Assert.Fail "Expected InProgress"
    }
    |> Async.RunSynchronously

[<Fact>]
let ``processResponseResult returns Failed state when response contains an Error`` () =
    async {
        let state: AgentInstruction.LoopState =
            { messages = [ LlmClient.userMessage "Hello" ]
              promptTokens = 10
              completionTokens = 5
              iterationCount = 0
              result = AgentInstruction.InProgress }

        let! newState = AgentInstruction.processResponseResult (mockAgentConfig ()) state (Error "Network error")
        Assert.Empty newState.messages

        match newState.result with
        | AgentInstruction.Failed(err, p, c) ->
            Assert.Equal("Network error", err)
            Assert.Equal(10, p)
            Assert.Equal(5, c)
        | _ -> Assert.Fail "Expected Failed result"
    }
    |> Async.RunSynchronously

[<Fact>]
let ``processResponseResult returns Failed state when choices array is empty`` () =
    async {
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

        let! newState = AgentInstruction.processResponseResult (mockAgentConfig ()) state (Ok response)
        Assert.Empty newState.messages

        match newState.result with
        | AgentInstruction.Failed(err, p, c) ->
            Assert.Contains("no choices", err)
            Assert.Equal(18, p)
            Assert.Equal(9, c)
        | _ -> Assert.Fail "Expected Failed result"
    }
    |> Async.RunSynchronously

[<Fact>]
let ``processResponseResult preserves previous token counts when response usage field is null`` () =
    async {
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
                { name = AgentToolCall.ToolName.toString AgentToolCall.ReadFile
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

        let! newState = AgentInstruction.processResponseResult (mockAgentConfig ()) state (Ok response)
        Assert.Equal(5, newState.promptTokens)
        Assert.Equal(3, newState.completionTokens)

        match newState.result with
        | AgentInstruction.InProgress -> ()
        | _ -> Assert.Fail "Expected InProgress"
    }
    |> Async.RunSynchronously

[<Fact>]
let ``processResponseResult returns Failed state when tool call iterations exceed maxToolCallIterations`` () =
    async {
        let mutable output = []

        let config =
            let cfg = mockAgentConfig ()

            { cfg with
                runtimeConfig =
                    { cfg.runtimeConfig with
                        maxToolCallIterations = 2 }
                interactive =
                    { cfg.interactive with
                        writeLine = fun s -> output <- output @ [ s ]
                        confirmToolCall = fun _ _ _ -> true }
                tools =
                    { cfg.tools with
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
                { name = AgentToolCall.ToolName.toString AgentToolCall.ReadFile
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
    |> Async.RunSynchronously

[<Fact>]
let ``instructionLoop accumulates prompt and completion tokens across multiple API call iterations`` () =
    async {
        let toolCallJson =
            """{"id":"chatcmpl-1","choices":[{"index":0,"message":{"role":"assistant","content":"","tool_calls":[{"id":"call_1","type":"function","function":{"name":"read_file","arguments":"{\"file_path\":\"/nonexistent.txt\"}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":10,"completion_tokens":3,"total_tokens":13}}"""

        let finalJson =
            """{"id":"chatcmpl-2","choices":[{"index":0,"message":{"role":"assistant","content":"All done!"},"finish_reason":"stop"}],"usage":{"prompt_tokens":20,"completion_tokens":8,"total_tokens":28}}"""

        let mutable callCount = 0

        let mockClient =
            fun _json ->
                async {
                    callCount <- callCount + 1
                    let json = if callCount = 1 then toolCallJson else finalJson
                    return makeSuccessResponse json
                }

        let messages = [ LlmClient.userMessage "Do something" ]

        let state: AgentInstruction.LoopState =
            { messages = messages
              promptTokens = 0
              completionTokens = 0
              iterationCount = 0
              result = AgentInstruction.InProgress }

        let! result = AgentInstruction.instructionLoop (mockAgentConfig ()) mockClient state

        match result with
        | Ok(content, updatedMessages, pTokens, cTokens) ->
            Assert.Equal("All done!", content)
            Assert.Equal(4, updatedMessages.Length)
            Assert.Equal(10 + 20, pTokens)
            Assert.Equal(3 + 8, cTokens)
        | Error(err, _, _) -> Assert.Fail $"Expected Ok but got Error: {err}"
    }
    |> Async.RunSynchronously

[<Fact>]
let ``instructionLoop returns Error result when the underlying API call fails`` () =
    async {
        let mockClient =
            fun _json ->
                async { return makeErrorResponse System.Net.HttpStatusCode.InternalServerError "Server Error" "{}" }

        let messages = [ LlmClient.userMessage "Hello" ]

        let state: AgentInstruction.LoopState =
            { messages = messages
              promptTokens = 5
              completionTokens = 3
              iterationCount = 0
              result = AgentInstruction.InProgress }

        let! result = AgentInstruction.instructionLoop (mockAgentConfig ()) mockClient state

        match result with
        | Error(errMsg, pTokens, cTokens) ->
            Assert.Contains("API Error:", errMsg)
            Assert.Equal(5, pTokens)
            Assert.Equal(3, cTokens)
        | Ok _ -> Assert.Fail "Expected Error but got Ok"
    }
    |> Async.RunSynchronously

[<Fact>]
let ``processInstruction omits assistant banner when response content is blank or whitespace`` () =
    async {
        let mutable output = []

        let config =
            let cfg = mockAgentConfig ()

            { cfg with
                interactive =
                    { cfg.interactive with
                        writeLine = fun s -> output <- output @ [ s ]
                        write = ignore } }

        let blankContentJson =
            """{"id":"chatcmpl-blank","choices":[{"index":0,"message":{"role":"assistant","content":"   "},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}}"""

        let mockClient = fun _json -> async { return makeSuccessResponse blankContentJson }

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
    |> Async.RunSynchronously

[<Fact>]
let ``processInstruction displays error message when instructionLoop returns Error result`` () =
    async {
        let mutable output = []

        let config =
            let cfg = mockAgentConfig ()

            { cfg with
                interactive =
                    { cfg.interactive with
                        writeLine = fun s -> output <- output @ [ s ]
                        write = ignore } }

        let mockClient =
            fun _json -> async { return makeErrorResponse System.Net.HttpStatusCode.BadRequest "Bad Request" "{}" }

        let messages = [ LlmClient.userMessage "Hello" ]
        let! resultMsgs, pTokens, cTokens = AgentInstruction.processInstruction config mockClient messages messages
        Assert.True(output |> List.exists (fun s -> s.Contains "An error occurred"))
        Assert.Equal<LlmClient.ChatMessage list>(messages, resultMsgs)
        Assert.Equal(0, pTokens)
        Assert.Equal(0, cTokens)
    }
    |> Async.RunSynchronously

[<Fact>]
let ``processInstruction catches unexpected exceptions and displays error to user`` () =
    async {
        let mutable output = []

        let config =
            let cfg = mockAgentConfig ()

            { cfg with
                interactive =
                    { cfg.interactive with
                        writeLine = fun s -> output <- output @ [ s ]
                        write = fun _ -> raise (new System.InvalidOperationException "Write failed") } }

        let mockClient =
            fun (_json: string) -> async { return makeSuccessResponse validChatResponseJson }

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
    |> Async.RunSynchronously
