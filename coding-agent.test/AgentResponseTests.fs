module CodingAgent.AgentResponseTests

open Xunit
open CodingAgent
open TestHelpers

let validChatResponseJson =
    """{"id":"chatcmpl-123","choices":[{"index":0,"message":{"role":"assistant","content":"Hello!"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}"""

let mockConfig =
    { llmClientConfig =
        { apiKey = ""
          model = ""
          endpoint = "" }
      tools =
        { readFile =
            fun path ->
                if path.Contains "nonexistent" || path.Contains "non_existent" then
                    Error(sprintf "Error: File '%s' not found." path)
                else
                    Ok(sprintf "Content of %s" path)
          writeFile = fun path _ -> Ok(sprintf "Successfully wrote to '%s'." path)
          runCommand = fun cmd cwd -> Ok(sprintf "Output of %s in %s" cmd cwd)
          listDirectory = fun path -> Ok(sprintf "Contents of directory '%s':" path)
          grepSearch = fun query path -> Ok(sprintf "Matches for '%s' in '%s'" query path)
          patchFile = fun path _ _ -> Ok(sprintf "Patched '%s'" path)
          readFileLines = fun path startLine endLine -> Ok(sprintf "Lines %d-%d of %s" startLine endLine path)
          findFiles = fun pattern path -> Ok(sprintf "Matches for '%s' in '%s'" pattern path) }
      sessionStore = mockSessionStore ()
      fileSystem = (MockFileSystem()).FileSystem
      write = ignore
      writeLine = ignore
      readLine = fun () -> ""
      confirmToolCall = fun _ _ -> true
      systemPrompt = "You are helpful"
      maxHistory = 20
      autoConfirm = Off }

[<Fact>]
let ``handleResponse returns Stop action for assistant message without tool calls`` () =
    let responseMsg: LlmClient.ResponseMessage =
        { role = "assistant"
          content = "I have completed the task."
          tool_calls = null }

    let currentMessages = []
    let action = AgentResponse.handleResponse mockConfig currentMessages responseMsg

    match action with
    | AgentResponse.Stop(content, nextMessages) ->
        Assert.Equal("I have completed the task.", content)
        let msg = Assert.Single nextMessages
        Assert.Equal("assistant", msg.role)
        Assert.Equal("I have completed the task.", msg.content)
    | AgentResponse.Continue _ -> failwith "Expected Stop, but got Continue"

[<Fact>]
let ``handleResponse returns Stop action when tool_calls is empty array`` () =
    let responseMsg: LlmClient.ResponseMessage =
        { role = "assistant"
          content = "Done."
          tool_calls = [||] }

    let action = AgentResponse.handleResponse mockConfig [] responseMsg

    match action with
    | AgentResponse.Stop(content, _) -> Assert.Equal("Done.", content)
    | AgentResponse.Continue _ -> failwith "Expected Stop, but got Continue"

[<Fact>]
let ``handleResponse appends assistant message to currentMessages`` () =
    let existingMsg = LlmClient.userMessage "Hello"

    let responseMsg: LlmClient.ResponseMessage =
        { role = "assistant"
          content = "Hi there."
          tool_calls = null }

    let action = AgentResponse.handleResponse mockConfig [ existingMsg ] responseMsg

    match action with
    | AgentResponse.Stop(_, msgs) ->
        Assert.Equal(2, msgs.Length)
        Assert.Equal("user", msgs.[0].role)
        Assert.Equal("assistant", msgs.[1].role)
    | AgentResponse.Continue _ -> failwith "Expected Stop, but got Continue"

[<Fact>]
let ``handleResponse returns Continue action for assistant message with tool calls`` () =
    let dummyToolCall: LlmClient.ToolCall =
        { id = "call_123"
          ``type`` = "function"
          ``function`` =
            { name = "read_file"
              arguments = "{\"file_path\": \"non_existent.txt\"}" } }

    let responseMsg: LlmClient.ResponseMessage =
        { role = "assistant"
          content = null
          tool_calls = [| dummyToolCall |] }

    let currentMessages = []
    let action = AgentResponse.handleResponse mockConfig currentMessages responseMsg

    match action with
    | AgentResponse.Continue nextMessages ->
        Assert.Equal(2, nextMessages.Length)
        let assistantMsg = nextMessages.[0]
        Assert.Equal("assistant", assistantMsg.role)
        Assert.Equal(1, assistantMsg.tool_calls.Length)
        let resultMsg = nextMessages.[1]
        Assert.Equal("tool", resultMsg.role)
        Assert.Equal("call_123", resultMsg.tool_call_id)
        Assert.Contains("not found", resultMsg.content)
    | AgentResponse.Stop _ -> failwith "Expected Continue, but got Stop"

[<Fact>]
let ``handleResponse writes success message when tool call succeeds`` () =
    let mutable output = []

    let config =
        { mockConfig with
            writeLine = fun s -> output <- output @ [ s ]
            confirmToolCall = fun _ _ -> true
            tools =
                { mockConfig.tools with
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

    let action = AgentResponse.handleResponse config [] responseMsg

    match action with
    | AgentResponse.Continue nextMessages ->
        Assert.True(output |> List.exists (fun s -> s.Contains("✅")))
        Assert.Equal(2, nextMessages.Length)
        let toolResult = nextMessages.[1]
        Assert.Equal("tool", toolResult.role)
        Assert.Contains("file content", toolResult.content)
    | AgentResponse.Stop _ -> Assert.Fail "Expected Continue, got Stop"

[<Fact>]
let ``handleResponse writes failure message when tool call returns Error`` () =
    let mutable output = []

    let config =
        { mockConfig with
            writeLine = fun s -> output <- output @ [ s ]
            confirmToolCall = fun _ _ -> true
            tools =
                { mockConfig.tools with
                    readFile = fun _ -> Error "Error: something went wrong" } }

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

    let action = AgentResponse.handleResponse config [] responseMsg

    match action with
    | AgentResponse.Continue nextMessages ->
        Assert.True(output |> List.exists (fun s -> s.Contains "❌"))
        let toolResult = nextMessages.[1]
        Assert.Equal("tool", toolResult.role)
        Assert.Contains("something went wrong", toolResult.content)
    | AgentResponse.Stop _ -> Assert.Fail "Expected Continue, got Stop"

[<Fact>]
let ``handleResponseResult with Ok response and Stop action`` () =
    let state: AgentResponse.LoopState =
        { messages = [ LlmClient.userMessage "Hello" ]
          promptTokens = 10
          completionTokens = 5
          result = None }

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

    let newState = AgentResponse.handleResponseResult mockConfig state (Ok response)

    Assert.Empty(newState.messages :> System.Collections.IEnumerable)
    Assert.Equal(18, newState.promptTokens)
    Assert.Equal(9, newState.completionTokens)

    match newState.result with
    | Some(Ok(content, _, _, _)) -> Assert.Equal("Done!", content)
    | _ -> Assert.Fail "Expected Ok result"

[<Fact>]
let ``handleResponseResult with Ok response and Continue action`` () =
    let toolCall: LlmClient.ToolCall =
        { id = "call_1"
          ``type`` = "function"
          ``function`` =
            { name = "read_file"
              arguments = "{\"file_path\":\"test.txt\"}" } }

    let state: AgentResponse.LoopState =
        { messages = [ LlmClient.userMessage "Hello" ]
          promptTokens = 10
          completionTokens = 5
          result = None }

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

    let newState = AgentResponse.handleResponseResult mockConfig state (Ok response)

    Assert.Equal(3, newState.messages.Length)
    Assert.True newState.result.IsNone
    Assert.Equal(18, newState.promptTokens)
    Assert.Equal(9, newState.completionTokens)

[<Fact>]
let ``handleResponseResult with Error response`` () =
    let state: AgentResponse.LoopState =
        { messages = [ LlmClient.userMessage "Hello" ]
          promptTokens = 10
          completionTokens = 5
          result = None }

    let newState =
        AgentResponse.handleResponseResult mockConfig state (Error "Network error")

    Assert.Empty(newState.messages :> System.Collections.IEnumerable)

    match newState.result with
    | Some(Error(err, p, c)) ->
        Assert.Equal("Network error", err)
        Assert.Equal(10, p)
        Assert.Equal(5, c)
    | _ -> Assert.Fail "Expected Error result"

[<Fact>]
let ``handleResponseResult with empty choices`` () =
    let state: AgentResponse.LoopState =
        { messages = [ LlmClient.userMessage "Hello" ]
          promptTokens = 10
          completionTokens = 5
          result = None }

    let response: LlmClient.ChatResponse =
        { id = "chatcmpl-empty"
          choices = [||]
          usage =
            { prompt_tokens = 8
              completion_tokens = 4
              total_tokens = 12 } }

    let newState = AgentResponse.handleResponseResult mockConfig state (Ok response)

    Assert.Empty(newState.messages :> System.Collections.IEnumerable)

    match newState.result with
    | Some(Error(err, _, _)) -> Assert.Contains("no choices", err)
    | _ -> Assert.Fail "Expected Error result"

[<Fact>]
let ``handleResponseResult with null usage keeps token counts`` () =
    let state: AgentResponse.LoopState =
        { messages = [ LlmClient.userMessage "Hello" ]
          promptTokens = 5
          completionTokens = 3
          result = None }

    let response: LlmClient.ChatResponse =
        { id = "chatcmpl-nousage"
          choices =
            [| { index = 0
                 message =
                   { role = "assistant"
                     content = "Done"
                     tool_calls = null }
                 finish_reason = "stop" } |]
          usage = null }

    let newState = AgentResponse.handleResponseResult mockConfig state (Ok response)
    Assert.Equal(5, newState.promptTokens)
    Assert.Equal(3, newState.completionTokens)

[<Fact>]
let ``runLoop returns Ok with response content when LLM returns a final answer`` () =
    task {
        let mockClient =
            fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

        let messages = [ LlmClient.userMessage "Hello" ]

        let state: AgentResponse.LoopState =
            { messages = messages
              promptTokens = 0
              completionTokens = 0
              result = None }

        let! result = AgentResponse.runLoop mockConfig mockClient messages state

        match result with
        | Ok(content, updatedMessages, pTokens, cTokens) ->
            Assert.Equal("Hello!", content)
            Assert.Equal(2, updatedMessages.Length)
            Assert.Equal(10, pTokens)
            Assert.Equal(5, cTokens)
        | Error(err, _, _) -> Assert.Fail(sprintf "Expected Ok but got Error: %s" err)
    }

[<Fact>]
let ``runLoop continues loop when tool call is returned, then stops on next answer`` () =
    task {
        let toolCallJson =
            """{"id":"chatcmpl-1","choices":[{"index":0,"message":{"role":"assistant","content":"","tool_calls":[{"id":"call_1","type":"function","function":{"name":"read_file","arguments":"{\"file_path\":\"/nonexistent.txt\"}"}}]},"finish_reason":"tool_calls"}]}"""

        let mutable callCount = 0

        let mockClient =
            fun _json ->
                callCount <- callCount + 1

                let responseJson =
                    if callCount = 1 then
                        toolCallJson
                    else
                        validChatResponseJson

                System.Threading.Tasks.Task.FromResult(makeSuccessResponse responseJson)

        let messages = [ LlmClient.userMessage "Read a file" ]

        let state: AgentResponse.LoopState =
            { messages = messages
              promptTokens = 0
              completionTokens = 0
              result = None }

        let! result = AgentResponse.runLoop mockConfig mockClient messages state

        match result with
        | Ok(content, updatedMessages, pTokens, cTokens) ->
            Assert.Equal("Hello!", content)
            Assert.Equal(4, updatedMessages.Length)
            Assert.Equal(10, pTokens)
            Assert.Equal(5, cTokens)
        | Error(err, _, _) -> Assert.Fail(sprintf "Expected Ok but got Error: %s" err)
    }

[<Fact>]
let ``runLoop accumulates promptTokens and completionTokens across multiple API calls`` () =
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

        let state: AgentResponse.LoopState =
            { messages = messages
              promptTokens = 0
              completionTokens = 0
              result = None }

        let! result = AgentResponse.runLoop mockConfig mockClient messages state

        match result with
        | Ok(content, _, pTokens, cTokens) ->
            Assert.Equal("All done!", content)
            Assert.Equal(10 + 20, pTokens)
            Assert.Equal(3 + 8, cTokens)
        | Error(err, _, _) -> Assert.Fail(sprintf "Expected Ok but got Error: %s" err)
    }

[<Fact>]
let ``runLoop accumulates zero tokens when usage is null`` () =
    task {
        let noUsageJson =
            """{"id":"chatcmpl-nousage","choices":[{"index":0,"message":{"role":"assistant","content":"Done."},"finish_reason":"stop"}]}"""

        let mockClient =
            fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse noUsageJson)

        let messages = [ LlmClient.userMessage "Hello" ]

        let state: AgentResponse.LoopState =
            { messages = messages
              promptTokens = 0
              completionTokens = 0
              result = None }

        let! result = AgentResponse.runLoop mockConfig mockClient messages state

        match result with
        | Ok(content, _, pTokens, cTokens) ->
            Assert.Equal("Done.", content)
            Assert.Equal(0, pTokens)
            Assert.Equal(0, cTokens)
        | Error(err, _, _) -> Assert.Fail(sprintf "Expected Ok but got Error: %s" err)
    }

[<Fact>]
let ``runLoop returns Error when LLM client returns Error`` () =
    task {
        let mockClient =
            fun _json ->
                System.Threading.Tasks.Task.FromResult(
                    makeErrorResponse System.Net.HttpStatusCode.InternalServerError "Server Error" "{}"
                )

        let messages = [ LlmClient.userMessage "Hello" ]

        let state: AgentResponse.LoopState =
            { messages = messages
              promptTokens = 0
              completionTokens = 0
              result = None }

        let! result = AgentResponse.runLoop mockConfig mockClient messages state

        match result with
        | Error(errMsg, pTokens, cTokens) ->
            Assert.Contains("API Error:", errMsg)
            Assert.Equal(0, pTokens)
            Assert.Equal(0, cTokens)
        | Ok _ -> Assert.Fail "Expected Error but got Ok"
    }

[<Fact>]
let ``runLoop returns Error when API returns empty choices`` () =
    task {
        let emptyChoicesJson = """{"id":"chatcmpl-empty","choices":[]}"""

        let mockClient =
            fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse emptyChoicesJson)

        let messages = [ LlmClient.userMessage "Hello" ]

        let state: AgentResponse.LoopState =
            { messages = messages
              promptTokens = 0
              completionTokens = 0
              result = None }

        let! result = AgentResponse.runLoop mockConfig mockClient messages state

        match result with
        | Error(errMsg, pTokens, cTokens) ->
            Assert.Contains("no choices", errMsg)
            Assert.Equal(0, pTokens)
            Assert.Equal(0, cTokens)
        | Ok _ -> Assert.Fail "Expected Error but got Ok"
    }

[<Fact>]
let ``runAgentLoop prints nothing when response content is blank`` () =
    let mutable output = []

    let config =
        { mockConfig with
            writeLine = fun s -> output <- output @ [ s ]
            write = ignore }

    let blankContentJson =
        """{"id":"chatcmpl-blank","choices":[{"index":0,"message":{"role":"assistant","content":"   "},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}"""

    let mockClient =
        fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse blankContentJson)

    let messages = [ LlmClient.userMessage "Hello" ]

    let _, pTokens, cTokens =
        AgentResponse.runAgentLoop config mockClient messages messages
        |> Async.AwaitTask
        |> Async.RunSynchronously

    Assert.False(output |> List.exists (fun s -> s.StartsWith "\n🤖"))
    Assert.Equal(1, pTokens)
    Assert.Equal(1, cTokens)

[<Fact>]
let ``runAgentLoop prints error message when runLoop returns Error`` () =
    let mutable output = []

    let config =
        { mockConfig with
            writeLine = fun s -> output <- output @ [ s ]
            write = ignore }

    let mockClient =
        fun _json ->
            System.Threading.Tasks.Task.FromResult(
                makeErrorResponse System.Net.HttpStatusCode.BadRequest "Bad Request" "{}"
            )

    let messages = [ LlmClient.userMessage "Hello" ]

    let returnedMsgs, _, _ =
        AgentResponse.runAgentLoop config mockClient messages messages
        |> Async.AwaitTask
        |> Async.RunSynchronously

    Assert.True(output |> List.exists (fun s -> s.Contains "❌"))
    Assert.Equal<LlmClient.ChatMessage list>(messages, returnedMsgs)

[<Fact>]
let ``runAgentLoop catches exceptions and returns original messages`` () =
    let mutable output = []

    let config =
        { mockConfig with
            writeLine = fun s -> output <- output @ [ s ] }

    let mockClient =
        fun (_json: string) ->
            raise (new System.Exception("Unexpected failure"))
            System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

    let messages = [ LlmClient.userMessage "Hello" ]

    let resultMsgs, pTokens, cTokens =
        AgentResponse.runAgentLoop config mockClient messages messages
        |> Async.AwaitTask
        |> Async.RunSynchronously

    Assert.Equal<LlmClient.ChatMessage list>(messages, resultMsgs)
    Assert.Equal(0, pTokens)
    Assert.Equal(0, cTokens)
    Assert.True(output |> List.exists (fun s -> s.Contains "❌"))

[<Fact>]
let ``runAgentLoop catches exceptions with InnerException and returns error with outer message`` () =
    let mutable output = []

    let config =
        { mockConfig with
            writeLine = fun s -> output <- output @ [ s ] }

    let innerEx = new System.InvalidOperationException("Inner failure")

    let mockClient =
        fun (_json: string) ->
            raise (new System.Exception("Outer", innerEx))
            System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

    let messages = [ LlmClient.userMessage "Hello" ]

    let resultMsgs, pTokens, cTokens =
        AgentResponse.runAgentLoop config mockClient messages messages
        |> Async.AwaitTask
        |> Async.RunSynchronously

    Assert.Equal<LlmClient.ChatMessage list>(messages, resultMsgs)
    Assert.Equal(0, pTokens)
    Assert.Equal(0, cTokens)
    Assert.True(output |> List.exists (fun s -> s.Contains "Outer"))

[<Fact>]
let ``runAgentLoop catches exceptions from write and shows unexpected error`` () =
    let mutable output = []

    let config =
        { mockConfig with
            writeLine = fun s -> output <- output @ [ s ]
            write = fun _ -> raise (new System.InvalidOperationException("Write failed")) }

    let mockClient =
        fun (_json: string) -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

    let messages = [ LlmClient.userMessage "Hello" ]

    let resultMsgs, pTokens, cTokens =
        AgentResponse.runAgentLoop config mockClient messages messages
        |> Async.AwaitTask
        |> Async.RunSynchronously

    Assert.Equal<LlmClient.ChatMessage list>(messages, resultMsgs)
    Assert.Equal(0, pTokens)
    Assert.Equal(0, cTokens)
    Assert.True(output |> List.exists (fun s -> s.Contains "❌" && s.Contains "Write failed"))

[<Fact>]
let ``runAgentLoop catches exceptions without InnerException and shows ex.Message`` () =
    let mutable output = []

    let config =
        { mockConfig with
            writeLine = fun s -> output <- output @ [ s ]
            write = fun _ -> raise (new System.InvalidOperationException("No inner exception")) }

    let mockClient =
        fun (_json: string) -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

    let messages = [ LlmClient.userMessage "Hello" ]

    let resultMsgs, pTokens, cTokens =
        AgentResponse.runAgentLoop config mockClient messages messages
        |> Async.AwaitTask
        |> Async.RunSynchronously

    Assert.Equal<LlmClient.ChatMessage list>(messages, resultMsgs)
    Assert.Equal(0, pTokens)
    Assert.Equal(0, cTokens)

    Assert.True(
        output
        |> List.exists (fun s -> s.Contains "unexpected error" && s.Contains "No inner exception")
    )
