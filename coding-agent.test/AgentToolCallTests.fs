module CodingAgent.AgentToolCallTests

open Xunit
open CodingAgent
open TestHelpers

[<Fact>]
let ``AsyncResult.bind propagates Error without calling continuation`` () =
    async {
        let input =
            AgentToolCall.AsyncResult.ofResult (Error "initial error": Result<string, string>)

        let mutable wasCalled = false

        let! result =
            input
            |> AgentToolCall.AsyncResult.bind (fun _ ->
                wasCalled <- true
                async { return Ok "should not reach" })

        Assert.Equal("initial error", assertError result)
        Assert.False(wasCalled, "Continuation should not be called on Error")
    }
    |> Async.RunSynchronously

[<Fact>]
let ``AsyncResult.map propagates Error without calling mapping function`` () =
    async {
        let input =
            AgentToolCall.AsyncResult.ofResult (Error "map error": Result<string, string>)

        let mutable wasCalled = false

        let! result =
            input
            |> AgentToolCall.AsyncResult.map (fun _ ->
                wasCalled <- true
                "should not reach")

        Assert.Equal("map error", assertError result)
        Assert.False(wasCalled, "Mapping function should not be called on Error")
    }
    |> Async.RunSynchronously

[<Theory>]
[<InlineData("""{"name": "hello"}""", true, "hello")>]
[<InlineData("""{}""", false, "")>]
[<InlineData("""{"name": null}""", false, "")>]
let ``tryGetStringProperty gets optional string properties`` (json: string, expectSome: bool, expectedValue: string) =
    use doc = System.Text.Json.JsonDocument.Parse json
    let result = AgentToolCall.tryGetStringProperty doc.RootElement "name"

    if expectSome then
        Assert.Equal(Some expectedValue, result)
    else
        Assert.Equal(None, result)

[<Theory>]
[<InlineData("""{"file_path": "test.txt"}""", true, "test.txt", "")>]
[<InlineData("""{}""", false, "", "Missing required property 'file_path'")>]
[<InlineData("""{"file_path": 42}""", false, "", "must be a string")>]
let ``getRequiredStringProperty gets required string properties``
    (json: string, expectOk: bool, expectedValue: string, expectedMsg: string)
    =
    use doc = System.Text.Json.JsonDocument.Parse json
    let result = AgentToolCall.getRequiredStringProperty doc.RootElement "file_path"

    match result with
    | Ok value ->
        Assert.True expectOk
        Assert.Equal(expectedValue, value)
    | Error msg ->
        Assert.False expectOk
        Assert.Contains(expectedMsg, msg)

[<Theory>]
[<InlineData("""{"line": 42}""", true, 42, "")>]
[<InlineData("""{}""", false, 0, "Missing required property 'line'")>]
[<InlineData("""{"line": "abc"}""", false, 0, "must be an integer")>]
let ``getRequiredInt32Property gets required integer properties``
    (json: string, expectOk: bool, _expectedValue: int, expectedMsg: string)
    =
    use doc = System.Text.Json.JsonDocument.Parse json
    let result = AgentToolCall.getRequiredInt32Property doc.RootElement "line"

    match result with
    | Ok value ->
        Assert.True expectOk
        Assert.Equal(_expectedValue, value)
    | Error msg ->
        Assert.False expectOk
        Assert.Contains(expectedMsg, msg)

[<Fact>]
let ``handleReadFile forwards file_path to tools.readFile and returns content`` () =
    async {
        let config =
            let cfg = mockAgentConfig ()

            { cfg with
                tools =
                    { cfg.tools with
                        readFile =
                            fun path ->
                                Assert.Equal("test.txt", path)
                                Ok "hello from file" } }

        use doc = System.Text.Json.JsonDocument.Parse """{"file_path": "test.txt"}"""
        let! result = AgentToolCall.handleReadFile config doc.RootElement
        Assert.Equal("hello from file", assertOk result)
    }
    |> Async.RunSynchronously


[<Fact>]
let ``handleWriteFile forwards file_path and content to tools.writeFile`` () =
    async {
        let mutable capturedArgs = None

        let config =
            let cfg = mockAgentConfig ()

            { cfg with
                tools =
                    { cfg.tools with
                        writeFile =
                            fun path content ->
                                capturedArgs <- Some(path, content)
                                Ok "written" } }

        use doc =
            System.Text.Json.JsonDocument.Parse """{"file_path": "test.txt", "content": "hello"}"""

        let! result = AgentToolCall.handleWriteFile config doc.RootElement
        assertOk result |> ignore
        Assert.Equal(Some("test.txt", "hello"), capturedArgs)
    }
    |> Async.RunSynchronously


[<Fact>]
let ``handleRunCommand forwards command_line and cwd to tools.runCommand`` () =
    async {
        let mutable capturedArgs = None

        let config =
            let cfg = mockAgentConfig ()

            { cfg with
                tools =
                    { cfg.tools with
                        runCommand =
                            fun cmd cwd ->
                                capturedArgs <- Some(cmd, cwd)
                                async { return Ok "output" } } }

        use doc =
            System.Text.Json.JsonDocument.Parse """{"command_line": "ls", "cwd": "/tmp"}"""

        let! result = AgentToolCall.handleRunCommand config doc.RootElement
        assertOk result |> ignore
        Assert.Equal(Some("ls", "/tmp"), capturedArgs)
    }
    |> Async.RunSynchronously


[<Theory>]
[<InlineData("""{"directory_path": "/tmp"}""", "/tmp")>]
[<InlineData("""{}""", "")>]
let ``handleListDirectory forwards directory_path to tools.listDirectory`` (json: string, expectedPath: string) =
    async {
        let mutable capturedPath = None

        let config =
            let cfg = mockAgentConfig ()

            { cfg with
                tools =
                    { cfg.tools with
                        listDirectory =
                            fun path ->
                                capturedPath <- Some path
                                Ok "listing" } }

        use doc = System.Text.Json.JsonDocument.Parse json
        let! result = AgentToolCall.handleListDirectory config doc.RootElement
        assertOk result |> ignore
        Assert.Equal(Some expectedPath, capturedPath)
    }
    |> Async.RunSynchronously

[<Theory>]
[<InlineData("""{"query": "hello", "directory_path": "/src"}""", "/src")>]
[<InlineData("""{"query": "hello"}""", "")>]
let ``handleGrepSearch forwards query and directory_path to tools.grepSearch`` (json: string, expectedPath: string) =
    async {
        let config =
            let cfg = mockAgentConfig ()

            { cfg with
                tools =
                    { cfg.tools with
                        grepSearch =
                            fun query path ->
                                Assert.Equal("hello", query)
                                Assert.Equal(expectedPath, path)
                                Ok $"Matches for 'hello' in '{path}':\nfoo.txt:1: hello" } }

        use doc = System.Text.Json.JsonDocument.Parse json
        let! result = AgentToolCall.handleGrepSearch config doc.RootElement
        Assert.Contains("foo.txt", assertOk result)
    }
    |> Async.RunSynchronously


[<Fact>]
let ``handlePatchFile forwards file_path, target, and replacement to tools.patchFile`` () =
    async {
        let mutable capturedArgs = None

        let config =
            let cfg = mockAgentConfig ()

            { cfg with
                tools =
                    { cfg.tools with
                        patchFile =
                            fun path target replacement ->
                                capturedArgs <- Some(path, target, replacement)
                                Ok "patched" } }

        use doc =
            System.Text.Json.JsonDocument.Parse """{"file_path": "test.txt", "target": "old", "replacement": "new"}"""

        let! result = AgentToolCall.handlePatchFile config doc.RootElement
        assertOk result |> ignore
        Assert.Equal(Some("test.txt", "old", "new"), capturedArgs)
    }
    |> Async.RunSynchronously


[<Fact>]
let ``handleReadFileLines forwards file_path, start_line, end_line to tools.readFileLines`` () =
    async {
        let mutable capturedArgs = None

        let config =
            let cfg = mockAgentConfig ()

            { cfg with
                tools =
                    { cfg.tools with
                        readFileLines =
                            fun path start endLine ->
                                capturedArgs <- Some(path, start, endLine)
                                Ok "lines" } }

        use doc =
            System.Text.Json.JsonDocument.Parse """{"file_path": "test.txt", "start_line": 10, "end_line": 20}"""

        let! result = AgentToolCall.handleReadFileLines config doc.RootElement
        assertOk result |> ignore
        Assert.Equal(Some("test.txt", 10, 20), capturedArgs)
    }
    |> Async.RunSynchronously


[<Theory>]
[<InlineData("""{"pattern": "*.fs", "directory_path": "/src"}""", "*.fs", "/src")>]
[<InlineData("""{"pattern": "*.fs"}""", "*.fs", "")>]
let ``handleFindFiles forwards pattern and directory_path to tools.findFiles``
    (json: string, expectedPattern: string, expectedPath: string)
    =
    async {
        let mutable capturedArgs = None

        let config =
            let cfg = mockAgentConfig ()

            { cfg with
                tools =
                    { cfg.tools with
                        findFiles =
                            fun pattern path ->
                                capturedArgs <- Some(pattern, path)
                                Ok "files" } }

        use doc = System.Text.Json.JsonDocument.Parse json
        let! result = AgentToolCall.handleFindFiles config doc.RootElement
        assertOk result |> ignore
        Assert.Equal(Some(expectedPattern, expectedPath), capturedArgs)
    }
    |> Async.RunSynchronously

[<Fact>]
let ``toolRegistrations each definition has a corresponding handler`` () =
    AgentToolCall.toolRegistrations
    |> Array.iter (fun reg ->
        let name = reg.definition.``function``.name
        Assert.True(AgentToolCall.toolHandlers.ContainsKey name, $"Handler missing for tool '{name}'"))

[<Fact>]
let ``toolRegistrations each definition appears in toolsDefinition`` () =
    let defNames =
        AgentToolCall.toolsDefinition ()
        |> Array.map (fun d -> d.``function``.name)
        |> set

    AgentToolCall.toolRegistrations
    |> Array.iter (fun reg ->
        let name = reg.definition.``function``.name
        Assert.True(defNames.Contains name, $"Definition '{name}' missing from toolsDefinition"))

[<Theory>]
[<InlineData("read_file", true)>]
[<InlineData("list_directory", true)>]
[<InlineData("grep_search", true)>]
[<InlineData("read_file_lines", true)>]
[<InlineData("find_files", true)>]
[<InlineData("write_file", false)>]
[<InlineData("run_command", false)>]
[<InlineData("patch_file", false)>]
let ``isReadOnlyTool correctly classifies each tool as read-only or not`` (toolName: string, expected: bool) =
    let toolCall: LlmClient.ToolCall =
        { id = "call_1"
          ``type`` = "function"
          ``function`` = { name = toolName; arguments = "{}" } }

    Assert.Equal(expected, AgentToolCall.isReadOnlyTool toolCall)

[<Theory>]
[<InlineData("y", true)>]
[<InlineData("Y", true)>]
[<InlineData("n", false)>]
[<InlineData("", false)>]
let ``confirmToolCall returns true for y/Y input and false for n or empty input`` (input: string, expected: bool) =
    let mutable written = []

    let config =
        let cfg = mockAgentConfig ()

        { cfg with
            runtimeConfig =
                { cfg.runtimeConfig with
                    autoConfirm = Off }
            interactive =
                { cfg.interactive with
                    write = fun s -> written <- written @ [ s ]
                    readLine = fun () -> input } }

    let toolCall: LlmClient.ToolCall =
        { id = "call_confirm"
          ``type`` = "function"
          ``function`` =
            { name = AgentToolCall.ToolName.toString AgentToolCall.ReadFile
              arguments = "{}" } }

    let result =
        AgentToolCall.confirmToolCall config.interactive config.runtimeConfig toolCall

    Assert.Equal(expected, result)

    Assert.True(
        written
        |> List.exists (fun s -> s.Contains(AgentToolCall.ToolName.toString AgentToolCall.ReadFile))
    )

[<Theory>]
[<InlineData("All", "run_command", "all")>]
[<InlineData("ReadsOnly", "read_file", "reads")>]
[<InlineData("ReadsOnly", "write_file", "prompt")>]
let ``confirmToolCall auto-confirms according to mode`` (modeStr: string, toolName: string, kind: string) =
    let mode = if modeStr = "All" then All else ReadsOnly
    let mutable output = []
    let mutable prompted = false

    let config =
        let cfg = mockAgentConfig ()

        { cfg with
            runtimeConfig =
                { cfg.runtimeConfig with
                    autoConfirm = mode }
            interactive =
                { cfg.interactive with
                    writeLine = fun s -> output <- output @ [ s ]
                    write = fun _ -> prompted <- true
                    readLine = fun () -> "y" } }

    let toolCall: LlmClient.ToolCall =
        { id = "call_1"
          ``type`` = "function"
          ``function`` = { name = toolName; arguments = "{}" } }

    let result =
        AgentToolCall.confirmToolCall config.interactive config.runtimeConfig toolCall

    Assert.True result

    match kind with
    | "all" -> Assert.True(output |> List.exists (fun s -> s.Contains "Auto-confirm" && s.Contains "all"))
    | "reads" -> Assert.True(output |> List.exists (fun s -> s.Contains "Auto-confirm" && s.Contains "reads"))
    | "prompt" -> Assert.True prompted
    | _ -> ()

[<Fact>]
let ``executeToolCall returns cancellation Error when user declines confirmation prompt`` () =
    async {
        let mutable cancelMsg = ""

        let config =
            let cfg = mockAgentConfig ()

            { cfg with
                interactive =
                    { cfg.interactive with
                        confirmToolCall = fun _ _ _ -> false
                        writeLine = fun s -> cancelMsg <- s } }

        let toolCall: LlmClient.ToolCall =
            { id = "call_cancel"
              ``type`` = "function"
              ``function`` =
                { name = AgentToolCall.ToolName.toString AgentToolCall.ReadFile
                  arguments = "{\"file_path\": \"test.txt\"}" } }

        let! result = AgentToolCall.executeToolCall config toolCall
        Assert.Contains("cancelled", assertError result)
        Assert.Contains(AgentToolCall.ToolName.toString AgentToolCall.ReadFile, cancelMsg)
    }
    |> Async.RunSynchronously

[<Fact>]
let ``executeToolCall returns Error when function name is not registered in tool handlers`` () =
    async {
        let toolCall: LlmClient.ToolCall =
            { id = "call_unknown"
              ``type`` = "function"
              ``function`` =
                { name = "nonexistent_tool"
                  arguments = "{}" } }

        let! result = AgentToolCall.executeToolCall (mockAgentConfig ()) toolCall
        Assert.Contains("nonexistent_tool", assertError result)
    }
    |> Async.RunSynchronously

[<Fact>]
let ``executeToolCall returns Error when tool arguments contain malformed JSON`` () =
    async {
        let toolCall: LlmClient.ToolCall =
            { id = "call_bad"
              ``type`` = "function"
              ``function`` =
                { name = AgentToolCall.ToolName.toString AgentToolCall.ReadFile
                  arguments = "NOT VALID JSON" } }

        let! result = AgentToolCall.executeToolCall (mockAgentConfig ()) toolCall
        Assert.Contains("read_file", assertError result)
    }
    |> Async.RunSynchronously
