namespace CodingAgent

module AgentToolCall =
    type ToolName =
        | ReadFile
        | WriteFile
        | RunCommand
        | ListDirectory
        | GrepSearch
        | PatchFile
        | ReadFileLines
        | FindFiles

    module ToolName =
        let toolNameToString =
            [| ReadFile, "read_file"
               WriteFile, "write_file"
               RunCommand, "run_command"
               ListDirectory, "list_directory"
               GrepSearch, "grep_search"
               PatchFile, "patch_file"
               ReadFileLines, "read_file_lines"
               FindFiles, "find_files" |]
            |> Map.ofArray

        let toString name = Map.find name toolNameToString

        let stringToToolName =
            [| "read_file", ReadFile
               "write_file", WriteFile
               "run_command", RunCommand
               "list_directory", ListDirectory
               "grep_search", GrepSearch
               "patch_file", PatchFile
               "read_file_lines", ReadFileLines
               "find_files", FindFiles |]
            |> Map.ofArray

        let fromString s = Map.tryFind s stringToToolName

    module AsyncResult =
        let ofResult (r: Result<'T, string>) = async { return r }

        let bind (f: 'T -> Async<Result<'U, string>>) (x: Async<Result<'T, string>>) =
            async {
                match! x with
                | Ok v -> return! f v
                | Error e -> return Error e
            }

        let map (f: 'T -> 'U) (x: Async<Result<'T, string>>) =
            async {
                match! x with
                | Ok v -> return Ok(f v)
                | Error e -> return Error e
            }

    type ToolRegistration =
        { toolName: ToolName
          definition: LlmClient.ToolDef
          handler: AgentConfig -> System.Text.Json.JsonElement -> Async<Result<string, string>>
          readOnly: bool }

    let tryGetStringProperty (json: System.Text.Json.JsonElement) (propertyName: string) =
        match json.TryGetProperty propertyName with
        | true, el -> el.GetString() |> Option.ofObj
        | _ -> None

    let getRequiredStringProperty (root: System.Text.Json.JsonElement) (name: string) =
        match root.TryGetProperty name with
        | true, el when el.ValueKind = System.Text.Json.JsonValueKind.String -> el.GetString() |> Ok
        | true, _ -> sprintf "Property '%s' must be a string." name |> Error
        | false, _ -> sprintf "Missing required property '%s' in tool arguments." name |> Error

    let getRequiredInt32Property (root: System.Text.Json.JsonElement) (name: string) =
        match root.TryGetProperty name with
        | true, el when el.ValueKind = System.Text.Json.JsonValueKind.Number -> el.GetInt32() |> Ok
        | true, _ -> sprintf "Property '%s' must be an integer." name |> Error
        | false, _ -> sprintf "Missing required property '%s' in tool arguments." name |> Error

    let handleReadFile config (root: System.Text.Json.JsonElement) =
        match getRequiredStringProperty root "file_path" with
        | Ok filePath ->
            sprintf "🛠️  [Tool] Executing read_file: %s" filePath
            |> config.interactive.writeLine

            async { return config.tools.readFile filePath }
        | Error err -> async { return Error err }

    let handleWriteFile config (root: System.Text.Json.JsonElement) =
        getRequiredStringProperty root "file_path"
        |> AsyncResult.ofResult
        |> AsyncResult.bind (fun filePath ->
            getRequiredStringProperty root "content"
            |> AsyncResult.ofResult
            |> AsyncResult.map (fun content -> filePath, content))
        |> AsyncResult.bind (fun (filePath, content) ->
            sprintf "🛠️  [Tool] Executing write_file: %s" filePath
            |> config.interactive.writeLine

            async { return config.tools.writeFile filePath content })

    let handleRunCommand config (root: System.Text.Json.JsonElement) =
        async {
            match getRequiredStringProperty root "command_line" with
            | Ok commandLine ->
                let cwd = tryGetStringProperty root "cwd" |> Option.defaultValue ""

                sprintf "🛠️  [Tool] Executing run_command: %s (cwd: %s)" commandLine cwd
                |> config.interactive.writeLine

                return! config.tools.runCommand commandLine cwd
            | Error err -> return Error err
        }

    let handleListDirectory config (root: System.Text.Json.JsonElement) =
        let directoryPath =
            tryGetStringProperty root "directory_path" |> Option.defaultValue ""

        sprintf "🛠️  [Tool] Executing list_directory: %s" directoryPath
        |> config.interactive.writeLine

        async { return config.tools.listDirectory directoryPath }

    let handleGrepSearch config (root: System.Text.Json.JsonElement) =
        match getRequiredStringProperty root "query" with
        | Ok query ->
            let directoryPath =
                tryGetStringProperty root "directory_path" |> Option.defaultValue ""

            sprintf "🛠️  [Tool] Executing grep_search: '%s' in %s" query directoryPath
            |> config.interactive.writeLine

            async { return config.tools.grepSearch query directoryPath }
        | Error err -> async { return Error err }

    let handlePatchFile config (root: System.Text.Json.JsonElement) =
        getRequiredStringProperty root "file_path"
        |> AsyncResult.ofResult
        |> AsyncResult.bind (fun filePath ->
            getRequiredStringProperty root "target"
            |> AsyncResult.ofResult
            |> AsyncResult.bind (fun target ->
                getRequiredStringProperty root "replacement"
                |> AsyncResult.ofResult
                |> AsyncResult.map (fun replacement -> filePath, target, replacement)))
        |> AsyncResult.bind (fun (filePath, target, replacement) ->
            sprintf "🛠️  [Tool] Executing patch_file: %s" filePath
            |> config.interactive.writeLine

            async { return config.tools.patchFile filePath target replacement })

    let handleReadFileLines config (root: System.Text.Json.JsonElement) =
        getRequiredStringProperty root "file_path"
        |> AsyncResult.ofResult
        |> AsyncResult.bind (fun filePath ->
            getRequiredInt32Property root "start_line"
            |> AsyncResult.ofResult
            |> AsyncResult.bind (fun startLine ->
                getRequiredInt32Property root "end_line"
                |> AsyncResult.ofResult
                |> AsyncResult.map (fun endLine -> filePath, startLine, endLine)))
        |> AsyncResult.bind (fun (filePath, startLine, endLine) ->
            sprintf "🛠️  [Tool] Executing read_file_lines: %s (lines %d-%d)" filePath startLine endLine
            |> config.interactive.writeLine

            async { return config.tools.readFileLines filePath startLine endLine })

    let handleFindFiles config (root: System.Text.Json.JsonElement) =
        match getRequiredStringProperty root "pattern" with
        | Ok pattern ->
            let directoryPath =
                tryGetStringProperty root "directory_path" |> Option.defaultValue ""

            sprintf "🛠️  [Tool] Executing find_files: '%s' in %s" pattern directoryPath
            |> config.interactive.writeLine

            async { return config.tools.findFiles pattern directoryPath }
        | Error err -> async { return Error err }

    let readFileReg =
        { toolName = ReadFile
          definition =
            { ``type`` = "function"
              ``function`` =
                { name = ToolName.toString ReadFile
                  description = "Reads the content of a file."
                  parameters =
                    {| ``type`` = "object"
                       properties =
                        {| file_path =
                            {| ``type`` = "string"
                               description = "The path to the file to read." |} |}
                       required = [| "file_path" |] |} } }
          handler = handleReadFile
          readOnly = true }

    let writeFileReg =
        { toolName = WriteFile
          definition =
            { ``type`` = "function"
              ``function`` =
                { name = ToolName.toString WriteFile
                  description = "Writes content to a file. Overwrites if it exists."
                  parameters =
                    {| ``type`` = "object"
                       properties =
                        {| file_path =
                            {| ``type`` = "string"
                               description = "The path to the file to write." |}
                           content =
                            {| ``type`` = "string"
                               description = "The content to write to the file." |} |}
                       required = [| "file_path"; "content" |] |} } }
          handler = handleWriteFile
          readOnly = false }

    let runCommandReg =
        { toolName = RunCommand
          definition =
            { ``type`` = "function"
              ``function`` =
                { name = ToolName.toString RunCommand
                  description = "Executes a shell command."
                  parameters =
                    {| ``type`` = "object"
                       properties =
                        {| command_line =
                            {| ``type`` = "string"
                               description = "The bash shell command to execute." |}
                           cwd =
                            {| ``type`` = "string"
                               description = "The current working directory (optional)." |} |}
                       required = [| "command_line" |] |} } }
          handler = handleRunCommand
          readOnly = false }

    let listDirectoryReg =
        { toolName = ListDirectory
          definition =
            { ``type`` = "function"
              ``function`` =
                { name = ToolName.toString ListDirectory
                  description = "Lists files and subdirectories within a directory."
                  parameters =
                    {| ``type`` = "object"
                       properties =
                        {| directory_path =
                            {| ``type`` = "string"
                               description =
                                "The path to the directory to list (optional, defaults to current directory)." |} |}
                       required = [||] |} } }
          handler = handleListDirectory
          readOnly = true }

    let grepSearchReg =
        { toolName = GrepSearch
          definition =
            { ``type`` = "function"
              ``function`` =
                { name = ToolName.toString GrepSearch
                  description = "Searches for a specific query string within text files recursively."
                  parameters =
                    {| ``type`` = "object"
                       properties =
                        {| query =
                            {| ``type`` = "string"
                               description = "The text pattern/query to search for." |}
                           directory_path =
                            {| ``type`` = "string"
                               description =
                                "The path to the directory to search (optional, defaults to current directory)." |} |}
                       required = [| "query" |] |} } }
          handler = handleGrepSearch
          readOnly = true }

    let patchFileReg =
        { toolName = PatchFile
          definition =
            { ``type`` = "function"
              ``function`` =
                { name = ToolName.toString PatchFile
                  description = "Replaces a specific target string block inside a file with a replacement string block."
                  parameters =
                    {| ``type`` = "object"
                       properties =
                        {| file_path =
                            {| ``type`` = "string"
                               description = "The path to the file to patch." |}
                           target =
                            {| ``type`` = "string"
                               description = "The exact string block inside the file to search for and replace." |}
                           replacement =
                            {| ``type`` = "string"
                               description = "The new string block to replace the target block with." |} |}
                       required = [| "file_path"; "target"; "replacement" |] |} } }
          handler = handlePatchFile
          readOnly = false }

    let readFileLinesReg =
        { toolName = ReadFileLines
          definition =
            { ``type`` = "function"
              ``function`` =
                { name = ToolName.toString ReadFileLines
                  description = "Reads a specific line range from a file (1-indexed)."
                  parameters =
                    {| ``type`` = "object"
                       properties =
                        {| file_path =
                            {| ``type`` = "string"
                               description = "The path to the file to read." |}
                           start_line =
                            {| ``type`` = "integer"
                               description = "The starting line number to read (inclusive, 1-indexed)." |}
                           end_line =
                            {| ``type`` = "integer"
                               description = "The ending line number to read (inclusive, 1-indexed)." |} |}
                       required = [| "file_path"; "start_line"; "end_line" |] |} } }
          handler = handleReadFileLines
          readOnly = true }

    let findFilesReg =
        { toolName = FindFiles
          definition =
            { ``type`` = "function"
              ``function`` =
                { name = ToolName.toString FindFiles
                  description = "Recursively searches for files under a directory matching a pattern."
                  parameters =
                    {| ``type`` = "object"
                       properties =
                        {| pattern =
                            {| ``type`` = "string"
                               description = "The search pattern (e.g. '*.fs' or '*Agent*')." |}
                           directory_path =
                            {| ``type`` = "string"
                               description =
                                "The directory path to search in (optional, defaults to current directory)." |} |}
                       required = [| "pattern" |] |} } }
          handler = handleFindFiles
          readOnly = true }

    let toolRegistrations: ToolRegistration array =
        [| readFileReg
           writeFileReg
           runCommandReg
           listDirectoryReg
           grepSearchReg
           patchFileReg
           readFileLinesReg
           findFilesReg |]

    let toolsDefinition () : LlmClient.ToolDef array =
        toolRegistrations |> Array.map (fun r -> r.definition)

    let readOnlyTools =
        toolRegistrations
        |> Array.filter (fun r -> r.readOnly)
        |> Array.map (fun r -> r.toolName)
        |> Set.ofArray

    let isReadOnlyTool (toolCall: LlmClient.ToolCall) =
        match ToolName.fromString toolCall.``function``.name with
        | Some name -> Set.contains name readOnlyTools
        | None -> false

    let promptToolConfirmation interactive (toolCall: LlmClient.ToolCall) =
        sprintf
            "\n❓ [Confirm] Execute tool '%s' with arguments: %s? (y/N): "
            toolCall.``function``.name
            toolCall.``function``.arguments
        |> interactive.write

        let response = interactive.readLine ()

        not (System.String.IsNullOrWhiteSpace response)
        && response.Trim().ToLower() = "y"

    let confirmToolCall interactive runtimeConfig (toolCall: LlmClient.ToolCall) =
        match runtimeConfig.autoConfirm with
        | All ->
            sprintf "🟢 [Auto-confirm] Executing tool '%s' (auto-confirm all)." toolCall.``function``.name
            |> interactive.writeLine

            true
        | ReadsOnly when isReadOnlyTool toolCall ->
            sprintf "🟢 [Auto-confirm] Executing read tool '%s' (auto-confirm reads)." toolCall.``function``.name
            |> interactive.writeLine

            true
        | _ -> promptToolConfirmation interactive toolCall

    let toolHandlers: Map<string, AgentConfig -> System.Text.Json.JsonElement -> Async<Result<string, string>>> =
        toolRegistrations
        |> Array.map (fun r -> ToolName.toString r.toolName, r.handler)
        |> Map.ofArray

    let executeToolCall config (toolCall: LlmClient.ToolCall) =
        async {
            if not (config.interactive.confirmToolCall config.interactive config.runtimeConfig toolCall) then
                sprintf "⚠️  [Tool] Execution of '%s' cancelled by user." toolCall.``function``.name
                |> config.interactive.writeLine

                return Error "Tool execution cancelled by user."
            else
                try
                    use jsonDoc = System.Text.Json.JsonDocument.Parse toolCall.``function``.arguments
                    let root = jsonDoc.RootElement
                    let toolName = toolCall.``function``.name

                    match toolHandlers |> Map.tryFind toolName with
                    | Some handler -> return! handler config root
                    | None -> return sprintf "Unknown function '%s'." toolName |> Error
                with ex ->
                    return
                        sprintf "Failed to parse arguments for tool '%s': %s" toolCall.``function``.name ex.Message
                        |> Error
        }
