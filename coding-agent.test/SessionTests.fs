module CodingAgent.SessionTests

open Xunit
open CodingAgent
open TestHelpers

[<Fact>]
let ``serializeMessage produces valid JSON for message with null name and null tool_calls`` () =
    let msg = LlmClient.userMessage "test"
    let json = Session.serializeMessage msg
    Assert.Contains("\"role\":\"user\"", json)
    Assert.Contains("\"content\":\"test\"", json)
    Assert.Contains("\"name\":null", json)
    Assert.Contains("\"tool_call_id\":null", json)
    Assert.Contains("\"tool_calls\":null", json)

[<Fact>]
let ``deserializeMessage returns Error for invalid JSON`` () =
    let result = Session.deserializeMessage "not valid json"

    match result with
    | Error err -> Assert.Contains("Failed to deserialize message", err)
    | Ok _ -> Assert.Fail "Expected Error for invalid JSON"

[<Fact>]
let ``deserializeMessage returns Ok for valid JSON`` () =
    let json =
        "{\"role\":\"user\",\"content\":\"hello\",\"name\":null,\"tool_call_id\":null,\"tool_calls\":null}"

    let result = Session.deserializeMessage json

    match result with
    | Ok msg ->
        Assert.Equal("user", msg.role)
        Assert.Equal("hello", msg.content)
    | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)

[<Fact>]
let ``newSessionStore creates a store with working functions`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "session-store-test")

    mock.AddDir tempDir
    let store = Session.newSessionStore mock.FileSystem tempDir

    let path = store.sessionPath "my-session"
    Assert.Equal(System.IO.Path.Combine(tempDir, "my-session.jsonl"), path)

    let ts = store.timestampedSessionName ()
    Assert.False(System.String.IsNullOrWhiteSpace ts)
    Assert.True(ts.Length >= 12)

    let msgs = [ LlmClient.userMessage "Hello store" ]
    let saveResult = store.saveSession path msgs

    match saveResult with
    | Ok() -> Assert.True(mock.FileSystem.existsFile path)
    | Error err -> Assert.Fail(sprintf "Expected Ok from saveSession, got Error: %s" err)

    match store.loadSession path with
    | Ok loaded ->
        Assert.Equal(1, loaded.Length)
        Assert.Equal("Hello store", loaded.[0].content)
    | Error err -> Assert.Fail(sprintf "Expected Ok from loadSession, got Error: %s" err)

    let listed = store.listSessions () |> Seq.toArray
    Assert.Equal(1, listed.Length)
    Assert.Contains("my-session", listed.[0])

[<Fact>]
let ``saveSession creates directory and writes file`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "session-save-test")

    mock.AddDir tempDir
    let sessionFile = System.IO.Path.Combine(tempDir, "test.jsonl")
    let store = Session.newSessionStore mock.FileSystem tempDir

    let result =
        [ LlmClient.systemMessage "Test system"; LlmClient.userMessage "Hello" ]
        |> store.saveSession sessionFile

    match result with
    | Ok() ->
        Assert.True(mock.FileSystem.existsFile sessionFile)
        let lines = mock.FileSystem.readLines sessionFile |> Seq.toArray
        Assert.Equal(2, lines.Length)
        Assert.Contains("\"role\":\"system\"", lines.[0])
        Assert.Contains("\"role\":\"user\"", lines.[1])
    | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)

[<Fact>]
let ``saveSession with empty message list creates empty file`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "session-empty-test")

    mock.AddDir tempDir
    let sessionFile = System.IO.Path.Combine(tempDir, "empty.jsonl")
    let store = Session.newSessionStore mock.FileSystem tempDir
    let result = store.saveSession sessionFile []

    match result with
    | Ok() ->
        Assert.True(mock.FileSystem.existsFile sessionFile)
        let lines = mock.FileSystem.readLines sessionFile |> Seq.toArray
        Assert.True(lines.Length <= 1 && lines |> Array.forall System.String.IsNullOrWhiteSpace)
    | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)

[<Fact>]
let ``saveSession handles special JSON characters in content`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "session-special-test")

    mock.AddDir tempDir
    let sessionFile = System.IO.Path.Combine(tempDir, "special.jsonl")
    let store = Session.newSessionStore mock.FileSystem tempDir

    let saveResult =
        [ LlmClient.userMessage "Line1\nLine2\tTab \"quoted\" \\backslash" ]
        |> store.saveSession sessionFile

    match saveResult with
    | Ok() ->
        match store.loadSession sessionFile with
        | Ok loaded ->
            Assert.Equal(1, loaded.Length)
            Assert.Equal("Line1\nLine2\tTab \"quoted\" \\backslash", loaded.[0].content)
        | Error err -> Assert.Fail(sprintf "Expected Ok from load, got Error: %s" err)
    | Error err -> Assert.Fail(sprintf "Expected Ok from save, got Error: %s" err)

[<Fact>]
let ``saveSession returns Error when path is invalid`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "session-invalid-test")

    let store = Session.newSessionStore mock.FileSystem tempDir
    let result = store.saveSession "/invalid/\x00path/file.jsonl" []

    match result with
    | Error err -> Assert.Contains("Failed to save session", err)
    | Ok() -> Assert.Fail "Expected Error for invalid path"

[<Fact>]
let ``loadSession reads back messages saved by saveSession`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "session-roundtrip-test")

    mock.AddDir tempDir
    let sessionFile = System.IO.Path.Combine(tempDir, "roundtrip.jsonl")
    let store = Session.newSessionStore mock.FileSystem tempDir

    let saveResult =
        [ LlmClient.systemMessage "System prompt"
          LlmClient.userMessage "What is F#?"
          LlmClient.assistantMessage "F# is a functional programming language." ]
        |> store.saveSession sessionFile

    match saveResult with
    | Ok() ->
        match store.loadSession sessionFile with
        | Ok loaded ->
            Assert.Equal(3, loaded.Length)
            Assert.Equal("system", loaded.[0].role)
            Assert.Equal("System prompt", loaded.[0].content)
            Assert.Equal("user", loaded.[1].role)
            Assert.Equal("What is F#?", loaded.[1].content)
            Assert.Equal("assistant", loaded.[2].role)
            Assert.Equal("F# is a functional programming language.", loaded.[2].content)
        | Error err -> Assert.Fail(sprintf "Expected Ok from load, got Error: %s" err)
    | Error err -> Assert.Fail(sprintf "Expected Ok from save, got Error: %s" err)

[<Fact>]
let ``saveSession-loadSession roundtrip preserves tool_call messages`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "session-toolcall-test")

    mock.AddDir tempDir
    let sessionFile = System.IO.Path.Combine(tempDir, "toolcall.jsonl")
    let store = Session.newSessionStore mock.FileSystem tempDir

    let toolCall: LlmClient.ToolCall =
        { id = "call_abc"
          ``type`` = "function"
          ``function`` =
            { name = "read_file"
              arguments = "{\"file_path\":\"test.txt\"}" } }

    let saveResult =
        [ LlmClient.systemMessage "System"
          LlmClient.userMessage "Read a file"
          { role = "assistant"
            content = ""
            name = null
            tool_call_id = null
            tool_calls = [| toolCall |] }
          LlmClient.toolResultMessage "call_abc" "read_file" "file contents here" ]
        |> store.saveSession sessionFile

    match saveResult with
    | Ok() ->
        match store.loadSession sessionFile with
        | Ok loaded ->
            Assert.Equal(4, loaded.Length)
            let toolMsg = loaded.[2]
            Assert.Equal("assistant", toolMsg.role)
            Assert.NotNull toolMsg.tool_calls
            Assert.Equal(1, toolMsg.tool_calls.Length)
            Assert.Equal("call_abc", toolMsg.tool_calls.[0].id)
            Assert.Equal("read_file", toolMsg.tool_calls.[0].``function``.name)
            let resultMsg = loaded.[3]
            Assert.Equal("tool", resultMsg.role)
            Assert.Equal("call_abc", resultMsg.tool_call_id)
            Assert.Equal("read_file", resultMsg.name)
            Assert.Equal("file contents here", resultMsg.content)
        | Error err -> Assert.Fail(sprintf "Expected Ok from load, got Error: %s" err)
    | Error err -> Assert.Fail(sprintf "Expected Ok from save, got Error: %s" err)

[<Fact>]
let ``loadSession skips blank lines in file`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "session-blanks-test")

    mock.AddDir tempDir
    let sessionFile = System.IO.Path.Combine(tempDir, "blanks.jsonl")

    let content =
        System.String.Join(
            "\n",
            [| ""
               "{\"role\":\"user\",\"content\":\"first\",\"name\":null,\"tool_call_id\":null,\"tool_calls\":null}"
               "   "
               "{\"role\":\"user\",\"content\":\"second\",\"name\":null,\"tool_call_id\":null,\"tool_calls\":null}"
               "" |]
        )

    mock.AddFile sessionFile content
    let store = Session.newSessionStore mock.FileSystem tempDir

    match store.loadSession sessionFile with
    | Ok loaded ->
        Assert.Equal(2, loaded.Length)
        Assert.Equal("first", loaded.[0].content)
        Assert.Equal("second", loaded.[1].content)
    | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)

[<Fact>]
let ``loadSession returns Error for nonexistent file`` () =
    let mock = MockFileSystem()

    let nonExistentDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "session-missing-test")

    let sessionFile = System.IO.Path.Combine(nonExistentDir, "session.jsonl")
    let store = Session.newSessionStore mock.FileSystem nonExistentDir
    let result = store.loadSession sessionFile

    match result with
    | Error err -> Assert.Contains("not found", err)
    | Ok _ -> Assert.Fail "Expected Error for nonexistent file"

[<Fact>]
let ``loadSession returns Error when file is empty`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "session-empty-load-test")

    mock.AddDir tempDir
    let sessionFile = System.IO.Path.Combine(tempDir, "empty.jsonl")
    mock.AddFile sessionFile ""
    let store = Session.newSessionStore mock.FileSystem tempDir

    match store.loadSession sessionFile with
    | Ok loaded -> Assert.Empty loaded
    | Error err -> Assert.Fail(sprintf "Expected Ok with empty list, got Error: %s" err)

[<Fact>]
let ``loadSession returns Error when one line is invalid among valid lines`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "session-partial-corrupt-test")

    mock.AddDir tempDir
    let sessionFile = System.IO.Path.Combine(tempDir, "partial.jsonl")

    let validLine =
        "{\"role\":\"user\",\"content\":\"hello\",\"name\":null,\"tool_call_id\":null,\"tool_calls\":null}"

    mock.AddFile sessionFile (validLine + "\nbad json\n" + validLine)
    let store = Session.newSessionStore mock.FileSystem tempDir

    match store.loadSession sessionFile with
    | Error err -> Assert.Contains("Corrupt session data at line", err)
    | Ok _ -> Assert.Fail "Expected Error when session contains corrupt line"

[<Fact>]
let ``listSessions returns sorted entries with timestamps`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "session-list-test")

    mock.AddDir tempDir
    let fileA = System.IO.Path.Combine(tempDir, "alpha.jsonl")
    let fileB = System.IO.Path.Combine(tempDir, "beta.jsonl")
    mock.AddFile fileA "{\"role\":\"user\",\"content\":\"a\",\"name\":null,\"tool_call_id\":null,\"tool_calls\":null}"
    mock.AddFile fileB "{\"role\":\"user\",\"content\":\"b\",\"name\":null,\"tool_call_id\":null,\"tool_calls\":null}"
    let store = Session.newSessionStore mock.FileSystem tempDir
    let listed = store.listSessions () |> Seq.toArray
    Assert.Equal(2, listed.Length)
    Assert.Contains("alpha", listed.[0])
    Assert.Contains("beta", listed.[1])

[<Fact>]
let ``listSessions returns empty array when directory does not exist`` () =
    let mock = MockFileSystem()

    let nonExistentDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "session-nodir-test")

    let store = Session.newSessionStore mock.FileSystem nonExistentDir
    let listed = store.listSessions ()
    Assert.Empty listed
