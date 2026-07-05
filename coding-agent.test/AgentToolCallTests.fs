module CodingAgent.AgentToolCallTests

open Xunit
open CodingAgent
open TestHelpers

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
                            fun query isRegex ignoreCase path ->
                                Assert.Equal("hello", query)
                                Assert.Equal(expectedPath, path)
                                Ok $"Matches for 'hello' in '{path}':\nfoo.txt:1: hello" } }

        use doc = System.Text.Json.JsonDocument.Parse json
        let! result = AgentToolCall.handleGrepSearch config doc.RootElement
        Assert.Contains("foo.txt", assertOk result)
    }
    |> Async.RunSynchronously

[<Theory>]
[<InlineData("""{"query": "hello", "is_regex": true, "ignore_case": false, "directory_path": "/src"}""",
             "/src",
             true,
             false)>]
[<InlineData("""{"query": "hello", "is_regex": false, "directory_path": "/src"}""", "/src", false, true)>]
[<InlineData("""{"query": "hello"}""", "", false, true)>]
let ``handleGrepSearch forwards flags to tools.grepSearch``
    (json: string, expectedPath: string, expectedRegex: bool, expectedIc: bool)
    =
    async {
        let mutable capturedFlags = None

        let config =
            let cfg = mockAgentConfig ()

            { cfg with
                tools =
                    { cfg.tools with
                        grepSearch =
                            fun query isRegex ignoreCase path ->
                                capturedFlags <- Some(isRegex, ignoreCase, path)
                                Ok "matches" } }

        use doc = System.Text.Json.JsonDocument.Parse json
        let! result = AgentToolCall.handleGrepSearch config doc.RootElement
        assertOk result |> ignore
        Assert.Equal(Some(expectedRegex, expectedIc, expectedPath), capturedFlags)
    }
    |> Async.RunSynchronously


[<Theory>]
[<InlineData("""{"file_path": "test.txt", "target": "old", "replacement": "new"}""", false)>]
[<InlineData("""{"file_path": "test.txt", "target": "old", "replacement": "new", "is_regex": true}""", true)>]
let ``handlePatchFile forwards file_path, target, replacement, and is_regex to tools.patchFile``
    (json: string, expectedIsRegex: bool)
    =
    async {
        let mutable capturedArgs = None

        let config =
            let cfg = mockAgentConfig ()

            { cfg with
                tools =
                    { cfg.tools with
                        patchFile =
                            fun path target replacement isRegex ->
                                capturedArgs <- Some(path, target, replacement, isRegex)
                                Ok "patched" } }

        use doc = System.Text.Json.JsonDocument.Parse json
        let! result = AgentToolCall.handlePatchFile config doc.RootElement
        assertOk result |> ignore
        Assert.Equal(Some("test.txt", "old", "new", expectedIsRegex), capturedArgs)
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
[<InlineData("move_file", false)>]
[<InlineData("create_directory", false)>]
[<InlineData("delete_file", false)>]
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

[<Fact>]
let ``handleMoveFile forwards source, destination, overwrite to tools.moveFile`` () =
    async {
        let mutable capturedArgs = None

        let config =
            let cfg = mockAgentConfig ()

            { cfg with
                tools =
                    { cfg.tools with
                        moveFile =
                            fun source destination overwrite ->
                                capturedArgs <- Some(source, destination, overwrite)
                                Ok "moved" } }

        use doc =
            System.Text.Json.JsonDocument.Parse """{"source": "src.txt", "destination": "dst.txt", "overwrite": true}"""

        let! result = AgentToolCall.handleMoveFile config doc.RootElement
        assertOk result |> ignore
        Assert.Equal(Some("src.txt", "dst.txt", true), capturedArgs)
    }
    |> Async.RunSynchronously

[<Fact>]
let ``handleMoveFile defaults overwrite to false when not specified`` () =
    async {
        let mutable capturedArgs = None

        let config =
            let cfg = mockAgentConfig ()

            { cfg with
                tools =
                    { cfg.tools with
                        moveFile =
                            fun source destination overwrite ->
                                capturedArgs <- Some(source, destination, overwrite)
                                Ok "moved" } }

        use doc =
            System.Text.Json.JsonDocument.Parse """{"source": "src.txt", "destination": "dst.txt"}"""

        let! result = AgentToolCall.handleMoveFile config doc.RootElement
        assertOk result |> ignore
        Assert.Equal(Some("src.txt", "dst.txt", false), capturedArgs)
    }
    |> Async.RunSynchronously

[<Fact>]
let ``handleMoveFile returns Error when source is missing`` () =
    async {
        let config = mockAgentConfig ()

        use doc = System.Text.Json.JsonDocument.Parse """{"destination": "dst.txt"}"""

        let! result = AgentToolCall.handleMoveFile config doc.RootElement
        Assert.Contains("Missing required property 'source'", assertError result)
    }
    |> Async.RunSynchronously

[<Fact>]
let ``handleMoveFile returns Error when destination is missing`` () =
    async {
        let config = mockAgentConfig ()

        use doc = System.Text.Json.JsonDocument.Parse """{"source": "src.txt"}"""

        let! result = AgentToolCall.handleMoveFile config doc.RootElement
        Assert.Contains("Missing required property 'destination'", assertError result)
    }
    |> Async.RunSynchronously

[<Fact>]
let ``handleCreateDirectory forwards path and exist_ok to tools.createDirectory`` () =
    async {
        let mutable capturedArgs = None

        let config =
            let cfg = mockAgentConfig ()

            { cfg with
                tools =
                    { cfg.tools with
                        createDirectory =
                            fun path existOk ->
                                capturedArgs <- Some(path, existOk)
                                Ok "created" } }

        use doc =
            System.Text.Json.JsonDocument.Parse """{"path": "/tmp/newdir", "exist_ok": true}"""

        let! result = AgentToolCall.handleCreateDirectory config doc.RootElement
        assertOk result |> ignore
        Assert.Equal(Some("/tmp/newdir", true), capturedArgs)
    }
    |> Async.RunSynchronously

[<Fact>]
let ``handleCreateDirectory defaults exist_ok to false when not specified`` () =
    async {
        let mutable capturedArgs = None

        let config =
            let cfg = mockAgentConfig ()

            { cfg with
                tools =
                    { cfg.tools with
                        createDirectory =
                            fun path existOk ->
                                capturedArgs <- Some(path, existOk)
                                Ok "created" } }

        use doc = System.Text.Json.JsonDocument.Parse """{"path": "/tmp/newdir"}"""

        let! result = AgentToolCall.handleCreateDirectory config doc.RootElement
        assertOk result |> ignore
        Assert.Equal(Some("/tmp/newdir", false), capturedArgs)
    }
    |> Async.RunSynchronously

[<Fact>]
let ``handleCreateDirectory returns Error when path is missing`` () =
    async {
        let config = mockAgentConfig ()

        use doc = System.Text.Json.JsonDocument.Parse """{}"""

        let! result = AgentToolCall.handleCreateDirectory config doc.RootElement
        Assert.Contains("Missing required property 'path'", assertError result)
    }
    |> Async.RunSynchronously

[<Fact>]
let ``handleDeleteFile forwards file_path to tools.deleteFile`` () =
    async {
        let mutable capturedPath = None

        let config =
            let cfg = mockAgentConfig ()

            { cfg with
                tools =
                    { cfg.tools with
                        deleteFile =
                            fun path ->
                                capturedPath <- Some path
                                Ok "deleted" } }

        use doc = System.Text.Json.JsonDocument.Parse """{"file_path": "test.txt"}"""

        let! result = AgentToolCall.handleDeleteFile config doc.RootElement
        assertOk result |> ignore
        Assert.Equal(Some "test.txt", capturedPath)
    }
    |> Async.RunSynchronously

[<Fact>]
let ``handleDeleteFile returns Error when file_path is missing`` () =
    async {
        let config = mockAgentConfig ()

        use doc = System.Text.Json.JsonDocument.Parse """{}"""

        let! result = AgentToolCall.handleDeleteFile config doc.RootElement
        Assert.Contains("Missing required property 'file_path'", assertError result)
    }
    |> Async.RunSynchronously
