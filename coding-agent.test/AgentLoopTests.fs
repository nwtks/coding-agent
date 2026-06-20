module CodingAgent.AgentLoopTests

open Xunit
open CodingAgent
open TestHelpers

[<Theory>]
[<InlineData("empty", 0, "")>]
[<InlineData("non-tool-last", 2, "")>]
[<InlineData("single-leading-tool", 1, "user")>]
[<InlineData("multiple-leading-tools", 1, "user")>]
[<InlineData("all-tools", 0, "")>]
let ``dropTrailingTool removes trailing tool messages``
    (scenario: string, expectedCount: int, expectedFirstRole: string)
    =
    let msgs =
        match scenario with
        | "empty" -> []
        | "non-tool-last" -> [ LlmClient.userMessage "Hello"; LlmClient.assistantMessage "Hi" ]
        | "single-leading-tool" ->
            [ LlmClient.toolResultMessage "1" (AgentToolCall.ToolName.toString AgentToolCall.ReadFile) "result"
              LlmClient.userMessage "Hello" ]
        | "multiple-leading-tools" ->
            [ LlmClient.toolResultMessage "1" "t1" "r1"
              LlmClient.toolResultMessage "2" "t2" "r2"
              LlmClient.userMessage "Hello" ]
        | "all-tools" ->
            [ LlmClient.toolResultMessage "1" "t1" "r1"
              LlmClient.toolResultMessage "2" "t2" "r2" ]
        | _ -> failwith "unknown scenario"

    let result = AgentLoop.dropTrailingTool msgs
    Assert.Equal(expectedCount, result.Length)

    if expectedFirstRole <> "" then
        Assert.Equal(expectedFirstRole, result.[0].role)

[<Fact>]
let ``truncateMessages keeps all messages when total count is within maxHistory limit`` () =
    let sysMsg = LlmClient.systemMessage "System"
    let userMsg = LlmClient.userMessage "Hello"
    let messages = [ sysMsg; userMsg ]
    let result = AgentLoop.truncateMessages 20 messages
    Assert.Equal(2, result.Length)
    Assert.Equal("system", result.[0].role)

[<Fact>]
let ``truncateMessages truncates to maxHistory + 1 if exceeded`` () =
    let sysMsg = LlmClient.systemMessage "System"
    let messages = sysMsg :: [ for i in 1..25 -> LlmClient.userMessage $"Msg {i}" ]
    let result = AgentLoop.truncateMessages 20 messages
    Assert.Equal(21, result.Length)
    Assert.Equal("system", result.[0].role)
    Assert.Equal("user", result.[1].role)
    Assert.Equal("Msg 6", result.[1].content)

[<Fact>]
let ``truncateMessages removes orphaned tool message after truncation`` () =
    let sysMsg = LlmClient.systemMessage "System"

    let makeMsg i : LlmClient.ChatMessage =
        if i = 6 || i = 7 then
            { role = "tool"
              content = "tool result"
              name = "some_tool"
              tool_call_id = "123"
              tool_calls = null }
        else
            LlmClient.userMessage ($"Msg {i}")

    let messages = sysMsg :: [ for i in 1..25 -> makeMsg i ]
    let result = AgentLoop.truncateMessages 20 messages
    Assert.Equal(19, result.Length)
    Assert.Equal("system", result.[0].role)
    Assert.Equal("user", result.[1].role)
    Assert.Equal("Msg 8", result.[1].content)

[<Fact>]
let ``truncateMessages keeps all messages when total count exactly equals maxHistory + 1`` () =
    let sysMsg = LlmClient.systemMessage "System"
    let messages = sysMsg :: [ for i in 1..20 -> LlmClient.userMessage $"Msg {i}" ]
    let result = AgentLoop.truncateMessages 20 messages
    Assert.Equal(21, result.Length)

[<Fact>]
let ``truncateMessages returns empty list when input message list is empty`` () =
    let result = AgentLoop.truncateMessages 20 []
    Assert.Empty result

[<Fact>]
let ``printUsage writes formatted token count to output`` () =
    let mutable output = []

    let config =
        let cfg = mockAgentConfig ()

        { cfg with
            interactive =
                { cfg.interactive with
                    writeLine = fun s -> output <- output @ [ s ] } }

    AgentLoop.printUsage config 100 50
    let line = Assert.Single output
    Assert.Contains("100", line)
    Assert.Contains("50", line)
    Assert.Contains("150", line)

[<Fact>]
let ``splitCommand splits by spaces and trims`` () =
    let parts = AgentLoop.splitCommand "  /save   my-session  "
    Assert.Equal(2, parts.Length)
    Assert.Equal("/save", parts.[0])
    Assert.Equal("my-session", parts.[1])

[<Theory>]
[<InlineData("on", true, "All")>]
[<InlineData("off", true, "Off")>]
[<InlineData("reads", true, "ReadsOnly")>]
[<InlineData("invalid", false, "Off")>]
let ``setAutoConfirmMode returns config for valid mode names and None for unrecognized``
    (modeStr: string, expectSome: bool, expectedModeName: string)
    =
    let result = AgentLoop.setAutoConfirmMode (mockAgentConfig ()) modeStr

    let expectedMode =
        match expectedModeName with
        | "All" -> All
        | "Off" -> Off
        | "ReadsOnly" -> ReadsOnly
        | _ -> failwith "unexpected"

    match result with
    | Some cfg ->
        Assert.True expectSome
        Assert.Equal(expectedMode, cfg.runtimeConfig.autoConfirm)
    | None -> Assert.False expectSome

[<Theory>]
[<InlineData("/autoconfirm on", true, "All")>]
[<InlineData("/autoconfirm off", true, "Off")>]
[<InlineData("/autoconfirm reads", true, "ReadsOnly")>]
let ``handleAutoConfirmCommand returns config with specified mode for valid /autoconfirm commands``
    (command: string, expectSome: bool, expectedModeName: string)
    =
    let result = AgentLoop.handleAutoConfirmCommand (mockAgentConfig ()) command

    let expectedMode =
        match expectedModeName with
        | "All" -> All
        | "Off" -> Off
        | "ReadsOnly" -> ReadsOnly
        | _ -> failwith "unexpected"

    match result with
    | Some cfg ->
        Assert.True expectSome
        Assert.Equal(expectedMode, cfg.runtimeConfig.autoConfirm)
    | None -> Assert.Fail "Expected Some config"

[<Theory>]
[<InlineData("/autoconfirm invalid")>]
[<InlineData("/autoconfirm")>]
[<InlineData("/save test")>]
let ``handleAutoConfirmCommand returns None for unrecognized, missing, or unrelated commands`` (command: string) =
    let result = AgentLoop.handleAutoConfirmCommand (mockAgentConfig ()) command
    Assert.True result.IsNone

[<Theory>]
[<InlineData("named")>]
[<InlineData("timestamped")>]
let ``handleSaveCommand saves session to a named file or timestamped filename`` (scenario: string) =
    let mutable output = []
    let store = mockSessionStore ()

    let config =
        let cfg = mockAgentConfig ()

        { cfg with
            sessionStore = store
            interactive =
                { cfg.interactive with
                    writeLine = fun s -> output <- output @ [ s ] } }

    let msgs = [ LlmClient.userMessage "Hello" ]
    let command = if scenario = "named" then "/save my-session" else "/save"
    let result = AgentLoop.handleSaveCommand config command msgs
    Assert.True result

    let expectedName =
        if scenario = "named" then
            "my-session.jsonl"
        else
            "20250102-040506.jsonl"

    Assert.True(
        output
        |> List.exists (fun s -> s.Contains "Session saved to" && s.Contains expectedName)
    )

[<Fact>]
let ``handleSaveCommand prints error message when session save fails`` () =
    let mutable output = []

    let store =
        { mockSessionStore () with
            saveSession = fun _ _ -> Error "Disk full" }

    let config =
        let cfg = mockAgentConfig ()

        { cfg with
            sessionStore = store
            interactive =
                { cfg.interactive with
                    writeLine = fun s -> output <- output @ [ s ] } }

    let result =
        AgentLoop.handleSaveCommand config "/save test" [ LlmClient.userMessage "Hello" ]

    Assert.True result
    Assert.True(output |> List.exists (fun s -> s.Contains "❌" && s.Contains "Disk full"))

[<Fact>]
let ``handleSaveCommand returns false when command is not a /save command`` () =
    let result =
        AgentLoop.handleSaveCommand (mockAgentConfig ()) "/load test" [ LlmClient.userMessage "Hello" ]

    Assert.False result

[<Fact>]
let ``handleLoadCommand loads previously saved session messages by name`` () =
    let mutable output = []
    let store = mockSessionStore ()
    let msgs = [ LlmClient.userMessage "Previous" ]
    store.saveSession (store.sessionPath "existing") msgs |> ignore

    let config =
        let cfg = mockAgentConfig ()

        { cfg with
            sessionStore = store
            interactive =
                { cfg.interactive with
                    writeLine = fun s -> output <- output @ [ s ] } }

    let result = AgentLoop.handleLoadCommand config "/load existing"

    match result with
    | Some loaded ->
        Assert.Equal(1, loaded.Length)
        Assert.Equal("Previous", loaded.[0].content)
        Assert.True(output |> List.exists (fun s -> s.Contains "Session loaded from"))
    | None -> Assert.Fail "Expected Some loaded messages"

[<Fact>]
let ``handleLoadCommand prints error message when session file cannot be loaded`` () =
    let mutable output = []
    let store = mockSessionStore ()

    let config =
        let cfg = mockAgentConfig ()

        { cfg with
            sessionStore = store
            interactive =
                { cfg.interactive with
                    writeLine = fun s -> output <- output @ [ s ] } }

    let result = AgentLoop.handleLoadCommand config "/load nonexistent"
    Assert.True result.IsNone

    Assert.True(
        output
        |> List.exists (fun s -> s.Contains "❌" && s.Contains "Session file not found")
    )

[<Theory>]
[<InlineData("with-sessions")>]
[<InlineData("empty")>]
let ``handleLoadCommand lists sessions or shows empty message when no name provided`` (scenario: string) =
    let mutable output = []
    let store = mockSessionStore ()

    if scenario = "with-sessions" then
        store.saveSession (store.sessionPath "alpha") [ LlmClient.userMessage "A" ]
        |> ignore

    let config =
        let cfg = mockAgentConfig ()

        { cfg with
            sessionStore = store
            interactive =
                { cfg.interactive with
                    writeLine = fun s -> output <- output @ [ s ] } }

    let result = AgentLoop.handleLoadCommand config "/load"
    Assert.True result.IsNone

    if scenario = "with-sessions" then
        Assert.True(output |> List.exists (fun s -> s.Contains "Available sessions"))
    else
        Assert.True(output |> List.exists (fun s -> s.Contains "No saved sessions"))

[<Fact>]
let ``handleLoadCommand returns None when command is not a /load command`` () =
    let result = AgentLoop.handleLoadCommand (mockAgentConfig ()) "/save test"
    Assert.True result.IsNone

[<Fact>]
let ``handleInput returns Continue action for empty string input`` () =
    let result = AgentLoop.handleInput (mockAgentConfig ()) [] ""
    Assert.Equal(AgentLoop.Continue, result)

[<Fact>]
let ``handleInput returns Continue action for whitespace-only string input`` () =
    let result = AgentLoop.handleInput (mockAgentConfig ()) [] "   "
    Assert.Equal(AgentLoop.Continue, result)

[<Fact>]
let ``handleInput returns Exit action for /exit command`` () =
    let result = AgentLoop.handleInput (mockAgentConfig ()) [] "/exit"
    Assert.Equal(AgentLoop.Exit, result)

[<Fact>]
let ``handleInput returns Clear action for /clear command`` () =
    let result = AgentLoop.handleInput (mockAgentConfig ()) [] "/clear"
    Assert.Equal(AgentLoop.Clear, result)

[<Fact>]
let ``handleInput returns AutoConfirm with new config for /autoconfirm on`` () =
    let result = AgentLoop.handleInput (mockAgentConfig ()) [] "/autoconfirm on"

    match result with
    | AgentLoop.AutoConfirm cfg -> Assert.Equal(All, cfg.runtimeConfig.autoConfirm)
    | _ -> Assert.Fail "Expected AutoConfirm"

[<Fact>]
let ``handleInput returns Continue for /autoconfirm`` () =
    let result = AgentLoop.handleInput (mockAgentConfig ()) [] "/autoconfirm"
    Assert.Equal(AgentLoop.Continue, result)

[<Fact>]
let ``handleInput returns Continue for /save`` () =
    let result = AgentLoop.handleInput (mockAgentConfig ()) [] "/save test"
    Assert.Equal(AgentLoop.Continue, result)

[<Fact>]
let ``handleInput returns Load Some for /load with existing session`` () =
    let store = mockSessionStore ()

    store.saveSession (store.sessionPath "existing") [ LlmClient.userMessage "hi" ]
    |> ignore

    let config =
        { mockAgentConfig () with
            sessionStore = store }

    let result = AgentLoop.handleInput config [] "/load existing"

    match result with
    | AgentLoop.Load msgs -> Assert.Single msgs |> ignore
    | _ -> Assert.Fail "Expected Load msgs"

[<Fact>]
let ``handleInput returns Continue for /load with no name`` () =
    let result = AgentLoop.handleInput (mockAgentConfig ()) [] "/load"
    Assert.Equal(AgentLoop.Continue, result)

[<Fact>]
let ``handleInput returns Query action for non-command natural language input`` () =
    let result = AgentLoop.handleInput (mockAgentConfig ()) [] "Hello agent"

    match result with
    | AgentLoop.Query _ -> ()
    | _ -> Assert.Fail "Expected Query"

[<Fact>]
let ``repl exits main loop when user enters /exit command`` () =
    let mutable output = []

    let config =
        let cfg = mockAgentConfig ()

        { cfg with
            interactive =
                { cfg.interactive with
                    writeLine = fun s -> output <- output @ [ s ]
                    readLine = fun () -> "/exit" } }

    let mockClient =
        fun _json -> async { return makeSuccessResponse validChatResponseJson }

    AgentLoop.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    |> Async.RunSynchronously

    Assert.Contains("Goodbye!", output)

[<Fact>]
let ``repl auto-saves session history to file before exiting`` () =
    let mutable output = []
    let mutable callCount = 0

    let config =
        let cfg = mockAgentConfig ()

        { cfg with
            interactive =
                { cfg.interactive with
                    writeLine = fun s -> output <- output @ [ s ]
                    readLine =
                        fun () ->
                            callCount <- callCount + 1
                            if callCount = 1 then "Hello" else "/exit" } }

    let mockClient =
        fun _json -> async { return makeSuccessResponse validChatResponseJson }

    AgentLoop.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    |> Async.RunSynchronously

    Assert.True(output |> List.exists (fun s -> s.Contains "Session auto-saved to"))
    Assert.Contains("Goodbye!", output)

[<Fact>]
let ``repl clears conversation context on /clear command`` () =
    let mutable output = []
    let mutable callCount = 0

    let config =
        let cfg = mockAgentConfig ()

        { cfg with
            interactive =
                { cfg.interactive with
                    writeLine = fun s -> output <- output @ [ s ]
                    readLine =
                        fun () ->
                            callCount <- callCount + 1
                            if callCount = 1 then "/clear" else "/exit" } }

    let mockClient =
        fun _json -> async { return makeSuccessResponse validChatResponseJson }

    AgentLoop.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    |> Async.RunSynchronously

    Assert.True(output |> List.exists (fun s -> s.Contains "Context cleared"))

[<Fact>]
let ``repl updates auto-confirm mode on /autoconfirm on command and continues loop`` () =
    let mutable output = []
    let mutable callCount = 0

    let config =
        let cfg = mockAgentConfig ()

        { cfg with
            interactive =
                { cfg.interactive with
                    writeLine = fun s -> output <- output @ [ s ]
                    readLine =
                        fun () ->
                            callCount <- callCount + 1
                            if callCount = 1 then "/autoconfirm on" else "/exit" } }

    let mockClient =
        fun _json -> async { return makeSuccessResponse validChatResponseJson }

    AgentLoop.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    |> Async.RunSynchronously

    Assert.True(output |> List.exists (fun s -> s.Contains "Auto-confirm mode: ON"))

[<Fact>]
let ``repl saves current session to file on /save command`` () =
    let mutable output = []
    let mutable callCount = 0

    let config =
        let cfg = mockAgentConfig ()

        { cfg with
            interactive =
                { cfg.interactive with
                    writeLine = fun s -> output <- output @ [ s ]
                    readLine =
                        fun () ->
                            callCount <- callCount + 1
                            if callCount = 1 then "/save test-session" else "/exit" } }

    let mockClient =
        fun _json -> async { return makeSuccessResponse validChatResponseJson }

    AgentLoop.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    |> Async.RunSynchronously

    Assert.True(output |> List.exists (fun s -> s.Contains "Session saved to"))

[<Fact>]
let ``repl loads and restores existing session on /load command`` () =
    let mutable output = []
    let mutable callCount = 0
    let store = mockSessionStore ()
    let testMessages = [ LlmClient.userMessage "Previous session" ]
    store.saveSession (store.sessionPath "test-load") testMessages |> ignore

    let config =
        let cfg = mockAgentConfig ()

        { cfg with
            sessionStore = store
            interactive =
                { cfg.interactive with
                    writeLine = fun s -> output <- output @ [ s ]
                    readLine =
                        fun () ->
                            callCount <- callCount + 1
                            if callCount = 1 then "/load test-load" else "/exit" } }

    let mockClient =
        fun _json -> async { return makeSuccessResponse validChatResponseJson }

    AgentLoop.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    |> Async.RunSynchronously

    Assert.True(output |> List.exists (fun s -> s.Contains "Session loaded"))

[<Fact>]
let ``repl prints available session files on /load without arguments`` () =
    let mutable output = []
    let mutable callCount = 0

    let config =
        let cfg = mockAgentConfig ()

        { cfg with
            interactive =
                { cfg.interactive with
                    writeLine = fun s -> output <- output @ [ s ]
                    readLine =
                        fun () ->
                            callCount <- callCount + 1
                            if callCount = 1 then "/load" else "/exit" } }

    let mockClient =
        fun _json -> async { return makeSuccessResponse validChatResponseJson }

    AgentLoop.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    |> Async.RunSynchronously

    Assert.True(
        output
        |> List.exists (fun s -> s.Contains "Available sessions" || s.Contains "No saved sessions")
    )

[<Fact>]
let ``repl ignores blank or whitespace-only user input`` () =
    let mutable output = []
    let mutable callCount = 0

    let config =
        let cfg = mockAgentConfig ()

        { cfg with
            interactive =
                { cfg.interactive with
                    writeLine = fun s -> output <- output @ [ s ]
                    readLine =
                        fun () ->
                            callCount <- callCount + 1
                            if callCount <= 2 then "" else "/exit" } }

    let mockClient =
        fun _json -> async { return makeSuccessResponse validChatResponseJson }

    AgentLoop.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    |> Async.RunSynchronously

    Assert.Contains("Goodbye!", output)
    Assert.True(callCount >= 3)

[<Fact>]
let ``repl sends user query to LLM and displays assistant response`` () =
    let mutable output = []
    let mutable callCount = 0

    let config =
        let cfg = mockAgentConfig ()

        { cfg with
            interactive =
                { cfg.interactive with
                    writeLine = fun s -> output <- output @ [ s ]
                    readLine =
                        fun () ->
                            callCount <- callCount + 1
                            if callCount = 1 then "Hello agent" else "/exit" } }

    let mockClient =
        fun _json -> async { return makeSuccessResponse validChatResponseJson }

    AgentLoop.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    |> Async.RunSynchronously

    Assert.True(output |> List.exists (fun s -> s.Contains "Hello!"))

[<Fact>]
let ``repl displays error message and continues execution on API failure`` () =
    let mutable output = []
    let mutable callCount = 0

    let config =
        let cfg = mockAgentConfig ()

        { cfg with
            interactive =
                { cfg.interactive with
                    writeLine = fun s -> output <- output @ [ s ]
                    readLine =
                        fun () ->
                            callCount <- callCount + 1
                            if callCount = 1 then "Hello" else "/exit" } }

    let mutable clientCallCount = 0

    let mockClient =
        fun _json ->
            async {
                clientCallCount <- clientCallCount + 1
                return makeErrorResponse System.Net.HttpStatusCode.InternalServerError "Server Error" "{}"
            }

    AgentLoop.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    |> Async.RunSynchronously

    Assert.True(output |> List.exists (fun s -> s.Contains "error occurred" || s.Contains "❌"))
    Assert.Contains("Goodbye!", output)


[<Theory>]
[<InlineData("test_file.md", "Setup: dotnet build", true, true)>]
[<InlineData("nonexistent.md", "", false, false)>]
[<InlineData("empty_file.md", "   \n\t  ", true, false)>]
let ``loadFileContent returns content when file exists and None when not found or empty``
    (fileName: string, fileContent: string, addFile: bool, expectSome: bool)
    =
    let mock = MockFileSystem()

    if addFile then
        mock.AddFile fileName fileContent

    let fs = mock.FileSystem

    let config =
        { mockAgentConfig () with
            fileSystem = fs }

    let result = AgentLoop.loadFileContent config fileName

    if expectSome then
        Assert.True result.IsSome
        Assert.Equal(fileContent, result.Value)
    else
        Assert.True result.IsNone

[<Theory>]
[<InlineData(true, "Setup: dotnet test")>]
[<InlineData(false, "")>]
let ``updateConfig appends AGENTS.md content when file exists and leaves prompt unchanged when missing``
    (addFile: bool, fileContent: string)
    =
    let mock = MockFileSystem()

    if addFile then
        mock.AddFile "AGENTS.md" fileContent

    let fs = mock.FileSystem
    let mutable output = []

    let config =
        let cfg = mockAgentConfig ()

        { cfg with
            interactive =
                { cfg.interactive with
                    writeLine = fun s -> output <- output @ [ s ] }
            runtimeConfig =
                { cfg.runtimeConfig with
                    systemPrompt = "Base prompt" }
            fileSystem = fs }

    let updated = AgentLoop.updateConfig config

    if addFile then
        Assert.True(output |> List.exists (fun s -> s.Contains "Loaded project instructions"))
        Assert.Contains("Base prompt", updated.runtimeConfig.systemPrompt)
        Assert.Contains(fileContent, updated.runtimeConfig.systemPrompt)
    else
        Assert.Equal("Base prompt", updated.runtimeConfig.systemPrompt)

[<Theory>]
[<InlineData("none")>]
[<InlineData("valid")>]
[<InlineData("invalid")>]
let ``initializeSession returns system message when no session, loads session when name provided, and shows error for invalid``
    (scenario: string)
    =
    let mutable output = []
    let store = mockSessionStore ()

    if scenario = "valid" then
        store.saveSession (store.sessionPath "myload") [ LlmClient.userMessage "Previous" ]
        |> ignore

    let config =
        let cfg = mockAgentConfig ()

        { cfg with
            sessionStore = store
            interactive =
                { cfg.interactive with
                    writeLine = fun s -> output <- output @ [ s ] }
            runtimeConfig =
                { cfg.runtimeConfig with
                    systemPrompt = "Test system" } }

    let sessionName =
        match scenario with
        | "none" -> None
        | "valid" -> Some "myload"
        | "invalid" -> Some "nonexistent_session_xyz"
        | _ -> failwith "unknown scenario"

    let msgs = AgentLoop.initializeSession sessionName config

    match scenario with
    | "none" ->
        Assert.Equal(1, msgs.Length)
        Assert.Equal("system", msgs.[0].role)
        Assert.Equal("Test system", msgs.[0].content)
    | "valid" ->
        Assert.Equal(2, msgs.Length)
        Assert.Equal("system", msgs.[0].role)
        Assert.Equal("Previous", msgs.[1].content)
        Assert.True(output |> List.exists (fun s -> s.Contains "Session loaded from"))
    | "invalid" ->
        Assert.Equal(1, msgs.Length)
        Assert.Equal("system", msgs.[0].role)
        Assert.Equal("Test system", msgs.[0].content)
        Assert.True(output |> List.exists (fun s -> s.Contains "❌"))
    | _ -> failwith "unknown scenario"

[<Fact>]
let ``start prints startup banner and begins repl`` () =
    let mutable output = []

    let config =
        let cfg = mockAgentConfig ()

        { cfg with
            interactive =
                { cfg.interactive with
                    writeLine = fun s -> output <- output @ [ s ]
                    readLine = fun () -> "/exit" } }

    let mockClient =
        fun _json -> async { return makeSuccessResponse validChatResponseJson }

    AgentLoop.start None config mockClient
    Assert.True(output |> List.exists (fun s -> s.Contains "Coding Agent started"))
    Assert.Contains("Goodbye!", output)

[<Fact>]
let ``start with sessionToLoad loads existing session`` () =
    let mutable output = []

    let messages =
        [ LlmClient.systemMessage "Custom system"
          LlmClient.userMessage "Previous question"
          LlmClient.assistantMessage "Previous answer" ]

    let store = mockSessionStore ()
    store.saveSession (store.sessionPath "testload") messages |> ignore

    let config =
        let cfg = mockAgentConfig ()

        { cfg with
            sessionStore = store
            interactive =
                { cfg.interactive with
                    writeLine = fun s -> output <- output @ [ s ]
                    readLine = fun () -> "/exit" } }

    let mockClient =
        fun _json -> async { return makeSuccessResponse validChatResponseJson }

    AgentLoop.start (Some "testload") config mockClient

    Assert.True(
        output
        |> List.exists (fun s -> s.Contains "Session loaded from" && s.Contains "testload.jsonl")
    )
