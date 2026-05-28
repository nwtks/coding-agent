module CodingAgent.AgentTests

open Xunit
open CodingAgent

let mockConfig =
    { llmClientConfig =
        { apiKey = ""
          model = ""
          endpoint = "" }
      tools =
        { readFile =
            fun path ->
                if path.Contains("nonexistent") || path.Contains("non_existent") then
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
      write = ignore
      writeLine = ignore
      readLine = fun () -> ""
      confirmToolCall = fun _ _ -> true
      systemPrompt = "You are helpful"
      maxHistory = 20 }

let validChatResponseJson =
    """{"id":"chatcmpl-123","choices":[{"index":0,"message":{"role":"assistant","content":"Hello!"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}"""

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
let ``confirmToolCall returns true when user types 'y'`` () =
    let mutable written = []

    let config =
        { mockConfig with
            write = fun s -> written <- written @ [ s ]
            readLine = fun () -> "y" }

    let toolCall =
        { id = "call_confirm"
          ``type`` = "function"
          ``function`` =
            { name = "read_file"
              arguments = "{\"file_path\": \"foo.txt\"}" } }

    let result = Agent.confirmToolCall config toolCall
    Assert.True result
    Assert.True(written |> List.exists (fun s -> s.Contains "read_file"))

[<Fact>]
let ``confirmToolCall returns true when user types 'Y' (case-insensitive)`` () =
    let config =
        { mockConfig with
            readLine = fun () -> "Y" }

    let toolCall =
        { id = "call_confirm"
          ``type`` = "function"
          ``function`` =
            { name = "write_file"
              arguments = "{}" } }

    Assert.True(Agent.confirmToolCall config toolCall)

[<Fact>]
let ``confirmToolCall returns false when user types 'n'`` () =
    let config =
        { mockConfig with
            readLine = fun () -> "n" }

    let toolCall =
        { id = "call_confirm"
          ``type`` = "function"
          ``function`` = { name = "read_file"; arguments = "{}" } }

    Assert.False(Agent.confirmToolCall config toolCall)

[<Fact>]
let ``confirmToolCall returns false when user presses Enter (empty input)`` () =
    let config =
        { mockConfig with
            readLine = fun () -> "" }

    let toolCall =
        { id = "call_confirm"
          ``type`` = "function"
          ``function`` = { name = "read_file"; arguments = "{}" } }

    Assert.False(Agent.confirmToolCall config toolCall)

[<Fact>]
let ``executeToolCall read_file returns file content on success`` () =
    let toolCall =
        { id = "call_1"
          ``type`` = "function"
          ``function`` =
            { name = "read_file"
              arguments = "{\"file_path\": \"test.txt\"}" } }

    let customConfig =
        { mockConfig with
            tools =
                { mockConfig.tools with
                    readFile =
                        fun path ->
                            Assert.Equal("test.txt", path)
                            Ok "hello from file" } }

    let result = Agent.executeToolCall customConfig toolCall

    match result with
    | Ok content -> Assert.Equal("hello from file", content)
    | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)

[<Fact>]
let ``executeToolCall read_file returns Error for non-existent file`` () =
    let toolCall =
        { id = "call_1"
          ``type`` = "function"
          ``function`` =
            { name = "read_file"
              arguments = "{\"file_path\": \"/definitely/does/not/exist.txt\"}" } }

    let customConfig =
        { mockConfig with
            tools =
                { mockConfig.tools with
                    readFile =
                        fun path ->
                            Assert.Equal("/definitely/does/not/exist.txt", path)
                            Error "Error: File '/definitely/does/not/exist.txt' not found." } }

    let result = Agent.executeToolCall customConfig toolCall

    match result with
    | Error _ -> ()
    | Ok _ -> Assert.Fail "Expected Error for missing file"

[<Fact>]
let ``executeToolCall write_file writes content successfully`` () =
    let toolCall =
        { id = "call_2"
          ``type`` = "function"
          ``function`` =
            { name = "write_file"
              arguments = "{\"file_path\": \"test.txt\", \"content\": \"written by test\"}" } }

    let mutable called = false

    let customConfig =
        { mockConfig with
            tools =
                { mockConfig.tools with
                    writeFile =
                        fun path content ->
                            Assert.Equal("test.txt", path)
                            Assert.Equal("written by test", content)
                            called <- true
                            Ok "Successfully wrote to 'test.txt'." } }

    let result = Agent.executeToolCall customConfig toolCall

    match result with
    | Ok _ -> Assert.True(called)
    | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)

[<Fact>]
let ``executeToolCall run_command returns command output`` () =
    let toolCall =
        { id = "call_3"
          ``type`` = "function"
          ``function`` =
            { name = "run_command"
              arguments = "{\"command_line\": \"echo hello from agent test\"}" } }

    let customConfig =
        { mockConfig with
            tools =
                { mockConfig.tools with
                    runCommand =
                        fun cmd cwd ->
                            Assert.Equal("echo hello from agent test", cmd)
                            Assert.Equal("", cwd)
                            Ok "hello from agent test" } }

    let result = Agent.executeToolCall customConfig toolCall

    match result with
    | Ok output -> Assert.Contains("hello from agent test", output)
    | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)

[<Fact>]
let ``executeToolCall run_command with cwd argument succeeds`` () =
    let toolCall =
        { id = "call_4"
          ``type`` = "function"
          ``function`` =
            { name = "run_command"
              arguments = "{\"command_line\": \"echo in temp\", \"cwd\": \"/tmp\"}" } }

    let customConfig =
        { mockConfig with
            tools =
                { mockConfig.tools with
                    runCommand =
                        fun cmd cwd ->
                            Assert.Equal("echo in temp", cmd)
                            Assert.Equal("/tmp", cwd)
                            Ok "in temp" } }

    let result = Agent.executeToolCall customConfig toolCall

    match result with
    | Ok output -> Assert.Contains("in temp", output)
    | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)

[<Fact>]
let ``executeToolCall list_directory returns directory listing`` () =
    let toolCall =
        { id = "call_5"
          ``type`` = "function"
          ``function`` =
            { name = "list_directory"
              arguments = "{\"directory_path\": \"/tmp\"}" } }

    let customConfig =
        { mockConfig with
            tools =
                { mockConfig.tools with
                    listDirectory =
                        fun path ->
                            Assert.Equal("/tmp", path)
                            Ok "Contents of directory '/tmp':\ntest.txt" } }

    let result = Agent.executeToolCall customConfig toolCall

    match result with
    | Ok output -> Assert.Contains("test.txt", output)
    | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)

[<Fact>]
let ``executeToolCall list_directory without directoryPath argument uses empty string`` () =
    let toolCall =
        { id = "call_6"
          ``type`` = "function"
          ``function`` =
            { name = "list_directory"
              arguments = "{}" } }

    let customConfig =
        { mockConfig with
            tools =
                { mockConfig.tools with
                    listDirectory =
                        fun path ->
                            Assert.Equal("", path)
                            Ok "Contents of directory '':\ntest.txt" } }

    let result = Agent.executeToolCall customConfig toolCall
    Assert.NotNull(result :> obj)

[<Fact>]
let ``executeToolCall grep_search returns query matches`` () =
    let toolCall =
        { id = "call_grep"
          ``type`` = "function"
          ``function`` =
            { name = "grep_search"
              arguments = "{\"query\": \"hello\", \"directory_path\": \"/src\"}" } }

    let customConfig =
        { mockConfig with
            tools =
                { mockConfig.tools with
                    grepSearch =
                        fun query path ->
                            Assert.Equal("hello", query)
                            Assert.Equal("/src", path)
                            Ok "Found matches for 'hello' in '/src':\nfoo.txt:1: hello" } }

    let result = Agent.executeToolCall customConfig toolCall

    match result with
    | Ok output -> Assert.Contains("foo.txt", output)
    | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)

[<Fact>]
let ``executeToolCall grep_search uses empty string when directory_path is omitted`` () =
    let toolCall =
        { id = "call_grep_nodir"
          ``type`` = "function"
          ``function`` =
            { name = "grep_search"
              arguments = "{\"query\": \"hello\"}" } }

    let mutable capturedPath = "NOT_SET"

    let config =
        { mockConfig with
            tools =
                { mockConfig.tools with
                    grepSearch =
                        fun query path ->
                            capturedPath <- path
                            Ok(sprintf "Matches for '%s' in '%s'" query path) } }

    let result = Agent.executeToolCall config toolCall

    match result with
    | Ok _ -> Assert.Equal("", capturedPath)
    | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)

[<Fact>]
let ``executeToolCall patch_file patches file content successfully`` () =
    let toolCall =
        { id = "call_patch"
          ``type`` = "function"
          ``function`` =
            { name = "patch_file"
              arguments = "{\"file_path\": \"test.txt\", \"target\": \"old\", \"replacement\": \"new\"}" } }

    let customConfig =
        { mockConfig with
            tools =
                { mockConfig.tools with
                    patchFile =
                        fun path target replacement ->
                            Assert.Equal("test.txt", path)
                            Assert.Equal("old", target)
                            Assert.Equal("new", replacement)
                            Ok "Successfully patched file 'test.txt'." } }

    let result = Agent.executeToolCall customConfig toolCall

    match result with
    | Ok output -> Assert.Contains("Successfully patched", output)
    | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)

[<Fact>]
let ``executeToolCall read_file_lines reads file lines successfully`` () =
    let toolCall =
        { id = "call_lines"
          ``type`` = "function"
          ``function`` =
            { name = "read_file_lines"
              arguments = "{\"file_path\": \"test.txt\", \"start_line\": 10, \"end_line\": 20}" } }

    let customConfig =
        { mockConfig with
            tools =
                { mockConfig.tools with
                    readFileLines =
                        fun path start end_ ->
                            Assert.Equal("test.txt", path)
                            Assert.Equal(10, start)
                            Assert.Equal(20, end_)
                            Ok "lines 10 to 20" } }

    let result = Agent.executeToolCall customConfig toolCall

    match result with
    | Ok output -> Assert.Equal("lines 10 to 20", output)
    | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)

[<Fact>]
let ``executeToolCall find_files searches files successfully`` () =
    let toolCall =
        { id = "call_find"
          ``type`` = "function"
          ``function`` =
            { name = "find_files"
              arguments = "{\"pattern\": \"*.fs\", \"directory_path\": \"/src\"}" } }

    let customConfig =
        { mockConfig with
            tools =
                { mockConfig.tools with
                    findFiles =
                        fun pattern path ->
                            Assert.Equal("*.fs", pattern)
                            Assert.Equal("/src", path)
                            Ok "file1.fs\nfile2.fs" } }

    let result = Agent.executeToolCall customConfig toolCall

    match result with
    | Ok output -> Assert.Equal("file1.fs\nfile2.fs", output)
    | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)

[<Fact>]
let ``executeToolCall find_files uses empty string when directory_path is omitted`` () =
    let toolCall =
        { id = "call_find_nodir"
          ``type`` = "function"
          ``function`` =
            { name = "find_files"
              arguments = "{\"pattern\": \"*.fs\"}" } }

    let mutable capturedPath = "NOT_SET"

    let config =
        { mockConfig with
            tools =
                { mockConfig.tools with
                    findFiles =
                        fun pattern path ->
                            capturedPath <- path
                            Ok(sprintf "Found '%s' in '%s'" pattern path) } }

    let result = Agent.executeToolCall config toolCall

    match result with
    | Ok _ -> Assert.Equal("", capturedPath)
    | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)

[<Fact>]
let ``executeToolCall returns Error when user cancels confirmation`` () =
    let mutable cancelMsg = ""

    let config =
        { mockConfig with
            confirmToolCall = fun _ _ -> false
            writeLine = fun s -> cancelMsg <- s }

    let toolCall =
        { id = "call_cancel"
          ``type`` = "function"
          ``function`` =
            { name = "read_file"
              arguments = "{\"file_path\": \"test.txt\"}" } }

    let result = Agent.executeToolCall config toolCall

    match result with
    | Error errMsg ->
        Assert.Contains("cancelled", errMsg)
        Assert.Contains("read_file", cancelMsg)
    | Ok _ -> Assert.Fail "Expected Error when tool call is cancelled"

[<Fact>]
let ``executeToolCall returns Error for unknown function name`` () =
    let toolCall =
        { id = "call_unknown"
          ``type`` = "function"
          ``function`` =
            { name = "nonexistent_tool"
              arguments = "{}" } }

    let result = Agent.executeToolCall mockConfig toolCall

    match result with
    | Error errMsg -> Assert.Contains("nonexistent_tool", errMsg)
    | Ok _ -> Assert.Fail "Expected Error for unknown tool"

[<Fact>]
let ``executeToolCall returns Error on invalid JSON arguments`` () =
    let toolCall =
        { id = "call_bad"
          ``type`` = "function"
          ``function`` =
            { name = "read_file"
              arguments = "NOT VALID JSON" } }

    let result = Agent.executeToolCall mockConfig toolCall

    match result with
    | Error errMsg -> Assert.Contains("read_file", errMsg)
    | Ok _ -> Assert.Fail "Expected Error for invalid JSON"

[<Fact>]
let ``handleResponse returns Stop action for assistant message without tool calls`` () =
    let responseMsg =
        { role = "assistant"
          content = "I have completed the task."
          tool_calls = null }

    let currentMessages = []
    let action = Agent.handleResponse mockConfig currentMessages responseMsg

    match action with
    | Agent.Stop(content, nextMessages) ->
        Assert.Equal("I have completed the task.", content)
        let msg = Assert.Single nextMessages
        Assert.Equal("assistant", msg.role)
        Assert.Equal("I have completed the task.", msg.content)
    | Agent.Continue _ -> failwith "Expected Stop, but got Continue"

[<Fact>]
let ``handleResponse returns Stop action when tool_calls is empty array`` () =
    let responseMsg =
        { role = "assistant"
          content = "Done."
          tool_calls = [||] }

    let action = Agent.handleResponse mockConfig [] responseMsg

    match action with
    | Agent.Stop(content, _) -> Assert.Equal("Done.", content)
    | Agent.Continue _ -> failwith "Expected Stop, but got Continue"

[<Fact>]
let ``handleResponse appends assistant message to currentMessages`` () =
    let existingMsg = LlmClient.userMessage "Hello"

    let responseMsg =
        { role = "assistant"
          content = "Hi there."
          tool_calls = null }

    let action = Agent.handleResponse mockConfig [ existingMsg ] responseMsg

    match action with
    | Agent.Stop(_, msgs) ->
        Assert.Equal(2, msgs.Length)
        Assert.Equal("user", msgs.[0].role)
        Assert.Equal("assistant", msgs.[1].role)
    | Agent.Continue _ -> failwith "Expected Stop, but got Continue"

[<Fact>]
let ``handleResponse returns Continue action for assistant message with tool calls`` () =
    let dummyToolCall =
        { id = "call_123"
          ``type`` = "function"
          ``function`` =
            { name = "read_file"
              arguments = "{\"file_path\": \"non_existent.txt\"}" } }

    let responseMsg =
        { role = "assistant"
          content = null
          tool_calls = [| dummyToolCall |] }

    let currentMessages = []
    let action = Agent.handleResponse mockConfig currentMessages responseMsg

    match action with
    | Agent.Continue nextMessages ->
        Assert.Equal(2, nextMessages.Length)
        let assistantMsg = nextMessages.[0]
        Assert.Equal("assistant", assistantMsg.role)
        Assert.Equal(1, assistantMsg.tool_calls.Length)
        let resultMsg = nextMessages.[1]
        Assert.Equal("tool", resultMsg.role)
        Assert.Equal("call_123", resultMsg.tool_call_id)
        Assert.Contains("not found", resultMsg.content)
    | Agent.Stop _ -> failwith "Expected Continue, but got Stop"

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

    let toolCall =
        { id = "call_ok"
          ``type`` = "function"
          ``function`` =
            { name = "read_file"
              arguments = "{\"file_path\": \"test.txt\"}" } }

    let responseMsg =
        { role = "assistant"
          content = null
          tool_calls = [| toolCall |] }

    let action = Agent.handleResponse config [] responseMsg

    match action with
    | Agent.Continue nextMessages ->
        Assert.True(output |> List.exists (fun s -> s.Contains("✅")))
        Assert.Equal(2, nextMessages.Length)
        let toolResult = nextMessages.[1]
        Assert.Equal("tool", toolResult.role)
        Assert.Contains("file content", toolResult.content)
    | Agent.Stop _ -> Assert.Fail "Expected Continue, got Stop"

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

    let toolCall =
        { id = "call_fail"
          ``type`` = "function"
          ``function`` =
            { name = "read_file"
              arguments = "{\"file_path\": \"bad.txt\"}" } }

    let responseMsg =
        { role = "assistant"
          content = null
          tool_calls = [| toolCall |] }

    let action = Agent.handleResponse config [] responseMsg

    match action with
    | Agent.Continue nextMessages ->
        Assert.True(output |> List.exists (fun s -> s.Contains "❌"))
        let toolResult = nextMessages.[1]
        Assert.Equal("tool", toolResult.role)
        Assert.Contains("something went wrong", toolResult.content)
    | Agent.Stop _ -> Assert.Fail "Expected Continue, got Stop"

[<Fact>]
let ``runLoop returns Ok with response content when LLM returns a final answer`` () =
    task {
        let mockClient =
            fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

        let messages = [ LlmClient.userMessage "Hello" ]
        let! result = Agent.runLoop mockConfig mockClient messages

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
        let! result = Agent.runLoop mockConfig mockClient messages

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
        let! result = Agent.runLoop mockConfig mockClient messages

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
        let! result = Agent.runLoop mockConfig mockClient messages

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
        let! result = Agent.runLoop mockConfig mockClient messages

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
        let! result = Agent.runLoop mockConfig mockClient messages

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
    let _, pTokens, cTokens = Agent.runAgentLoop config mockClient messages messages
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
    let returnedMsgs, _, _ = Agent.runAgentLoop config mockClient messages messages
    Assert.True(output |> List.exists (fun s -> s.Contains "❌"))
    Assert.Equal<ChatMessage list>(messages, returnedMsgs)

[<Fact>]
let ``truncateMessages does not truncate if length is within limits`` () =
    let sysMsg = LlmClient.systemMessage "System"
    let userMsg = LlmClient.userMessage "Hello"
    let messages = [ sysMsg; userMsg ]
    let result = Agent.truncateMessages 20 messages
    Assert.Equal(2, result.Length)
    Assert.Equal("system", result.[0].role)

[<Fact>]
let ``truncateMessages returns empty list for empty input`` () =
    let result = Agent.truncateMessages 20 []
    Assert.Empty result

[<Fact>]
let ``truncateMessages truncates to maxHistory + 1 if exceeded`` () =
    let sysMsg = LlmClient.systemMessage "System"

    let messages =
        sysMsg :: [ for i in 1..25 -> LlmClient.userMessage (sprintf "Msg %d" i) ]

    let result = Agent.truncateMessages 20 messages
    Assert.Equal(21, result.Length)
    Assert.Equal("system", result.[0].role)
    Assert.Equal("user", result.[1].role)
    Assert.Equal("Msg 6", result.[1].content)

[<Fact>]
let ``truncateMessages removes orphaned tool message after truncation`` () =
    let sysMsg = LlmClient.systemMessage "System"

    let makeMsg i =
        if i = 6 then
            { role = "tool"
              content = "tool result"
              name = "some_tool"
              tool_call_id = "123"
              tool_calls = null }
        else
            LlmClient.userMessage (sprintf "Msg %d" i)

    let messages = sysMsg :: [ for i in 1..25 -> makeMsg i ]
    let result = Agent.truncateMessages 20 messages
    Assert.Equal(20, result.Length)
    Assert.Equal("system", result.[0].role)
    Assert.Equal("user", result.[1].role)
    Assert.Equal("Msg 7", result.[1].content)

[<Fact>]
let ``truncateMessages keeps all messages if exactly at limit`` () =
    let sysMsg = LlmClient.systemMessage "System"

    let messages =
        sysMsg :: [ for i in 1..20 -> LlmClient.userMessage (sprintf "Msg %d" i) ]

    let result = Agent.truncateMessages 20 messages
    Assert.Equal(21, result.Length)

[<Fact>]
let ``printUsage writes formatted token count to output`` () =
    let mutable output = []

    let config =
        { mockConfig with
            writeLine = fun s -> output <- output @ [ s ] }

    Agent.printUsage config 100 50
    let line = Assert.Single output
    Assert.Contains("100", line)
    Assert.Contains("50", line)
    Assert.Contains("150", line)

[<Fact>]
let ``repl exits immediately on '/exit' input`` () =
    let mutable output = []

    let config =
        { mockConfig with
            writeLine = fun s -> output <- output @ [ s ]
            readLine = fun () -> "/exit" }

    let mockClient =
        fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

    Agent.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    Assert.Contains("Goodbye!", output)

[<Fact>]
let ``repl clears context on '/clear' input then exits`` () =
    let mutable output = []
    let mutable callCount = 0

    let config =
        { mockConfig with
            writeLine = fun s -> output <- output @ [ s ]
            readLine =
                fun () ->
                    callCount <- callCount + 1
                    if callCount = 1 then "/clear" else "/exit" }

    let mockClient =
        fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

    Agent.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    Assert.Contains("🧹 Context cleared.", output)
    Assert.Contains("Goodbye!", output)

[<Fact>]
let ``repl skips blank input and processes next non-blank input`` () =
    let mutable output = []
    let mutable callCount = 0

    let config =
        { mockConfig with
            writeLine = fun s -> output <- output @ [ s ]
            readLine =
                fun () ->
                    callCount <- callCount + 1
                    if callCount <= 2 then "" else "/exit" }

    let mockClient =
        fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

    Agent.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    Assert.Contains("Goodbye!", output)
    Assert.True(callCount >= 3)

[<Fact>]
let ``repl processes user message and prints response then exits`` () =
    let mutable output = []
    let mutable callCount = 0

    let config =
        { mockConfig with
            writeLine = fun s -> output <- output @ [ s ]
            readLine =
                fun () ->
                    callCount <- callCount + 1
                    if callCount = 1 then "Hello agent" else "/exit" }

    let mockClient =
        fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

    Agent.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    Assert.True(output |> List.exists (fun s -> s.Contains "Hello!"))

[<Fact>]
let ``repl prints error message and continues when runLoop returns Error`` () =
    let mutable output = []
    let mutable callCount = 0

    let config =
        { mockConfig with
            writeLine = fun s -> output <- output @ [ s ]
            readLine =
                fun () ->
                    callCount <- callCount + 1
                    if callCount = 1 then "Hello" else "/exit" }

    let mutable clientCallCount = 0

    let mockClient =
        fun _json ->
            clientCallCount <- clientCallCount + 1

            System.Threading.Tasks.Task.FromResult(
                makeErrorResponse System.Net.HttpStatusCode.InternalServerError "Server Error" "{}"
            )

    Agent.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    Assert.True(output |> List.exists (fun s -> s.Contains "error occurred" || s.Contains "❌"))
    Assert.Contains("Goodbye!", output)

[<Fact>]
let ``repl prints error message and continues when runLoop throws`` () =
    let mutable output = []
    let mutable callCount = 0

    let config =
        { mockConfig with
            writeLine = fun s -> output <- output @ [ s ]
            readLine =
                fun () ->
                    callCount <- callCount + 1
                    if callCount = 1 then "Trigger exception" else "/exit" }

    let mockClient =
        fun (_json: string) ->
            System.Threading.Tasks.Task.FromException<System.Net.Http.HttpResponseMessage>(System.Exception "boom")

    Agent.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    Assert.True(output |> List.exists (fun s -> s.Contains "❌" || s.Contains "error"))
    Assert.Contains("Goodbye!", output)

[<Fact>]
let ``loadAgentsMd returns content for valid file`` () =
    let tempFile = System.IO.Path.GetTempFileName()

    try
        System.IO.File.WriteAllText(tempFile, "Setup: dotnet build")
        let result = Agent.loadAgentsMd tempFile
        Assert.True result.IsSome
        Assert.Equal("Setup: dotnet build", result.Value)
    finally
        if System.IO.File.Exists tempFile then
            System.IO.File.Delete tempFile

[<Fact>]
let ``loadAgentsMd returns None for non-existent file`` () =
    let result = Agent.loadAgentsMd "nonexistent_agents.md"
    Assert.True result.IsNone

[<Fact>]
let ``loadAgentsMd returns None for empty or whitespace file`` () =
    let tempFile = System.IO.Path.GetTempFileName()

    try
        System.IO.File.WriteAllText(tempFile, "   \n\t  ")
        let result = Agent.loadAgentsMd tempFile
        Assert.True result.IsNone
    finally
        if System.IO.File.Exists tempFile then
            System.IO.File.Delete tempFile

[<Fact>]
let ``loadAgentsMd returns None when exception is thrown`` () =
    let result = Agent.loadAgentsMd null
    Assert.True result.IsNone

[<Fact>]
let ``start prints startup banner and begins repl`` () =
    let mutable output = []

    let config =
        { mockConfig with
            writeLine = fun s -> output <- output @ [ s ]
            readLine = fun () -> "/exit" }

    let mockClient =
        fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

    Agent.start config mockClient
    Assert.True(output |> List.exists (fun s -> s.Contains "Coding Agent started"))
    Assert.Contains("Goodbye!", output)

[<Fact>]
let ``start loads AGENTS.md content when file exists`` () =
    let hasPreexisting = System.IO.File.Exists "AGENTS.md"

    let backupContent =
        if hasPreexisting then
            Some(System.IO.File.ReadAllText "AGENTS.md")
        else
            None

    try
        System.IO.File.WriteAllText("AGENTS.md", "Setup: test build")
        let mutable output = []

        let config =
            { mockConfig with
                writeLine = fun s -> output <- output @ [ s ]
                readLine = fun () -> "/exit" }

        let mockClient =
            fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

        Agent.start config mockClient

        Assert.True(
            output
            |> List.exists (fun s -> s.Contains "Loaded project instructions from AGENTS.md")
        )
    finally
        if hasPreexisting then
            System.IO.File.WriteAllText("AGENTS.md", backupContent.Value)
        elif System.IO.File.Exists "AGENTS.md" then
            System.IO.File.Delete "AGENTS.md"
