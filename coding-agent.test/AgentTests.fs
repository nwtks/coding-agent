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
          writeFile = fun path content -> Ok(sprintf "Successfully wrote to '%s'." path)
          runCommand = fun cmd cwd -> Ok(sprintf "Output of %s in %s" cmd cwd)
          listDirectory = fun path -> Ok(sprintf "Contents of directory '%s':" path) }
      write = ignore
      writeLine = ignore
      readLine = fun () -> ""
      systemPrompt = "You are helpful"
      maxHistory = 20 }

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
let ``handleResponse returns Stop action for assistant message without tool calls`` () =
    let responseMsg =
        { role = "assistant"
          content = "I have completed the task."
          tool_calls = null }

    let currentMessages = []
    let action = Agent.handleResponse mockConfig responseMsg currentMessages

    match action with
    | Stop(content, nextMessages) ->
        Assert.Equal("I have completed the task.", content)
        let msg = Assert.Single nextMessages
        Assert.Equal("assistant", msg.role)
        Assert.Equal("I have completed the task.", msg.content)
    | Continue _ -> failwith "Expected Stop, but got Continue"

[<Fact>]
let ``handleResponse returns Stop action when tool_calls is empty array`` () =
    let responseMsg =
        { role = "assistant"
          content = "Done."
          tool_calls = [||] }

    let action = Agent.handleResponse mockConfig responseMsg []

    match action with
    | Stop(content, _) -> Assert.Equal("Done.", content)
    | Continue _ -> failwith "Expected Stop, but got Continue"

[<Fact>]
let ``handleResponse appends assistant message to currentMessages`` () =
    let existingMsg = LlmClient.userMessage "Hello"

    let responseMsg =
        { role = "assistant"
          content = "Hi there."
          tool_calls = null }

    let action = Agent.handleResponse mockConfig responseMsg [ existingMsg ]

    match action with
    | Stop(_, msgs) ->
        Assert.Equal(2, msgs.Length)
        Assert.Equal("user", msgs.[0].role)
        Assert.Equal("assistant", msgs.[1].role)
    | Continue _ -> failwith "Expected Stop, but got Continue"

[<Fact>]
let ``handleResponse returns Continue action for assistant message with tool calls`` () =
    let dummyToolCall =
        { id = "call_123"
          ``type`` = "function"
          ``function`` =
            { name = "read_file"
              arguments = "{\"filePath\": \"non_existent.txt\"}" } }

    let responseMsg =
        { role = "assistant"
          content = null
          tool_calls = [| dummyToolCall |] }

    let currentMessages = []
    let action = Agent.handleResponse mockConfig responseMsg currentMessages

    match action with
    | Continue nextMessages ->
        Assert.Equal(2, nextMessages.Length)
        let assistantMsg = nextMessages.[0]
        Assert.Equal("assistant", assistantMsg.role)
        Assert.Equal(1, assistantMsg.tool_calls.Length)
        let resultMsg = nextMessages.[1]
        Assert.Equal("tool", resultMsg.role)
        Assert.Equal("call_123", resultMsg.tool_call_id)
        Assert.Contains("not found", resultMsg.content)
    | Stop _ -> failwith "Expected Continue, but got Stop"

[<Fact>]
let ``executeToolCall read_file returns file content on success`` () =
    let toolCall =
        { id = "call_1"
          ``type`` = "function"
          ``function`` =
            { name = "read_file"
              arguments = "{\"filePath\": \"test.txt\"}" } }

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
              arguments = "{\"filePath\": \"/definitely/does/not/exist.txt\"}" } }

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
    | Ok _ -> Assert.Fail("Expected Error for missing file")

[<Fact>]
let ``executeToolCall write_file writes content successfully`` () =
    let toolCall =
        { id = "call_2"
          ``type`` = "function"
          ``function`` =
            { name = "write_file"
              arguments = "{\"filePath\": \"test.txt\", \"content\": \"written by test\"}" } }

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
              arguments = "{\"commandLine\": \"echo hello from agent test\"}" } }

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
              arguments = "{\"commandLine\": \"echo in temp\", \"cwd\": \"/tmp\"}" } }

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
              arguments = "{\"directoryPath\": \"/tmp\"}" } }

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
    | Ok _ -> Assert.Fail("Expected Error for unknown tool")

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
    | Ok _ -> Assert.Fail("Expected Error for invalid JSON")

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
    Assert.Empty(result)

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
        | Error errMsg -> Assert.Contains("API Error:", errMsg)
        | Ok _ -> Assert.Fail("Expected Error but got Ok")
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
        | Error errMsg -> Assert.Contains("no choices", errMsg)
        | Ok _ -> Assert.Fail("Expected Error but got Ok")
    }

[<Fact>]
let ``runLoop returns Ok with response content when LLM returns a final answer`` () =
    task {
        let mockClient =
            fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

        let messages = [ LlmClient.userMessage "Hello" ]
        let! result = Agent.runLoop mockConfig mockClient messages

        match result with
        | Ok(content, updatedMessages) ->
            Assert.Equal("Hello!", content)
            Assert.Equal(2, updatedMessages.Length)
        | Error err -> Assert.Fail(sprintf "Expected Ok but got Error: %s" err)
    }

[<Fact>]
let ``runLoop continues loop when tool call is returned, then stops on next answer`` () =
    task {
        let toolCallJson =
            """{"id":"chatcmpl-1","choices":[{"index":0,"message":{"role":"assistant","content":"","tool_calls":[{"id":"call_1","type":"function","function":{"name":"read_file","arguments":"{\"filePath\":\"/nonexistent.txt\"}"}}]},"finish_reason":"tool_calls"}]}"""

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
        | Ok(content, _) ->
            Assert.Equal(2, callCount)
            Assert.Equal("Hello!", content)
        | Error err -> Assert.Fail(sprintf "Expected Ok but got Error: %s" err)
    }

[<Fact>]
let ``repl exits immediately on 'exit' input`` () =
    let mutable output = []

    let config =
        { mockConfig with
            writeLine = fun s -> output <- output @ [ s ]
            readLine = fun () -> "exit" }

    let mockClient =
        fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

    Agent.repl config mockClient [ LlmClient.systemMessage "System" ]
    Assert.Contains("Goodbye!", output)

[<Fact>]
let ``repl exits immediately on empty input`` () =
    let mutable output = []

    let config =
        { mockConfig with
            writeLine = fun s -> output <- output @ [ s ]
            readLine = fun () -> "" }

    let mockClient =
        fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

    Agent.repl config mockClient [ LlmClient.systemMessage "System" ]
    Assert.Contains("Goodbye!", output)

[<Fact>]
let ``repl clears context on 'clear' input then exits`` () =
    let mutable output = []
    let mutable callCount = 0

    let config =
        { mockConfig with
            writeLine = fun s -> output <- output @ [ s ]
            readLine =
                fun () ->
                    callCount <- callCount + 1
                    if callCount = 1 then "clear" else "exit" }

    let mockClient =
        fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

    Agent.repl config mockClient [ LlmClient.systemMessage "System" ]
    Assert.Contains("🧹 Context cleared.", output)
    Assert.Contains("Goodbye!", output)

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
                    if callCount = 1 then "Hello agent" else "exit" }

    let mockClient =
        fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

    Agent.repl config mockClient [ LlmClient.systemMessage "System" ]
    Assert.True(output |> List.exists (fun s -> s.Contains("Hello!")))

[<Fact>]
let ``start prints startup banner and begins repl`` () =
    let mutable output = []

    let config =
        { mockConfig with
            writeLine = fun s -> output <- output @ [ s ]
            readLine = fun () -> "exit" }

    let mockClient =
        fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

    Agent.start config mockClient
    Assert.True(output |> List.exists (fun s -> s.Contains("F# Coding Agent started")))
    Assert.Contains("Goodbye!", output)
