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
        if i = 6 then
            { role = "tool"
              content = "tool result"
              name = "some_tool"
              tool_call_id = "123"
              tool_calls = null }
        else
            LlmClient.userMessage (sprintf "Msg %d" i)

    let messages = sysMsg :: [ for i in 1..25 -> makeMsg i ]
    let result = AgentLoop.truncateMessages 20 messages
    Assert.Equal(20, result.Length)
    Assert.Equal("system", result.[0].role)
    Assert.Equal("user", result.[1].role)
    Assert.Equal("Msg 7", result.[1].content)

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
let ``handleAutoConfirmCommand returns None for /autoconfirm without subcommand`` () =
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
        |> List.exists (fun s -> s.Contains "Session saved to" && s.Contains "my-session")
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
        |> List.exists (fun s -> s.Contains "Session saved to" && s.Contains "20250101-000000")
    )

[<Fact>]
let ``handleSaveCommand returns false for non-save command`` () =
    let result =
        AgentLoop.handleSaveCommand mockAgentConfig "/load test" [ LlmClient.userMessage "Hello" ]

    Assert.False result

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
let ``handleSaveCommand ignores non-save prefixed commands`` () =
    let result = AgentLoop.handleSaveCommand mockAgentConfig "hello world" []
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
let ``handleLoadCommand returns None for non-load command`` () =
    let result = AgentLoop.handleLoadCommand mockAgentConfig "/save test"
    Assert.True result.IsNone

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
    Assert.True(output |> List.exists (fun s -> s.Contains "❌"))

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
let ``repl exits immediately on '/exit' input`` () =
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
let ``repl clears context on '/clear' input then exits`` () =
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

    Assert.Contains("🧹 Context cleared.", output)
    Assert.Contains("Goodbye!", output)

[<Fact>]
let ``repl skips blank input and processes next non-blank input`` () =
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
let ``repl processes user message and prints response then exits`` () =
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
let ``repl prints error message and continues when runLoop returns Error`` () =
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
let ``repl prints error message and continues when runLoop throws`` () =
    let mutable output = []
    let mutable callCount = 0

    let config =
        { mockAgentConfig with
            writeLine = fun s -> output <- output @ [ s ]
            readLine =
                fun () ->
                    callCount <- callCount + 1
                    if callCount = 1 then "Trigger exception" else "/exit" }

    let mockClient =
        fun (_json: string) ->
            System.Threading.Tasks.Task.FromException<System.Net.Http.HttpResponseMessage>(System.Exception "boom")

    AgentLoop.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    |> ignore

    Assert.True(output |> List.exists (fun s -> s.Contains "❌" || s.Contains "error"))
    Assert.Contains("Goodbye!", output)

[<Fact>]
let ``repl /autoconfirm on enables full auto-confirm`` () =
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
let ``repl /autoconfirm off disables auto-confirm`` () =
    let mutable output = []
    let mutable callCount = 0

    let config =
        { mockAgentConfig with
            autoConfirm = All
            writeLine = fun s -> output <- output @ [ s ]
            readLine =
                fun () ->
                    callCount <- callCount + 1
                    if callCount = 1 then "/autoconfirm off" else "/exit" }

    let mockClient =
        fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

    AgentLoop.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    |> ignore

    Assert.True(output |> List.exists (fun s -> s.Contains "Auto-confirm mode: OFF"))

[<Fact>]
let ``repl /autoconfirm reads enables reads-only auto-confirm`` () =
    let mutable output = []
    let mutable callCount = 0

    let config =
        { mockAgentConfig with
            writeLine = fun s -> output <- output @ [ s ]
            readLine =
                fun () ->
                    callCount <- callCount + 1
                    if callCount = 1 then "/autoconfirm reads" else "/exit" }

    let mockClient =
        fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

    AgentLoop.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    |> ignore

    Assert.True(output |> List.exists (fun s -> s.Contains "READS ONLY"))

[<Fact>]
let ``repl /autoconfirm invalid shows usage`` () =
    let mutable output = []
    let mutable callCount = 0

    let config =
        { mockAgentConfig with
            writeLine = fun s -> output <- output @ [ s ]
            readLine =
                fun () ->
                    callCount <- callCount + 1
                    if callCount = 1 then "/autoconfirm invalid" else "/exit" }

    let mockClient =
        fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

    AgentLoop.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    |> ignore

    Assert.True(output |> List.exists (fun s -> s.Contains "Usage:"))

[<Fact>]
let ``repl /save command saves session and prints path`` () =
    let mutable output = []
    let mutable callCount = 0

    let config =
        { mockAgentConfig with
            writeLine = fun s -> output <- output @ [ s ]
            readLine =
                fun () ->
                    callCount <- callCount + 1

                    if callCount = 1 then "/save mytest" else "/exit" }

    let mockClient =
        fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

    AgentLoop.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    |> ignore

    Assert.True(
        output
        |> List.exists (fun s -> s.Contains "Session saved to" && s.Contains "mytest")
    )

[<Fact>]
let ``repl /save without name uses timestamp`` () =
    let mutable output = []
    let mutable callCount = 0

    let config =
        { mockAgentConfig with
            writeLine = fun s -> output <- output @ [ s ]
            readLine =
                fun () ->
                    callCount <- callCount + 1
                    if callCount = 1 then "/save" else "/exit" }

    let mockClient =
        fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

    AgentLoop.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    |> ignore

    Assert.True(
        output
        |> List.exists (fun s -> s.Contains "Session saved to" && s.Contains ".agents/sessions")
    )

[<Fact>]
let ``repl /load with no args lists available sessions`` () =
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
        |> List.exists (fun s -> s.Contains "No saved sessions" || s.Contains "Available sessions")
    )

[<Fact>]
let ``repl /load nonexistent session shows error`` () =
    let mutable output = []
    let mutable callCount = 0

    let config =
        { mockAgentConfig with
            writeLine = fun s -> output <- output @ [ s ]
            readLine =
                fun () ->
                    callCount <- callCount + 1
                    if callCount = 1 then "/load nonexistent_xyz" else "/exit" }

    let mockClient =
        fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

    AgentLoop.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    |> ignore

    Assert.True(output |> List.exists (fun s -> s.Contains "not found" || s.Contains "❌"))

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

    Assert.True(
        output
        |> List.exists (fun s -> s.Contains "Session auto-saved to" || s.Contains "Goodbye!")
    )

[<Fact>]
let ``repl handles /load with valid session name`` () =
    let mutable output = []
    let mutable callCount = 0
    let store = mockSessionStore ()
    let prevMsgs = [ LlmClient.userMessage "Previous content" ]
    store.saveSession (store.sessionPath "testsess") prevMsgs |> ignore

    let config =
        { mockAgentConfig with
            sessionStore = store
            writeLine = fun s -> output <- output @ [ s ]
            readLine =
                fun () ->
                    callCount <- callCount + 1
                    if callCount = 1 then "/load testsess" else "/exit" }

    let mockClient =
        fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

    AgentLoop.repl config mockClient 0 0 [ LlmClient.systemMessage "System" ]
    |> ignore

    Assert.True(output |> List.exists (fun s -> s.Contains "Context restored"))

[<Fact>]
let ``loadAgentsMd returns content for valid file`` () =
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
let ``loadAgentsMd returns None when ReadAllText throws (e.g. path is directory)`` () =
    let result = AgentLoop.loadAgentsMd mockAgentConfig (System.IO.Path.GetTempPath())
    Assert.True result.IsNone

[<Fact>]
let ``updateConfig enriches prompt when AGENTS.md exists`` () =
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
    Assert.True(output |> List.exists (fun s -> s.Contains "AGENTS.md"))
    Assert.Contains("Base prompt", updated.systemPrompt)
    Assert.Contains("Setup: dotnet test", updated.systemPrompt)

[<Fact>]
let ``updateConfig leaves prompt unchanged when AGENTS.md missing`` () =
    let originalFile = "AGENTS.md"
    let hasPreexisting = System.IO.File.Exists originalFile

    let backupContent =
        if hasPreexisting then
            Some(System.IO.File.ReadAllText originalFile)
        else
            None

    try
        if hasPreexisting then
            System.IO.File.Delete originalFile

        let config =
            { mockAgentConfig with
                systemPrompt = "Base prompt" }

        let updated = AgentLoop.updateConfig config
        Assert.Equal("Base prompt", updated.systemPrompt)
    finally
        if hasPreexisting then
            match backupContent with
            | Some content -> System.IO.File.WriteAllText(originalFile, content)
            | None -> ()

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
let ``start loads AGENTS.md content when file exists`` () =
    let mock = MockFileSystem()
    mock.AddFile "AGENTS.md" "Setup: test build"
    let fs = mock.FileSystem

    let mutable output = []

    let config =
        { mockAgentConfig with
            writeLine = fun s -> output <- output @ [ s ]
            readLine = fun () -> "/exit"
            fileSystem = fs }

    let mockClient =
        fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

    AgentLoop.start None config mockClient

    Assert.True(
        output
        |> List.exists (fun s -> s.Contains "Loaded project instructions from AGENTS.md")
    )

[<Fact>]
let ``start with sessionToLoad loads existing session`` () =
    let mutable output = []

    let messages =
        [ LlmClient.systemMessage "Custom system"
          LlmClient.userMessage "Previous question"
          LlmClient.assistantMessage "Previous answer" ]

    let store = mockSessionStore ()
    // Pre-populate the mock store with the session
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

[<Fact>]
let ``start with nonexistent sessionToLoad shows error and uses default prompt`` () =
    let mutable output = []

    let config =
        { mockAgentConfig with
            writeLine = fun s -> output <- output @ [ s ]
            readLine = fun () -> "/exit" }

    let mockClient =
        fun _json -> System.Threading.Tasks.Task.FromResult(makeSuccessResponse validChatResponseJson)

    AgentLoop.start (Some "nonexistent_session_xyz") config mockClient
    Assert.True(output |> List.exists (fun s -> s.Contains "not found" || s.Contains "❌"))
    Assert.True(output |> List.exists (fun s -> s.Contains "Coding Agent started"))
