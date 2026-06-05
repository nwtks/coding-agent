module CodingAgent.AgentLoopTests

open Xunit
open CodingAgent
open TestHelpers

let validChatResponseJson =
    """{"id":"chatcmpl-123","choices":[{"index":0,"message":{"role":"assistant","content":"Hello!"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}"""

[<Fact>]
let ``truncateMessages does not truncate if length is within limits`` () =
    let sysMsg = LlmClient.systemMessage "System"
    let userMsg = LlmClient.userMessage "Hello"
    let messages = [ sysMsg; userMsg ]
    let result = AgentLoop.truncateMessages 20 messages
    Assert.Equal(2, result.Length)
    Assert.Equal("system", result.[0].role)

[<Fact>]
let ``truncateMessages truncates to maxHistory + 1 if exceeded`` () =
    let sysMsg = LlmClient.systemMessage "System"

    let messages =
        sysMsg :: [ for i in 1..25 -> LlmClient.userMessage (sprintf "Msg %d" i) ]

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
            LlmClient.userMessage (sprintf "Msg %d" i)

    let messages = sysMsg :: [ for i in 1..25 -> makeMsg i ]
    let result = AgentLoop.truncateMessages 20 messages
    Assert.Equal(19, result.Length)
    Assert.Equal("system", result.[0].role)
    Assert.Equal("user", result.[1].role)
    Assert.Equal("Msg 8", result.[1].content)

[<Fact>]
let ``truncateMessages keeps all messages if exactly at limit`` () =
    let sysMsg = LlmClient.systemMessage "System"

    let messages =
        sysMsg :: [ for i in 1..20 -> LlmClient.userMessage (sprintf "Msg %d" i) ]

    let result = AgentLoop.truncateMessages 20 messages
    Assert.Equal(21, result.Length)

[<Fact>]
let ``truncateMessages returns empty list for empty input`` () =
    let result = AgentLoop.truncateMessages 20 []
    Assert.Empty result

[<Fact>]
let ``printUsage writes formatted token count to output`` () =
    let mutable output = []

    let config =
        { mockAgentConfig with
            writeLine = fun s -> output <- output @ [ s ] }

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

[<Fact>]
let ``handleAutoConfirmCommand enables full auto-confirm for on subcommand`` () =
    let result = AgentLoop.handleAutoConfirmCommand mockAgentConfig "/autoconfirm on"

    match result with
    | Some cfg -> Assert.Equal(All, cfg.autoConfirm)
    | None -> Assert.Fail "Expected Some config"

[<Fact>]
let ``handleAutoConfirmCommand disables auto-confirm for off subcommand`` () =
    let result = AgentLoop.handleAutoConfirmCommand mockAgentConfig "/autoconfirm off"

    match result with
    | Some cfg -> Assert.Equal(Off, cfg.autoConfirm)
    | None -> Assert.Fail "Expected Some config"

[<Fact>]
let ``handleAutoConfirmCommand enables reads-only mode for reads subcommand`` () =
    let result = AgentLoop.handleAutoConfirmCommand mockAgentConfig "/autoconfirm reads"

    match result with
    | Some cfg -> Assert.Equal(ReadsOnly, cfg.autoConfirm)
    | None -> Assert.Fail "Expected Some config"

[<Fact>]
let ``handleAutoConfirmCommand returns None for invalid subcommand`` () =
    let result =
        AgentLoop.handleAutoConfirmCommand mockAgentConfig "/autoconfirm invalid"

    Assert.True result.IsNone

[<Fact>]
let ``handleAutoConfirmCommand returns None when subcommand is missing`` () =
    let result = AgentLoop.handleAutoConfirmCommand mockAgentConfig "/autoconfirm"
    Assert.True result.IsNone

[<Fact>]
let ``handleAutoConfirmCommand returns None for non-autoconfirm command`` () =
    let result = AgentLoop.handleAutoConfirmCommand mockAgentConfig "/save test"
    Assert.True result.IsNone

[<Fact>]
let ``handleSaveCommand saves session with custom name`` () =
    let mutable output = []
    let store = mockSessionStore ()

    let config =
        { mockAgentConfig with
            sessionStore = store
            writeLine = fun s -> output <- output @ [ s ] }

    let msgs = [ LlmClient.userMessage "Hello" ]
    let result = AgentLoop.handleSaveCommand config "/save my-session" msgs
    Assert.True result

    Assert.True(
        output
        |> List.exists (fun s -> s.Contains "Session saved to" && s.Contains "my-session.jsonl")
    )

[<Fact>]
let ``handleSaveCommand saves session with timestamp when no name`` () =
    let mutable output = []
    let store = mockSessionStore ()

    let config =
        { mockAgentConfig with
            sessionStore = store
            writeLine = fun s -> output <- output @ [ s ] }

    let msgs = [ LlmClient.userMessage "Hello" ]
    let result = AgentLoop.handleSaveCommand config "/save" msgs
    Assert.True result

    Assert.True(
        output
        |> List.exists (fun s -> s.Contains "Session saved to" && s.Contains "20250102-040506.jsonl")
    )

[<Fact>]
let ``handleSaveCommand prints error on save failure`` () =
    let mutable output = []

    let store =
        { mockSessionStore () with
            saveSession = fun _ _ -> Error "Disk full" }

    let config =
        { mockAgentConfig with
            sessionStore = store
            writeLine = fun s -> output <- output @ [ s ] }

    let result =
        AgentLoop.handleSaveCommand config "/save test" [ LlmClient.userMessage "Hello" ]

    Assert.True result
    Assert.True(output |> List.exists (fun s -> s.Contains "❌" && s.Contains "Disk full"))

[<Fact>]
let ``handleSaveCommand returns false for non-save command`` () =
    let result =
        AgentLoop.handleSaveCommand mockAgentConfig "/load test" [ LlmClient.userMessage "Hello" ]

    Assert.False result

[<Fact>]
let ``handleLoadCommand loads session by name`` () =
    let mutable output = []
    let store = mockSessionStore ()
    let msgs = [ LlmClient.userMessage "Previous" ]
    store.saveSession (store.sessionPath "existing") msgs |> ignore

    let config =
        { mockAgentConfig with
            sessionStore = store
            writeLine = fun s -> output <- output @ [ s ] }

    let result = AgentLoop.handleLoadCommand config "/load existing"

    match result with
    | Some loaded ->
        Assert.Equal(1, loaded.Length)
        Assert.Equal("Previous", loaded.[0].content)
        Assert.True(output |> List.exists (fun s -> s.Contains "Session loaded from"))
    | None -> Assert.Fail "Expected Some loaded messages"

[<Fact>]
let ``handleLoadCommand prints error on load failure`` () =
    let mutable output = []
    let store = mockSessionStore ()

    let config =
        { mockAgentConfig with
            sessionStore = store
            writeLine = fun s -> output <- output @ [ s ] }

    let result = AgentLoop.handleLoadCommand config "/load nonexistent"
    Assert.True result.IsNone

    Assert.True(
        output
        |> List.exists (fun s -> s.Contains "❌" && s.Contains "Session file not found")
    )

[<Fact>]
let ``handleLoadCommand lists sessions when no name`` () =
    let mutable output = []
    let store = mockSessionStore ()
    let msgs = [ LlmClient.userMessage "A" ]
    store.saveSession (store.sessionPath "alpha") msgs |> ignore

    let config =
        { mockAgentConfig with
            sessionStore = store
            writeLine = fun s -> output <- output @ [ s ] }

    let result = AgentLoop.handleLoadCommand config "/load"
    Assert.True result.IsNone
    Assert.True(output |> List.exists (fun s -> s.Contains "Available sessions"))

[<Fact>]
let ``handleLoadCommand prints no sessions message when store is empty`` () =
    let mutable output = []
    let store = mockSessionStore ()

    let config =
        { mockAgentConfig with
            sessionStore = store
            writeLine = fun s -> output <- output @ [ s ] }

    let result = AgentLoop.handleLoadCommand config "/load"
    Assert.True result.IsNone
    Assert.True(output |> List.exists (fun s -> s.Contains "No saved sessions"))

[<Fact>]
let ``handleLoadCommand returns None for non-load command`` () =
    let result = AgentLoop.handleLoadCommand mockAgentConfig "/save test"
    Assert.True result.IsNone

[<Fact>]
let ``handleInput returns Continue for empty input`` () =
    let result = AgentLoop.handleInput mockAgentConfig [] ""
    Assert.Equal(AgentLoop.Continue, result)

[<Fact>]
let ``handleInput returns Continue for whitespace-only input`` () =
    let result = AgentLoop.handleInput mockAgentConfig [] "   "
    Assert.Equal(AgentLoop.Continue, result)

[<Fact>]
let ``handleInput returns Exit for /exit`` () =
    let result = AgentLoop.handleInput mockAgentConfig [] "/exit"
    Assert.Equal(AgentLoop.Exit, result)

[<Fact>]
let ``handleInput returns Clear for /clear`` () =
    let result = AgentLoop.handleInput mockAgentConfig [] "/clear"
    Assert.Equal(AgentLoop.Clear, result)

[<Fact>]
let ``handleInput returns AutoConfirm with new config for /autoconfirm on`` () =
    let result = AgentLoop.handleInput mockAgentConfig [] "/autoconfirm on"

    match result with
    | AgentLoop.AutoConfirm cfg -> Assert.Equal(All, cfg.autoConfirm)
    | _ -> Assert.Fail "Expected AutoConfirm"

[<Fact>]
let ``handleInput returns Continue for /autoconfirm`` () =
    let result = AgentLoop.handleInput mockAgentConfig [] "/autoconfirm"
    Assert.Equal(AgentLoop.Continue, result)

[<Fact>]
let ``handleInput returns Save for /save`` () =
    let result = AgentLoop.handleInput mockAgentConfig [] "/save test"
    Assert.Equal(AgentLoop.Save, result)

[<Fact>]
let ``handleInput returns Load Some for /load with existing session`` () =
    let store = mockSessionStore ()

    store.saveSession (store.sessionPath "existing") [ LlmClient.userMessage "hi" ]
    |> ignore

    let config =
        { mockAgentConfig with
            sessionStore = store }

    let result = AgentLoop.handleInput config [] "/load existing"

    match result with
    | AgentLoop.Load(Some msgs) -> Assert.Single msgs |> ignore
    | _ -> Assert.Fail "Expected Load(Some msgs)"

[<Fact>]
let ``handleInput returns Load None for /load with no name`` () =
    let result = AgentLoop.handleInput mockAgentConfig [] "/load"

    match result with
    | AgentLoop.Load None -> ()
    | _ -> Assert.Fail "Expected Load None"

[<Fact>]
let ``handleInput returns Query for non-command input`` () =
    let result = AgentLoop.handleInput mockAgentConfig [] "Hello agent"
    Assert.Equal(AgentLoop.Query, result)

[<Fact>]
let ``repl exits immediately`` () =
    let mutable output = []

    let config =
        { mockAgentConfig with
            writeLine = fun s -> output <- output @ [ s ]
            readLine = fun () -> "/exit" }

    let mockClient =
        fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

    AgentLoop.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    |> ignore

    Assert.Contains("Goodbye!", output)

[<Fact>]
let ``repl auto-saves on exit`` () =
    let mutable output = []
    let mutable callCount = 0

    let config =
        { mockAgentConfig with
            writeLine = fun s -> output <- output @ [ s ]
            readLine =
                fun () ->
                    callCount <- callCount + 1
                    if callCount = 1 then "Hello" else "/exit" }

    let mockClient =
        fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

    AgentLoop.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    |> ignore

    Assert.True(output |> List.exists (fun s -> s.Contains "Session auto-saved to"))
    Assert.Contains("Goodbye!", output)

[<Fact>]
let ``repl handles Clear action`` () =
    let mutable output = []
    let mutable callCount = 0

    let config =
        { mockAgentConfig with
            writeLine = fun s -> output <- output @ [ s ]
            readLine =
                fun () ->
                    callCount <- callCount + 1
                    if callCount = 1 then "/clear" else "/exit" }

    let mockClient =
        fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

    AgentLoop.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    |> ignore

    Assert.True(output |> List.exists (fun s -> s.Contains "Context cleared"))

[<Fact>]
let ``repl handles AutoConfirm action and continues with new config`` () =
    let mutable output = []
    let mutable callCount = 0

    let config =
        { mockAgentConfig with
            writeLine = fun s -> output <- output @ [ s ]
            readLine =
                fun () ->
                    callCount <- callCount + 1
                    if callCount = 1 then "/autoconfirm on" else "/exit" }

    let mockClient =
        fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

    AgentLoop.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    |> ignore

    Assert.True(output |> List.exists (fun s -> s.Contains "Auto-confirm mode: ON"))

[<Fact>]
let ``repl handles Save action`` () =
    let mutable output = []
    let mutable callCount = 0

    let config =
        { mockAgentConfig with
            writeLine = fun s -> output <- output @ [ s ]
            readLine =
                fun () ->
                    callCount <- callCount + 1
                    if callCount = 1 then "/save test-session" else "/exit" }

    let mockClient =
        fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

    AgentLoop.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    |> ignore

    Assert.True(output |> List.exists (fun s -> s.Contains "Session saved to"))

[<Fact>]
let ``repl handles Load action with existing session`` () =
    let mutable output = []
    let mutable callCount = 0
    let store = mockSessionStore ()
    let testMessages = [ LlmClient.userMessage "Previous session" ]
    store.saveSession (store.sessionPath "test-load") testMessages |> ignore

    let config =
        { mockAgentConfig with
            sessionStore = store
            writeLine = fun s -> output <- output @ [ s ]
            readLine =
                fun () ->
                    callCount <- callCount + 1
                    if callCount = 1 then "/load test-load" else "/exit" }

    let mockClient =
        fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

    AgentLoop.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    |> ignore

    Assert.True(output |> List.exists (fun s -> s.Contains "Session loaded"))

[<Fact>]
let ``repl handles Load action with list sessions`` () =
    let mutable output = []
    let mutable callCount = 0

    let config =
        { mockAgentConfig with
            writeLine = fun s -> output <- output @ [ s ]
            readLine =
                fun () ->
                    callCount <- callCount + 1
                    if callCount = 1 then "/load" else "/exit" }

    let mockClient =
        fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

    AgentLoop.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    |> ignore

    Assert.True(
        output
        |> List.exists (fun s -> s.Contains "Available sessions" || s.Contains "No saved sessions")
    )

[<Fact>]
let ``repl skips blank input`` () =
    let mutable output = []
    let mutable callCount = 0

    let config =
        { mockAgentConfig with
            writeLine = fun s -> output <- output @ [ s ]
            readLine =
                fun () ->
                    callCount <- callCount + 1
                    if callCount <= 2 then "" else "/exit" }

    let mockClient =
        fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

    AgentLoop.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    |> ignore

    Assert.Contains("Goodbye!", output)
    Assert.True(callCount >= 3)

[<Fact>]
let ``repl processes user message and prints response`` () =
    let mutable output = []
    let mutable callCount = 0

    let config =
        { mockAgentConfig with
            writeLine = fun s -> output <- output @ [ s ]
            readLine =
                fun () ->
                    callCount <- callCount + 1
                    if callCount = 1 then "Hello agent" else "/exit" }

    let mockClient =
        fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

    AgentLoop.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    |> ignore

    Assert.True(output |> List.exists (fun s -> s.Contains "Hello!"))

[<Fact>]
let ``repl prints error message and continues when chatLoop returns Error`` () =
    let mutable output = []
    let mutable callCount = 0

    let config =
        { mockAgentConfig with
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

    AgentLoop.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    |> ignore

    Assert.True(output |> List.exists (fun s -> s.Contains "error occurred" || s.Contains "❌"))
    Assert.Contains("Goodbye!", output)


[<Fact>]
let ``loadAgentsMd returns Some content when file exists`` () =
    let mock = MockFileSystem()
    mock.AddFile "test_file.md" "Setup: dotnet build"
    let fs = mock.FileSystem
    let config = { mockAgentConfig with fileSystem = fs }
    let result = AgentLoop.loadAgentsMd config "test_file.md"
    Assert.True result.IsSome
    Assert.Equal("Setup: dotnet build", result.Value)

[<Fact>]
let ``loadAgentsMd returns None for non-existent file`` () =
    let result = AgentLoop.loadAgentsMd mockAgentConfig "nonexistent_agents.md"
    Assert.True result.IsNone

[<Fact>]
let ``loadAgentsMd returns None for empty or whitespace file`` () =
    let mock = MockFileSystem()
    mock.AddFile "empty_file.md" "   \n\t  "
    let fs = mock.FileSystem
    let config = { mockAgentConfig with fileSystem = fs }
    let result = AgentLoop.loadAgentsMd config "empty_file.md"
    Assert.True result.IsNone

[<Fact>]
let ``updateConfig appends AGENTS.md content to system prompt when file exists`` () =
    let mock = MockFileSystem()
    mock.AddFile "AGENTS.md" "Setup: dotnet test"
    let fs = mock.FileSystem
    let mutable output = []

    let config =
        { mockAgentConfig with
            writeLine = fun s -> output <- output @ [ s ]
            systemPrompt = "Base prompt"
            fileSystem = fs }

    let updated = AgentLoop.updateConfig config
    Assert.True(output |> List.exists (fun s -> s.Contains "Loaded project instructions"))
    Assert.Contains("Base prompt", updated.systemPrompt)
    Assert.Contains("Setup: dotnet test", updated.systemPrompt)

[<Fact>]
let ``updateConfig leaves prompt unchanged when AGENTS.md missing`` () =
    let mock = MockFileSystem()
    let fs = mock.FileSystem
    let mutable output = []

    let config =
        { mockAgentConfig with
            writeLine = fun s -> output <- output @ [ s ]
            systemPrompt = "Base prompt"
            fileSystem = fs }

    let updated = AgentLoop.updateConfig config
    Assert.Equal("Base prompt", updated.systemPrompt)

[<Fact>]
let ``initialMessages returns system message when no session`` () =
    let config =
        { mockAgentConfig with
            systemPrompt = "Test system" }

    let msgs = AgentLoop.initialMessages None config
    Assert.Equal(1, msgs.Length)
    Assert.Equal("system", msgs.[0].role)
    Assert.Equal("Test system", msgs.[0].content)

[<Fact>]
let ``initialMessages loads session when name provided`` () =
    let mutable output = []
    let store = mockSessionStore ()
    let prevMsgs = [ LlmClient.userMessage "Previous" ]
    store.saveSession (store.sessionPath "myload") prevMsgs |> ignore

    let config =
        { mockAgentConfig with
            sessionStore = store
            writeLine = fun s -> output <- output @ [ s ]
            systemPrompt = "Test system" }

    let msgs = AgentLoop.initialMessages (Some "myload") config
    Assert.Equal(2, msgs.Length)
    Assert.Equal("system", msgs.[0].role)
    Assert.Equal("Previous", msgs.[1].content)
    Assert.True(output |> List.exists (fun s -> s.Contains "Session loaded from"))

[<Fact>]
let ``initialMessages loads nonexistent session`` () =
    let mutable output = []

    let config =
        { mockAgentConfig with
            writeLine = fun s -> output <- output @ [ s ]
            systemPrompt = "Test system" }

    let msgs = AgentLoop.initialMessages (Some "nonexistent_session_xyz") config
    Assert.Equal(1, msgs.Length)
    Assert.Equal("system", msgs.[0].role)
    Assert.Equal("Test system", msgs.[0].content)
    Assert.True(output |> List.exists (fun s -> s.Contains "❌"))

[<Fact>]
let ``start prints startup banner and begins repl`` () =
    let mutable output = []

    let config =
        { mockAgentConfig with
            writeLine = fun s -> output <- output @ [ s ]
            readLine = fun () -> "/exit" }

    let mockClient =
        fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

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
        { mockAgentConfig with
            sessionStore = store
            writeLine = fun s -> output <- output @ [ s ]
            readLine = fun () -> "/exit" }

    let mockClient =
        fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

    AgentLoop.start (Some "testload") config mockClient

    Assert.True(
        output
        |> List.exists (fun s -> s.Contains "Session loaded from" && s.Contains "testload.jsonl")
    )
