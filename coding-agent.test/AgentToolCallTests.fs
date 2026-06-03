module CodingAgent.AgentToolCallTests

open Xunit
open CodingAgent
open TestHelpers

[<Fact>]
let ``isReadOnlyTool returns true for read_file`` () =
    let toolCall: LlmClient.ToolCall =
        { id = "call_1"
          ``type`` = "function"
          ``function`` = { name = "read_file"; arguments = "{}" } }

    Assert.True(AgentToolCall.isReadOnlyTool toolCall)

[<Fact>]
let ``isReadOnlyTool returns true for list_directory`` () =
    let toolCall: LlmClient.ToolCall =
        { id = "call_1"
          ``type`` = "function"
          ``function`` =
            { name = "list_directory"
              arguments = "{}" } }

    Assert.True(AgentToolCall.isReadOnlyTool toolCall)

[<Fact>]
let ``isReadOnlyTool returns true for grep_search`` () =
    let toolCall: LlmClient.ToolCall =
        { id = "call_1"
          ``type`` = "function"
          ``function`` =
            { name = "grep_search"
              arguments = "{}" } }

    Assert.True(AgentToolCall.isReadOnlyTool toolCall)

[<Fact>]
let ``isReadOnlyTool returns true for read_file_lines`` () =
    let toolCall: LlmClient.ToolCall =
        { id = "call_1"
          ``type`` = "function"
          ``function`` =
            { name = "read_file_lines"
              arguments = "{}" } }

    Assert.True(AgentToolCall.isReadOnlyTool toolCall)

[<Fact>]
let ``isReadOnlyTool returns true for find_files`` () =
    let toolCall: LlmClient.ToolCall =
        { id = "call_1"
          ``type`` = "function"
          ``function`` =
            { name = "find_files"
              arguments = "{}" } }

    Assert.True(AgentToolCall.isReadOnlyTool toolCall)

[<Fact>]
let ``isReadOnlyTool returns false for write_file`` () =
    let toolCall: LlmClient.ToolCall =
        { id = "call_1"
          ``type`` = "function"
          ``function`` =
            { name = "write_file"
              arguments = "{}" } }

    Assert.False(AgentToolCall.isReadOnlyTool toolCall)

[<Fact>]
let ``isReadOnlyTool returns false for run_command`` () =
    let toolCall: LlmClient.ToolCall =
        { id = "call_1"
          ``type`` = "function"
          ``function`` =
            { name = "run_command"
              arguments = "{}" } }

    Assert.False(AgentToolCall.isReadOnlyTool toolCall)

[<Fact>]
let ``isReadOnlyTool returns false for patch_file`` () =
    let toolCall: LlmClient.ToolCall =
        { id = "call_1"
          ``type`` = "function"
          ``function`` =
            { name = "patch_file"
              arguments = "{}" } }

    Assert.False(AgentToolCall.isReadOnlyTool toolCall)

[<Fact>]
let ``confirmToolCall returns true when user types 'y'`` () =
    let mutable written = []

    let config =
        { mockAgentConfig with
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
let ``confirmToolCall prompts for all tools when autoConfirm = Off`` () =
    let mutable prompted = false

    let config =
        { mockAgentConfig with
            autoConfirm = Off
            write = fun _ -> prompted <- true
            readLine = fun () -> "y" }

    let toolCall: LlmClient.ToolCall =
        { id = "call_1"
          ``type`` = "function"
          ``function`` =
            { name = "read_file"
              arguments = "{\"file_path\":\"test.txt\"}" } }

    let result = AgentToolCall.confirmToolCall config toolCall
    Assert.True result
    Assert.True prompted

[<Fact>]
let ``confirmToolCall returns false when user declines with autoConfirm = Off`` () =
    let config =
        { mockAgentConfig with
            autoConfirm = Off
            readLine = fun () -> "n" }

    let toolCall: LlmClient.ToolCall =
        { id = "call_1"
          ``type`` = "function"
          ``function`` = { name = "read_file"; arguments = "{}" } }

    Assert.False(AgentToolCall.confirmToolCall config toolCall)

[<Fact>]
let ``tryGetJsonPropertyValue returns value when property exists`` () =
    let json = """{"cwd":"/tmp"}"""
    use doc = System.Text.Json.JsonDocument.Parse json
    let root = doc.RootElement
    let result = AgentToolCall.tryGetJsonPropertyValue root "cwd" ""
    Assert.Equal("/tmp", result)

[<Fact>]
let ``tryGetJsonPropertyValue returns default when property missing`` () =
    let json = """{"command_line":"ls"}"""
    use doc = System.Text.Json.JsonDocument.Parse json
    let root = doc.RootElement
    let result = AgentToolCall.tryGetJsonPropertyValue root "cwd" "default_cwd"
    Assert.Equal("default_cwd", result)

[<Fact>]
let ``executeToolCall read_file returns file content on success`` () =
    let toolCall: LlmClient.ToolCall =
        { id = "call_1"
          ``type`` = "function"
          ``function`` =
            { name = "read_file"
              arguments = "{\"file_path\": \"test.txt\"}" } }

    let customConfig =
        { mockAgentConfig with
            tools =
                { mockAgentConfig.tools with
                    readFile =
                        fun path ->
                            Assert.Equal("test.txt", path)
                            Ok "hello from file" } }

    let result = AgentToolCall.executeToolCall customConfig toolCall

    match result with
    | Ok content -> Assert.Equal("hello from file", content)
    | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)

[<Fact>]
let ``executeToolCall read_file returns Error for non-existent file`` () =
    let toolCall: LlmClient.ToolCall =
        { id = "call_1"
          ``type`` = "function"
          ``function`` =
            { name = "read_file"
              arguments = "{\"file_path\": \"/definitely/does/not/exist.txt\"}" } }

    let customConfig =
        { mockAgentConfig with
            tools =
                { mockAgentConfig.tools with
                    readFile =
                        fun path ->
                            Assert.Equal("/definitely/does/not/exist.txt", path)
                            Error "Error: File '/definitely/does/not/exist.txt' not found." } }

    let result = AgentToolCall.executeToolCall customConfig toolCall

    match result with
    | Error _ -> ()
    | Ok _ -> Assert.Fail "Expected Error for missing file"

[<Fact>]
let ``executeToolCall write_file writes content successfully`` () =
    let toolCall: LlmClient.ToolCall =
        { id = "call_2"
          ``type`` = "function"
          ``function`` =
            { name = "write_file"
              arguments = "{\"file_path\": \"test.txt\", \"content\": \"written by test\"}" } }

    let mutable called = false

    let customConfig =
        { mockAgentConfig with
            tools =
                { mockAgentConfig.tools with
                    writeFile =
                        fun path content ->
                            Assert.Equal("test.txt", path)
                            Assert.Equal("written by test", content)
                            called <- true
                            Ok "Successfully wrote to 'test.txt'." } }

    let result = AgentToolCall.executeToolCall customConfig toolCall

    match result with
    | Ok _ -> Assert.True(called)
    | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)

[<Fact>]
let ``executeToolCall run_command returns command output`` () =
    let toolCall: LlmClient.ToolCall =
        { id = "call_3"
          ``type`` = "function"
          ``function`` =
            { name = "run_command"
              arguments = "{\"command_line\": \"echo hello from agent test\"}" } }

    let customConfig =
        { mockAgentConfig with
            tools =
                { mockAgentConfig.tools with
                    runCommand =
                        fun cmd cwd ->
                            Assert.Equal("echo hello from agent test", cmd)
                            Assert.Equal("", cwd)
                            Ok "hello from agent test" } }

    let result = AgentToolCall.executeToolCall customConfig toolCall

    match result with
    | Ok output -> Assert.Contains("hello from agent test", output)
    | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)

[<Fact>]
let ``executeToolCall run_command with cwd argument succeeds`` () =
    let toolCall: LlmClient.ToolCall =
        { id = "call_4"
          ``type`` = "function"
          ``function`` =
            { name = "run_command"
              arguments = "{\"command_line\": \"echo in temp\", \"cwd\": \"/tmp\"}" } }

    let customConfig =
        { mockAgentConfig with
            tools =
                { mockAgentConfig.tools with
                    runCommand =
                        fun cmd cwd ->
                            Assert.Equal("echo in temp", cmd)
                            Assert.Equal("/tmp", cwd)
                            Ok "in temp" } }

    let result = AgentToolCall.executeToolCall customConfig toolCall

    match result with
    | Ok output -> Assert.Contains("in temp", output)
    | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)

[<Fact>]
let ``executeToolCall list_directory returns directory listing`` () =
    let toolCall: LlmClient.ToolCall =
        { id = "call_5"
          ``type`` = "function"
          ``function`` =
            { name = "list_directory"
              arguments = "{\"directory_path\": \"/tmp\"}" } }

    let customConfig =
        { mockAgentConfig with
            tools =
                { mockAgentConfig.tools with
                    listDirectory =
                        fun path ->
                            Assert.Equal("/tmp", path)
                            Ok "Contents of directory '/tmp':\ntest.txt" } }

    let result = AgentToolCall.executeToolCall customConfig toolCall

    match result with
    | Ok output -> Assert.Contains("test.txt", output)
    | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)

[<Fact>]
let ``executeToolCall list_directory without directoryPath argument uses empty string`` () =
    let toolCall: LlmClient.ToolCall =
        { id = "call_6"
          ``type`` = "function"
          ``function`` =
            { name = "list_directory"
              arguments = "{}" } }

    let customConfig =
        { mockAgentConfig with
            tools =
                { mockAgentConfig.tools with
                    listDirectory =
                        fun path ->
                            Assert.Equal("", path)
                            Ok "Contents of directory '':\ntest.txt" } }

    let result = AgentToolCall.executeToolCall customConfig toolCall
    Assert.NotNull(result :> obj)

[<Fact>]
let ``executeToolCall grep_search returns query matches`` () =
    let toolCall: LlmClient.ToolCall =
        { id = "call_grep"
          ``type`` = "function"
          ``function`` =
            { name = "grep_search"
              arguments = "{\"query\": \"hello\", \"directory_path\": \"/src\"}" } }

    let customConfig =
        { mockAgentConfig with
            tools =
                { mockAgentConfig.tools with
                    grepSearch =
                        fun query path ->
                            Assert.Equal("hello", query)
                            Assert.Equal("/src", path)
                            Ok "Found matches for 'hello' in '/src':\nfoo.txt:1: hello" } }

    let result = AgentToolCall.executeToolCall customConfig toolCall

    match result with
    | Ok output -> Assert.Contains("foo.txt", output)
    | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)

[<Fact>]
let ``executeToolCall grep_search uses empty string when directory_path is omitted`` () =
    let toolCall: LlmClient.ToolCall =
        { id = "call_grep_nodir"
          ``type`` = "function"
          ``function`` =
            { name = "grep_search"
              arguments = "{\"query\": \"hello\"}" } }

    let mutable capturedPath = "NOT_SET"

    let config =
        { mockAgentConfig with
            tools =
                { mockAgentConfig.tools with
                    grepSearch =
                        fun query path ->
                            capturedPath <- path
                            Ok(sprintf "Matches for '%s' in '%s'" query path) } }

    let result = AgentToolCall.executeToolCall config toolCall

    match result with
    | Ok _ -> Assert.Equal("", capturedPath)
    | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)

[<Fact>]
let ``executeToolCall patch_file patches file content successfully`` () =
    let toolCall: LlmClient.ToolCall =
        { id = "call_patch"
          ``type`` = "function"
          ``function`` =
            { name = "patch_file"
              arguments = "{\"file_path\": \"test.txt\", \"target\": \"old\", \"replacement\": \"new\"}" } }

    let customConfig =
        { mockAgentConfig with
            tools =
                { mockAgentConfig.tools with
                    patchFile =
                        fun path target replacement ->
                            Assert.Equal("test.txt", path)
                            Assert.Equal("old", target)
                            Assert.Equal("new", replacement)
                            Ok "Successfully patched file 'test.txt'." } }

    let result = AgentToolCall.executeToolCall customConfig toolCall

    match result with
    | Ok output -> Assert.Contains("Successfully patched", output)
    | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)

[<Fact>]
let ``executeToolCall read_file_lines reads file lines successfully`` () =
    let toolCall: LlmClient.ToolCall =
        { id = "call_lines"
          ``type`` = "function"
          ``function`` =
            { name = "read_file_lines"
              arguments = "{\"file_path\": \"test.txt\", \"start_line\": 10, \"end_line\": 20}" } }

    let customConfig =
        { mockAgentConfig with
            tools =
                { mockAgentConfig.tools with
                    readFileLines =
                        fun path start end_ ->
                            Assert.Equal("test.txt", path)
                            Assert.Equal(10, start)
                            Assert.Equal(20, end_)
                            Ok "lines 10 to 20" } }

    let result = AgentToolCall.executeToolCall customConfig toolCall

    match result with
    | Ok output -> Assert.Equal("lines 10 to 20", output)
    | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)

[<Fact>]
let ``executeToolCall find_files searches files successfully`` () =
    let toolCall: LlmClient.ToolCall =
        { id = "call_find"
          ``type`` = "function"
          ``function`` =
            { name = "find_files"
              arguments = "{\"pattern\": \"*.fs\", \"directory_path\": \"/src\"}" } }

    let customConfig =
        { mockAgentConfig with
            tools =
                { mockAgentConfig.tools with
                    findFiles =
                        fun pattern path ->
                            Assert.Equal("*.fs", pattern)
                            Assert.Equal("/src", path)
                            Ok "file1.fs\nfile2.fs" } }

    let result = AgentToolCall.executeToolCall customConfig toolCall

    match result with
    | Ok output -> Assert.Equal("file1.fs\nfile2.fs", output)
    | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)

[<Fact>]
let ``executeToolCall find_files uses empty string when directory_path is omitted`` () =
    let toolCall: LlmClient.ToolCall =
        { id = "call_find_nodir"
          ``type`` = "function"
          ``function`` =
            { name = "find_files"
              arguments = "{\"pattern\": \"*.fs\"}" } }

    let mutable capturedPath = "NOT_SET"

    let config =
        { mockAgentConfig with
            tools =
                { mockAgentConfig.tools with
                    findFiles =
                        fun pattern path ->
                            capturedPath <- path
                            Ok(sprintf "Found '%s' in '%s'" pattern path) } }

    let result = AgentToolCall.executeToolCall config toolCall

    match result with
    | Ok _ -> Assert.Equal("", capturedPath)
    | Error err -> Assert.Fail(sprintf "Expected Ok, got Error: %s" err)

[<Fact>]
let ``executeToolCall returns Error when user cancels confirmation`` () =
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

    let result = AgentToolCall.executeToolCall config toolCall

    match result with
    | Error errMsg ->
        Assert.Contains("cancelled", errMsg)
        Assert.Contains("read_file", cancelMsg)
    | Ok _ -> Assert.Fail "Expected Error when tool call is cancelled"

[<Fact>]
let ``executeToolCall returns Error for unknown function name`` () =
    let toolCall: LlmClient.ToolCall =
        { id = "call_unknown"
          ``type`` = "function"
          ``function`` =
            { name = "nonexistent_tool"
              arguments = "{}" } }

    let result = AgentToolCall.executeToolCall mockAgentConfig toolCall

    match result with
    | Error errMsg -> Assert.Contains("nonexistent_tool", errMsg)
    | Ok _ -> Assert.Fail "Expected Error for unknown tool"

[<Fact>]
let ``executeToolCall returns Error on invalid JSON arguments`` () =
    let toolCall: LlmClient.ToolCall =
        { id = "call_bad"
          ``type`` = "function"
          ``function`` =
            { name = "read_file"
              arguments = "NOT VALID JSON" } }

    let result = AgentToolCall.executeToolCall mockAgentConfig toolCall

    match result with
    | Error errMsg -> Assert.Contains("read_file", errMsg)
    | Ok _ -> Assert.Fail "Expected Error for invalid JSON"
