module CodingAgent.SessionTests

open Xunit
open CodingAgent
open TestHelpers

[<Fact>]
let ``serializeMessage includes null values for name, tool_call_id, and tool_calls in JSON`` () =
    let msg = LlmClient.userMessage "test"
    let json = Session.serializeMessage msg
    Assert.Contains("\"role\":\"user\"", json)
    Assert.Contains("\"content\":\"test\"", json)
    Assert.Contains("\"name\":null", json)
    Assert.Contains("\"tool_call_id\":null", json)
    Assert.Contains("\"tool_calls\":null", json)

[<Theory>]
[<InlineData("""{"role":"user","content":"hello","name":null,"tool_call_id":null,"tool_calls":null}""", true, "user", "hello", "")>]
[<InlineData("not valid json", false, "", "", "Failed to deserialize message")>]
let ``deserializeMessage returns Ok for valid JSON and Error for invalid JSON``
    (json: string, expectOk: bool, expectedRole: string, expectedContent: string, expectedErr: string)
    =
    let result = Session.deserializeMessage json

    match result with
    | Ok msg ->
        Assert.True expectOk
        Assert.Equal(expectedRole, msg.role)
        Assert.Equal(expectedContent, msg.content)
    | Error err ->
        Assert.False expectOk
        Assert.Contains(expectedErr, err)

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``save creates session directory and writes JSON lines or empty file`` (hasMessages: bool) =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "session-save-test")

    mock.AddDir tempDir
    let sessionFile = System.IO.Path.Combine(tempDir, "test.jsonl")

    let messages =
        if hasMessages then
            [ LlmClient.systemMessage "Test system"; LlmClient.userMessage "Hello" ]
        else
            []

    let result = Session.save mock.FileSystem sessionFile messages

    match result with
    | Ok() ->
        Assert.True(mock.FileSystem.existsFile sessionFile)
        let lines = mock.FileSystem.readLines sessionFile |> Seq.toArray
        if hasMessages then
            Assert.Equal(2, lines.Length)
            Assert.Contains("\"role\":\"system\"", lines.[0])
            Assert.Contains("\"role\":\"user\"", lines.[1])
        else
            Assert.True(lines.Length <= 1 && lines |> Array.forall System.String.IsNullOrWhiteSpace)
    | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)

[<Fact>]
let ``load round-trips messages written by save with correct roles and content`` () =
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
let ``save-load round-trip preserves assistant tool_call messages and tool results`` () =
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
let ``load skips blank and whitespace-only lines when reading session file`` () =
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
let ``load returns an empty message list when session file is empty`` () =
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
let ``load returns Error when session file does not exist`` () =
    let mock = MockFileSystem()

    let nonExistentDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "session-missing-test")

    let sessionFile = System.IO.Path.Combine(nonExistentDir, "session.jsonl")
    let result = Session.load mock.FileSystem sessionFile

    match result with
    | Error err -> Assert.Contains("not found", err)
    | Ok _ -> Assert.Fail "Expected Error for nonexistent file"

[<Fact>]
let ``load returns Error on corrupt JSON line within an otherwise valid session file`` () =
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

[<Theory>]
[<InlineData("exists")>]
[<InlineData("missing")>]
let ``list returns sorted entries when directory exists and empty result when missing`` (scenario: string) =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "session-list-test")

    if scenario = "exists" then
        mock.AddDir tempDir
        let fileA = System.IO.Path.Combine(tempDir, "alpha.jsonl")
        let fileB = System.IO.Path.Combine(tempDir, "beta.jsonl")
        mock.AddFile fileA "{\"role\":\"user\",\"content\":\"a\",\"name\":null,\"tool_call_id\":null,\"tool_calls\":null}"
        mock.AddFile fileB "{\"role\":\"user\",\"content\":\"b\",\"name\":null,\"tool_call_id\":null,\"tool_calls\":null}"

    let dirArg = if scenario = "missing" then System.IO.Path.Combine(tempDir, "session-nodir-test") else tempDir
    let listed = Session.list mock.FileSystem dirArg () |> Seq.toArray

    match scenario with
    | "exists" ->
        Assert.Equal(2, listed.Length)
        Assert.Contains("alpha", listed.[0])
        Assert.Contains("beta", listed.[1])
    | "missing" ->
        Assert.Empty listed
    | _ -> failwith "unknown scenario"

[<Fact>]
let ``pathForName combines session directory with session name and appends .jsonl extension`` () =
    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "session-store-test")

    let path = Session.pathForName tempDir "my-session"
    Assert.Equal(System.IO.Path.Combine(tempDir, "my-session.jsonl"), path)

[<Fact>]
let ``timestampedName generates a non-empty, reasonably long unique string`` () =
    let ts = Session.timestampedName ()
    Assert.False(System.String.IsNullOrWhiteSpace ts)
    Assert.True(ts.Length >= 12)
