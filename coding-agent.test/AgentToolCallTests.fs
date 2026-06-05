module CodingAgent.AgentToolCallTests

open Xunit
open CodingAgent
open TestHelpers

[<Fact>]
let ``toolRegistrations each definition has a corresponding handler`` () =
    AgentToolCall.toolRegistrations
    |> Array.iter (fun reg ->
        let name = reg.definition.``function``.name
        Assert.True(AgentToolCall.toolHandlers.ContainsKey name, sprintf "Handler missing for tool '%s'" name))

[<Fact>]
let ``toolRegistrations each definition appears in toolsDefinition`` () =
    let defNames =
        AgentToolCall.toolsDefinition |> Array.map (fun d -> d.``function``.name) |> set

    AgentToolCall.toolRegistrations
    |> Array.iter (fun reg ->
        let name = reg.definition.``function``.name
        Assert.True(defNames.Contains name, sprintf "Definition '%s' missing from toolsDefinition" name))

[<Fact>]
let ``readOnlyTools matches toolRegistrations readOnly flags`` () =
    let expectedReadOnly =
        AgentToolCall.toolRegistrations
        |> Array.filter (fun r -> r.readOnly)
        |> Array.map (fun r -> r.definition.``function``.name)
        |> set

    Assert.Equal<Set<string>>(expectedReadOnly, AgentToolCall.readOnlyTools)

[<Theory>]
[<InlineData("read_file", true)>]
[<InlineData("list_directory", true)>]
[<InlineData("grep_search", true)>]
[<InlineData("read_file_lines", true)>]
[<InlineData("find_files", true)>]
[<InlineData("write_file", false)>]
[<InlineData("run_command", false)>]
[<InlineData("patch_file", false)>]
let ``isReadOnlyTool returns expected value`` (toolName: string, expected: bool) =
    let toolCall: LlmClient.ToolCall =
        { id = "call_1"
          ``type`` = "function"
          ``function`` = { name = toolName; arguments = "{}" } }

    Assert.Equal(expected, AgentToolCall.isReadOnlyTool toolCall)

[<Fact>]
let ``confirmToolCall returns true when user types 'y'`` () =
    let mutable written = []

    let config =
        { mockAgentConfig with
            autoConfirm = Off
            write = fun s -> written <- written @ [ s ]
            readLine = fun () -> "y" }

    let toolCall: LlmClient.ToolCall =
        { id = "call_confirm"
          ``type`` = "function"
          ``function`` =
            { name = "read_file"
              arguments = "{\"file_path\": \"foo.txt\"}" } }

    let result = AgentToolCall.confirmToolCall config toolCall
    Assert.True result
    Assert.True(written |> List.exists (fun s -> s.Contains "read_file"))

[<Fact>]
let ``confirmToolCall returns true when user types 'Y' (case-insensitive)`` () =
    let config =
        { mockAgentConfig with
            autoConfirm = Off
            readLine = fun () -> "Y" }

    let toolCall: LlmClient.ToolCall =
        { id = "call_confirm"
          ``type`` = "function"
          ``function`` =
            { name = "write_file"
              arguments = "{}" } }

    Assert.True(AgentToolCall.confirmToolCall config toolCall)

[<Fact>]
let ``confirmToolCall returns false when user types 'n'`` () =
    let config =
        { mockAgentConfig with
            autoConfirm = Off
            readLine = fun () -> "n" }

    let toolCall: LlmClient.ToolCall =
        { id = "call_confirm"
          ``type`` = "function"
          ``function`` = { name = "read_file"; arguments = "{}" } }

    Assert.False(AgentToolCall.confirmToolCall config toolCall)

[<Fact>]
let ``confirmToolCall returns false when user presses Enter (empty input)`` () =
    let config =
        { mockAgentConfig with
            autoConfirm = Off
            readLine = fun () -> "" }

    let toolCall: LlmClient.ToolCall =
        { id = "call_confirm"
          ``type`` = "function"
          ``function`` = { name = "read_file"; arguments = "{}" } }

    Assert.False(AgentToolCall.confirmToolCall config toolCall)

[<Fact>]
let ``confirmToolCall auto-confirms all tools when autoConfirm = All`` () =
    let mutable output = []

    let config =
        { mockAgentConfig with
            autoConfirm = All
            writeLine = fun s -> output <- output @ [ s ] }

    let toolCall: LlmClient.ToolCall =
        { id = "call_1"
          ``type`` = "function"
          ``function`` =
            { name = "run_command"
              arguments = "{\"command_line\":\"rm -rf /\"}" } }

    let result = AgentToolCall.confirmToolCall config toolCall
    Assert.True result
    Assert.True(output |> List.exists (fun s -> s.Contains "Auto-confirm" && s.Contains "all"))

[<Fact>]
let ``confirmToolCall auto-confirms read tools when autoConfirm = ReadsOnly`` () =
    let mutable output = []

    let config =
        { mockAgentConfig with
            autoConfirm = ReadsOnly
            writeLine = fun s -> output <- output @ [ s ] }

    let toolCall: LlmClient.ToolCall =
        { id = "call_1"
          ``type`` = "function"
          ``function`` =
            { name = "read_file"
              arguments = "{\"file_path\":\"test.txt\"}" } }

    let result = AgentToolCall.confirmToolCall config toolCall
    Assert.True result
    Assert.True(output |> List.exists (fun s -> s.Contains "Auto-confirm" && s.Contains "reads"))

[<Fact>]
let ``confirmToolCall prompts for write tools when autoConfirm = ReadsOnly`` () =
    let mutable prompted = false

    let config =
        { mockAgentConfig with
            autoConfirm = ReadsOnly
            write = fun _ -> prompted <- true
            readLine = fun () -> "y" }

    let toolCall: LlmClient.ToolCall =
        { id = "call_1"
          ``type`` = "function"
          ``function`` =
            { name = "write_file"
              arguments = "{}" } }

    let result = AgentToolCall.confirmToolCall config toolCall
    Assert.True result
    Assert.True prompted

[<Fact>]
let ``executeToolCall dispatches to correct handler and passes parsed arguments`` () =
    task {
        let mutable capturedArgs = None

        let config =
            { mockAgentConfig with
                tools =
                    { mockAgentConfig.tools with
                        grepSearch =
                            fun query path ->
                                capturedArgs <- Some(query, path)
                                Ok "ok" } }

        let toolCall: LlmClient.ToolCall =
            { id = "call_1"
              ``type`` = "function"
              ``function`` =
                { name = "grep_search"
                  arguments = "{\"query\": \"hello\", \"directory_path\": \"/src\"}" } }

        let! result = AgentToolCall.executeToolCall config toolCall

        match result with
        | Ok _ -> Assert.Equal(Some("hello", "/src"), capturedArgs)
        | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)
    }

[<Fact>]
let ``executeToolCall returns Error when user cancels confirmation`` () =
    task {
        let mutable cancelMsg = ""

        let config =
            { mockAgentConfig with
                confirmToolCall = fun _ _ -> false
                writeLine = fun s -> cancelMsg <- s }

        let toolCall: LlmClient.ToolCall =
            { id = "call_cancel"
              ``type`` = "function"
              ``function`` =
                { name = "read_file"
                  arguments = "{\"file_path\": \"test.txt\"}" } }

        let! result = AgentToolCall.executeToolCall config toolCall

        match result with
        | Error errMsg ->
            Assert.Contains("cancelled", errMsg)
            Assert.Contains("read_file", cancelMsg)
        | Ok _ -> Assert.Fail "Expected Error when tool call is cancelled"
    }

[<Fact>]
let ``executeToolCall returns Error for unknown function name`` () =
    task {
        let toolCall: LlmClient.ToolCall =
            { id = "call_unknown"
              ``type`` = "function"
              ``function`` =
                { name = "nonexistent_tool"
                  arguments = "{}" } }

        let! result = AgentToolCall.executeToolCall mockAgentConfig toolCall

        match result with
        | Error errMsg -> Assert.Contains("nonexistent_tool", errMsg)
        | Ok _ -> Assert.Fail "Expected Error for unknown tool"
    }

[<Fact>]
let ``executeToolCall returns Error on invalid JSON arguments`` () =
    task {
        let toolCall: LlmClient.ToolCall =
            { id = "call_bad"
              ``type`` = "function"
              ``function`` =
                { name = "read_file"
                  arguments = "NOT VALID JSON" } }

        let! result = AgentToolCall.executeToolCall mockAgentConfig toolCall

        match result with
        | Error errMsg -> Assert.Contains("read_file", errMsg)
        | Ok _ -> Assert.Fail "Expected Error for invalid JSON"
    }

[<Fact>]
let ``executeToolCall read_file returns file content on success`` () =
    task {
        let config =
            { mockAgentConfig with
                tools =
                    { mockAgentConfig.tools with
                        readFile =
                            fun path ->
                                Assert.Equal("test.txt", path)
                                Ok "hello from file" } }

        let toolCall: LlmClient.ToolCall =
            { id = "call_1"
              ``type`` = "function"
              ``function`` =
                { name = "read_file"
                  arguments = "{\"file_path\": \"test.txt\"}" } }

        let! result = AgentToolCall.executeToolCall config toolCall

        match result with
        | Ok content -> Assert.Equal("hello from file", content)
        | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)
    }

[<Fact>]
let ``executeToolCall read_file returns Error when file_path is missing`` () =
    task {
        let toolCall: LlmClient.ToolCall =
            { id = "call_1"
              ``type`` = "function"
              ``function`` = { name = "read_file"; arguments = "{}" } }

        let! result = AgentToolCall.executeToolCall mockAgentConfig toolCall

        match result with
        | Error msg -> Assert.Contains("file_path", msg)
        | Ok _ -> Assert.Fail "Expected Error for missing file_path"
    }

[<Fact>]
let ``executeToolCall write_file returns Ok with correct arguments`` () =
    task {
        let mutable capturedArgs = None

        let config =
            { mockAgentConfig with
                tools =
                    { mockAgentConfig.tools with
                        writeFile =
                            fun path content ->
                                capturedArgs <- Some(path, content)
                                Ok "written" } }

        let toolCall: LlmClient.ToolCall =
            { id = "call_1"
              ``type`` = "function"
              ``function`` =
                { name = "write_file"
                  arguments = "{\"file_path\": \"test.txt\", \"content\": \"hello\"}" } }

        let! result = AgentToolCall.executeToolCall config toolCall

        match result with
        | Ok _ -> Assert.Equal(Some("test.txt", "hello"), capturedArgs)
        | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)
    }

[<Fact>]
let ``executeToolCall write_file returns Error when file_path is missing`` () =
    task {
        let toolCall: LlmClient.ToolCall =
            { id = "call_1"
              ``type`` = "function"
              ``function`` =
                { name = "write_file"
                  arguments = "{\"content\": \"hello\"}" } }

        let! result = AgentToolCall.executeToolCall mockAgentConfig toolCall

        match result with
        | Error msg -> Assert.Contains("file_path", msg)
        | Ok _ -> Assert.Fail "Expected Error for missing file_path"
    }

[<Fact>]
let ``executeToolCall write_file returns Error when content is missing`` () =
    task {
        let toolCall: LlmClient.ToolCall =
            { id = "call_1"
              ``type`` = "function"
              ``function`` =
                { name = "write_file"
                  arguments = "{\"file_path\": \"test.txt\"}" } }

        let! result = AgentToolCall.executeToolCall mockAgentConfig toolCall

        match result with
        | Error msg -> Assert.Contains("content", msg)
        | Ok _ -> Assert.Fail "Expected Error for missing content"
    }

[<Fact>]
let ``executeToolCall run_command returns Ok with correct arguments`` () =
    task {
        let mutable capturedArgs = None

        let config =
            { mockAgentConfig with
                tools =
                    { mockAgentConfig.tools with
                        runCommand =
                            fun cmd cwd ->
                                capturedArgs <- Some(cmd, cwd)
                                task { return Ok "output" } } }

        let toolCall: LlmClient.ToolCall =
            { id = "call_1"
              ``type`` = "function"
              ``function`` =
                { name = "run_command"
                  arguments = "{\"command_line\": \"ls\", \"cwd\": \"/tmp\"}" } }

        let! result = AgentToolCall.executeToolCall config toolCall

        match result with
        | Ok _ -> Assert.Equal(Some("ls", "/tmp"), capturedArgs)
        | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)
    }

[<Fact>]
let ``executeToolCall run_command returns Error when command_line is missing`` () =
    task {
        let toolCall: LlmClient.ToolCall =
            { id = "call_1"
              ``type`` = "function"
              ``function`` =
                { name = "run_command"
                  arguments = "{}" } }

        let! result = AgentToolCall.executeToolCall mockAgentConfig toolCall

        match result with
        | Error msg -> Assert.Contains("command_line", msg)
        | Ok _ -> Assert.Fail "Expected Error for missing command_line"
    }

[<Fact>]
let ``executeToolCall list_directory returns Ok with correct arguments`` () =
    task {
        let mutable capturedPath = None

        let config =
            { mockAgentConfig with
                tools =
                    { mockAgentConfig.tools with
                        listDirectory =
                            fun path ->
                                capturedPath <- Some path
                                Ok "listing" } }

        let toolCall: LlmClient.ToolCall =
            { id = "call_1"
              ``type`` = "function"
              ``function`` =
                { name = "list_directory"
                  arguments = "{\"directory_path\": \"/tmp\"}" } }

        let! result = AgentToolCall.executeToolCall config toolCall

        match result with
        | Ok _ -> Assert.Equal(Some "/tmp", capturedPath)
        | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)
    }

[<Fact>]
let ``executeToolCall list_directory uses empty string when directory_path is omitted`` () =
    task {
        let mutable capturedPath = None

        let config =
            { mockAgentConfig with
                tools =
                    { mockAgentConfig.tools with
                        listDirectory =
                            fun path ->
                                capturedPath <- Some path
                                Ok "listing" } }

        let toolCall: LlmClient.ToolCall =
            { id = "call_1"
              ``type`` = "function"
              ``function`` =
                { name = "list_directory"
                  arguments = "{}" } }

        let! result = AgentToolCall.executeToolCall config toolCall

        match result with
        | Ok _ -> Assert.Equal(Some "", capturedPath)
        | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)
    }

[<Fact>]
let ``executeToolCall grep_search returns match results with file paths`` () =
    task {
        let config =
            { mockAgentConfig with
                tools =
                    { mockAgentConfig.tools with
                        grepSearch =
                            fun query path ->
                                Assert.Equal("hello", query)
                                Assert.Equal("/src", path)
                                Ok "Found matches for 'hello' in '/src':\nfoo.txt:1: hello" } }

        let toolCall: LlmClient.ToolCall =
            { id = "call_grep"
              ``type`` = "function"
              ``function`` =
                { name = "grep_search"
                  arguments = "{\"query\": \"hello\", \"directory_path\": \"/src\"}" } }

        let! result = AgentToolCall.executeToolCall config toolCall

        match result with
        | Ok output -> Assert.Contains("foo.txt", output)
        | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)
    }

[<Fact>]
let ``executeToolCall grep_search uses empty string when directory_path is omitted`` () =
    task {
        let config =
            { mockAgentConfig with
                tools =
                    { mockAgentConfig.tools with
                        grepSearch =
                            fun query path ->
                                Assert.Equal("hello", query)
                                Assert.Equal("", path)
                                Ok(sprintf "Matches for 'hello' in '%s':\nfoo.txt:1: hello" path) } }

        let toolCall: LlmClient.ToolCall =
            { id = "call_grep_nodir"
              ``type`` = "function"
              ``function`` =
                { name = "grep_search"
                  arguments = "{\"query\": \"hello\"}" } }

        let! result = AgentToolCall.executeToolCall config toolCall

        match result with
        | Ok output -> Assert.Contains("foo.txt", output)
        | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)
    }

[<Fact>]
let ``executeToolCall grep_search returns Error when query is missing`` () =
    task {
        let toolCall: LlmClient.ToolCall =
            { id = "call_grep"
              ``type`` = "function"
              ``function`` =
                { name = "grep_search"
                  arguments = "{\"directory_path\": \"/src\"}" } }

        let! result = AgentToolCall.executeToolCall mockAgentConfig toolCall

        match result with
        | Error msg -> Assert.Contains("query", msg)
        | Ok _ -> Assert.Fail "Expected Error for missing query"
    }

[<Fact>]
let ``executeToolCall patch_file returns Ok with correct arguments`` () =
    task {
        let mutable capturedArgs = None

        let config =
            { mockAgentConfig with
                tools =
                    { mockAgentConfig.tools with
                        patchFile =
                            fun path target replacement ->
                                capturedArgs <- Some(path, target, replacement)
                                Ok "patched" } }

        let toolCall: LlmClient.ToolCall =
            { id = "call_1"
              ``type`` = "function"
              ``function`` =
                { name = "patch_file"
                  arguments = "{\"file_path\": \"test.txt\", \"target\": \"old\", \"replacement\": \"new\"}" } }

        let! result = AgentToolCall.executeToolCall config toolCall

        match result with
        | Ok _ -> Assert.Equal(Some("test.txt", "old", "new"), capturedArgs)
        | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)
    }

[<Fact>]
let ``executeToolCall patch_file returns Error when file_path is missing`` () =
    task {
        let toolCall: LlmClient.ToolCall =
            { id = "call_1"
              ``type`` = "function"
              ``function`` =
                { name = "patch_file"
                  arguments = "{\"target\": \"old\", \"replacement\": \"new\"}" } }

        let! result = AgentToolCall.executeToolCall mockAgentConfig toolCall

        match result with
        | Error msg -> Assert.Contains("file_path", msg)
        | Ok _ -> Assert.Fail "Expected Error for missing file_path"
    }

[<Fact>]
let ``executeToolCall patch_file returns Error when target is missing`` () =
    task {
        let toolCall: LlmClient.ToolCall =
            { id = "call_1"
              ``type`` = "function"
              ``function`` =
                { name = "patch_file"
                  arguments = "{\"file_path\": \"test.txt\", \"replacement\": \"new\"}" } }

        let! result = AgentToolCall.executeToolCall mockAgentConfig toolCall

        match result with
        | Error msg -> Assert.Contains("target", msg)
        | Ok _ -> Assert.Fail "Expected Error for missing target"
    }

[<Fact>]
let ``executeToolCall patch_file returns Error when replacement is missing`` () =
    task {
        let toolCall: LlmClient.ToolCall =
            { id = "call_1"
              ``type`` = "function"
              ``function`` =
                { name = "patch_file"
                  arguments = "{\"file_path\": \"test.txt\", \"target\": \"old\"}" } }

        let! result = AgentToolCall.executeToolCall mockAgentConfig toolCall

        match result with
        | Error msg -> Assert.Contains("replacement", msg)
        | Ok _ -> Assert.Fail "Expected Error for missing replacement"
    }

[<Fact>]
let ``executeToolCall read_file_lines returns Ok with correct arguments`` () =
    task {
        let mutable capturedArgs = None

        let config =
            { mockAgentConfig with
                tools =
                    { mockAgentConfig.tools with
                        readFileLines =
                            fun path start endLine ->
                                capturedArgs <- Some(path, start, endLine)
                                Ok "lines" } }

        let toolCall: LlmClient.ToolCall =
            { id = "call_1"
              ``type`` = "function"
              ``function`` =
                { name = "read_file_lines"
                  arguments = "{\"file_path\": \"test.txt\", \"start_line\": 10, \"end_line\": 20}" } }

        let! result = AgentToolCall.executeToolCall config toolCall

        match result with
        | Ok _ -> Assert.Equal(Some("test.txt", 10, 20), capturedArgs)
        | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)
    }

[<Fact>]
let ``executeToolCall read_file_lines returns Error when file_path is missing`` () =
    task {
        let toolCall: LlmClient.ToolCall =
            { id = "call_1"
              ``type`` = "function"
              ``function`` =
                { name = "read_file_lines"
                  arguments = "{\"start_line\": 10, \"end_line\": 20}" } }

        let! result = AgentToolCall.executeToolCall mockAgentConfig toolCall

        match result with
        | Error msg -> Assert.Contains("file_path", msg)
        | Ok _ -> Assert.Fail "Expected Error for missing file_path"
    }

[<Fact>]
let ``executeToolCall read_file_lines returns Error when start_line is missing`` () =
    task {
        let toolCall: LlmClient.ToolCall =
            { id = "call_1"
              ``type`` = "function"
              ``function`` =
                { name = "read_file_lines"
                  arguments = "{\"file_path\": \"test.txt\", \"end_line\": 20}" } }

        let! result = AgentToolCall.executeToolCall mockAgentConfig toolCall

        match result with
        | Error msg -> Assert.Contains("start_line", msg)
        | Ok _ -> Assert.Fail "Expected Error for missing start_line"
    }

[<Fact>]
let ``executeToolCall read_file_lines returns Error when end_line is missing`` () =
    task {
        let toolCall: LlmClient.ToolCall =
            { id = "call_1"
              ``type`` = "function"
              ``function`` =
                { name = "read_file_lines"
                  arguments = "{\"file_path\": \"test.txt\", \"start_line\": 10}" } }

        let! result = AgentToolCall.executeToolCall mockAgentConfig toolCall

        match result with
        | Error msg -> Assert.Contains("end_line", msg)
        | Ok _ -> Assert.Fail "Expected Error for missing end_line"
    }

[<Fact>]
let ``executeToolCall find_files returns Ok with correct arguments`` () =
    task {
        let mutable capturedArgs = None

        let config =
            { mockAgentConfig with
                tools =
                    { mockAgentConfig.tools with
                        findFiles =
                            fun pattern path ->
                                capturedArgs <- Some(pattern, path)
                                Ok "files" } }

        let toolCall: LlmClient.ToolCall =
            { id = "call_1"
              ``type`` = "function"
              ``function`` =
                { name = "find_files"
                  arguments = "{\"pattern\": \"*.fs\", \"directory_path\": \"/src\"}" } }

        let! result = AgentToolCall.executeToolCall config toolCall

        match result with
        | Ok _ -> Assert.Equal(Some("*.fs", "/src"), capturedArgs)
        | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)
    }

[<Fact>]
let ``executeToolCall find_files uses empty string when directory_path is omitted`` () =
    task {
        let mutable capturedArgs = None

        let config =
            { mockAgentConfig with
                tools =
                    { mockAgentConfig.tools with
                        findFiles =
                            fun pattern path ->
                                capturedArgs <- Some(pattern, path)
                                Ok "files" } }

        let toolCall: LlmClient.ToolCall =
            { id = "call_1"
              ``type`` = "function"
              ``function`` =
                { name = "find_files"
                  arguments = "{\"pattern\": \"*.fs\"}" } }

        let! result = AgentToolCall.executeToolCall config toolCall

        match result with
        | Ok _ -> Assert.Equal(Some("*.fs", ""), capturedArgs)
        | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)
    }

[<Fact>]
let ``executeToolCall find_files returns Error when pattern is missing`` () =
    task {
        let toolCall: LlmClient.ToolCall =
            { id = "call_1"
              ``type`` = "function"
              ``function`` =
                { name = "find_files"
                  arguments = "{\"directory_path\": \"/src\"}" } }

        let! result = AgentToolCall.executeToolCall mockAgentConfig toolCall

        match result with
        | Error msg -> Assert.Contains("pattern", msg)
        | Ok _ -> Assert.Fail "Expected Error for missing pattern"
    }
