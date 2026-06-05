module CodingAgent.SessionTests

open Xunit
open CodingAgent
open TestHelpers

[<Fact>]
let ``serializeMessage includes null fields in JSON output`` () =
    let msg = LlmClient.userMessage "test"
    let json = Session.serializeMessage msg
    Assert.Contains("\"role\":\"user\"", json)
    Assert.Contains("\"content\":\"test\"", json)
    Assert.Contains("\"name\":null", json)
    Assert.Contains("\"tool_call_id\":null", json)
    Assert.Contains("\"tool_calls\":null", json)

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
let ``deserializeMessage returns Error for invalid JSON`` () =
    let result = Session.deserializeMessage "not valid json"

    match result with
    | Error err -> Assert.Contains("Failed to deserialize message", err)
    | Ok _ -> Assert.Fail "Expected Error for invalid JSON"

[<Fact>]
let ``save creates directory and writes file`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "session-save-test")

    mock.AddDir tempDir
    let sessionFile = System.IO.Path.Combine(tempDir, "test.jsonl")

    let result =
        [ LlmClient.systemMessage "Test system"; LlmClient.userMessage "Hello" ]
        |> Session.save mock.FileSystem sessionFile

    match result with
    | Ok() ->
        Assert.True(mock.FileSystem.existsFile sessionFile)
        let lines = mock.FileSystem.readLines sessionFile |> Seq.toArray
        Assert.Equal(2, lines.Length)
        Assert.Contains("\"role\":\"system\"", lines.[0])
        Assert.Contains("\"role\":\"user\"", lines.[1])
    | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)

[<Fact>]
let ``save with empty message list creates empty file`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "session-empty-test")

    mock.AddDir tempDir
    let sessionFile = System.IO.Path.Combine(tempDir, "empty.jsonl")
    let result = Session.save mock.FileSystem sessionFile []

    match result with
    | Ok() ->
        Assert.True(mock.FileSystem.existsFile sessionFile)
        let lines = mock.FileSystem.readLines sessionFile |> Seq.toArray
        Assert.True(lines.Length <= 1 && lines |> Array.forall System.String.IsNullOrWhiteSpace)
    | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)

[<Fact>]
let ``load reads back messages saved by save`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "session-roundtrip-test")

    mock.AddDir tempDir
    let sessionFile = System.IO.Path.Combine(tempDir, "roundtrip.jsonl")

    let saveResult =
        [ LlmClient.systemMessage "System prompt"
          LlmClient.userMessage "What is F#?"
          LlmClient.assistantMessage "F# is a functional programming language." ]
        |> Session.save mock.FileSystem sessionFile

    match saveResult with
    | Ok() ->
        match Session.load mock.FileSystem sessionFile with
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
let ``save round-trips newlines, tabs, and quotes in message content`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "session-special-test")

    mock.AddDir tempDir
    let sessionFile = System.IO.Path.Combine(tempDir, "special.jsonl")

    let saveResult =
        [ LlmClient.userMessage "Line1\nLine2\tTab \"quoted\" \\backslash" ]
        |> Session.save mock.FileSystem sessionFile

    match saveResult with
    | Ok() ->
        match Session.load mock.FileSystem sessionFile with
        | Ok loaded ->
            Assert.Equal(1, loaded.Length)
            Assert.Equal("Line1\nLine2\tTab \"quoted\" \\backslash", loaded.[0].content)
        | Error err -> Assert.Fail(sprintf "Expected Ok from load, got Error: %s" err)
    | Error err -> Assert.Fail(sprintf "Expected Ok from save, got Error: %s" err)

[<Fact>]
let ``save-load roundtrip preserves tool_call messages`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "session-toolcall-test")

    mock.AddDir tempDir
    let sessionFile = System.IO.Path.Combine(tempDir, "toolcall.jsonl")

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
        |> Session.save mock.FileSystem sessionFile

    match saveResult with
    | Ok() ->
        match Session.load mock.FileSystem sessionFile with
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
let ``load skips blank lines in file`` () =
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

    match Session.load mock.FileSystem sessionFile with
    | Ok loaded ->
        Assert.Equal(2, loaded.Length)
        Assert.Equal("first", loaded.[0].content)
        Assert.Equal("second", loaded.[1].content)
    | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)

[<Fact>]
let ``load returns empty list when file is empty`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "session-empty-load-test")

    mock.AddDir tempDir
    let sessionFile = System.IO.Path.Combine(tempDir, "empty.jsonl")
    mock.AddFile sessionFile ""

    match Session.load mock.FileSystem sessionFile with
    | Ok loaded -> Assert.Empty loaded
    | Error err -> Assert.Fail(sprintf "Expected Ok with empty list, got Error: %s" err)

[<Fact>]
let ``load returns Error for nonexistent file`` () =
    let mock = MockFileSystem()

    let nonExistentDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "session-missing-test")

    let sessionFile = System.IO.Path.Combine(nonExistentDir, "session.jsonl")
    let result = Session.load mock.FileSystem sessionFile

    match result with
    | Error err -> Assert.Contains("not found", err)
    | Ok _ -> Assert.Fail "Expected Error for nonexistent file"

[<Fact>]
let ``load returns Error on corrupt line within otherwise valid data`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "session-partial-corrupt-test")

    mock.AddDir tempDir
    let sessionFile = System.IO.Path.Combine(tempDir, "partial.jsonl")

    let validLine =
        "{\"role\":\"user\",\"content\":\"hello\",\"name\":null,\"tool_call_id\":null,\"tool_calls\":null}"

    mock.AddFile sessionFile (validLine + "\nbad json\n" + validLine)

    match Session.load mock.FileSystem sessionFile with
    | Error err -> Assert.Contains("Corrupt session data at line", err)
    | Ok _ -> Assert.Fail "Expected Error when session contains corrupt line"

[<Fact>]
let ``list returns sorted entries with timestamps`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "session-list-test")

    mock.AddDir tempDir
    let fileA = System.IO.Path.Combine(tempDir, "alpha.jsonl")
    let fileB = System.IO.Path.Combine(tempDir, "beta.jsonl")
    mock.AddFile fileA "{\"role\":\"user\",\"content\":\"a\",\"name\":null,\"tool_call_id\":null,\"tool_calls\":null}"
    mock.AddFile fileB "{\"role\":\"user\",\"content\":\"b\",\"name\":null,\"tool_call_id\":null,\"tool_calls\":null}"
    let listed = Session.list mock.FileSystem tempDir () |> Seq.toArray
    Assert.Equal(2, listed.Length)
    Assert.Contains("alpha", listed.[0])
    Assert.Contains("beta", listed.[1])

[<Fact>]
let ``list returns empty array when directory does not exist`` () =
    let mock = MockFileSystem()

    let nonExistentDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "session-nodir-test")

    let listed = Session.list mock.FileSystem nonExistentDir () |> Seq.toArray
    Assert.Empty listed

[<Fact>]
let ``pathForName combines directory with session name`` () =
    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "session-store-test")

    let path = Session.pathForName tempDir "my-session"
    Assert.Equal(System.IO.Path.Combine(tempDir, "my-session.jsonl"), path)

[<Fact>]
let ``timestampedName generates non-empty unique string`` () =
    let ts = Session.timestampedName ()
    Assert.False(System.String.IsNullOrWhiteSpace ts)
    Assert.True(ts.Length >= 12)
