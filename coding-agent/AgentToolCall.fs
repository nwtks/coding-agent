namespace CodingAgent

module AgentToolCall =
    type ToolRegistration =
        { definition: LlmClient.ToolDef
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
            sprintf "🛠️  [Tool] Executing read_file: %s" filePath |> config.writeLine
            async { return config.tools.readFile filePath }
        | Error err -> async { return Error err }

    let handleWriteFile config (root: System.Text.Json.JsonElement) =
        async {
            match getRequiredStringProperty root "file_path" with
            | Ok filePath ->
                match getRequiredStringProperty root "content" with
                | Ok content ->
                    sprintf "🛠️  [Tool] Executing write_file: %s" filePath |> config.writeLine
                    return config.tools.writeFile filePath content
                | Error err -> return Error err
            | Error err -> return Error err
        }

    let handleRunCommand config (root: System.Text.Json.JsonElement) =
        async {
            match getRequiredStringProperty root "command_line" with
            | Ok commandLine ->
                let cwd = tryGetStringProperty root "cwd" |> Option.defaultValue ""

                sprintf "🛠️  [Tool] Executing run_command: %s (cwd: %s)" commandLine cwd
                |> config.writeLine

                return! config.tools.runCommand commandLine cwd
            | Error err -> return Error err
        }

    let handleListDirectory config (root: System.Text.Json.JsonElement) =
        let directoryPath =
            tryGetStringProperty root "directory_path" |> Option.defaultValue ""

        sprintf "🛠️  [Tool] Executing list_directory: %s" directoryPath
        |> config.writeLine

        async { return config.tools.listDirectory directoryPath }

    let handleGrepSearch config (root: System.Text.Json.JsonElement) =
        match getRequiredStringProperty root "query" with
        | Ok query ->
            let directoryPath =
                tryGetStringProperty root "directory_path" |> Option.defaultValue ""

            sprintf "🛠️  [Tool] Executing grep_search: '%s' in %s" query directoryPath
            |> config.writeLine

            async { return config.tools.grepSearch query directoryPath }
        | Error err -> async { return Error err }

    let handlePatchFile config (root: System.Text.Json.JsonElement) =
        async {
            match getRequiredStringProperty root "file_path" with
            | Ok filePath ->
                match getRequiredStringProperty root "target" with
                | Ok target ->
                    match getRequiredStringProperty root "replacement" with
                    | Ok replacement ->
                        sprintf "🛠️  [Tool] Executing patch_file: %s" filePath |> config.writeLine
                        return config.tools.patchFile filePath target replacement
                    | Error err -> return Error err
                | Error err -> return Error err
            | Error err -> return Error err
        }

    let handleReadFileLines config (root: System.Text.Json.JsonElement) =
        async {
            match getRequiredStringProperty root "file_path" with
            | Ok filePath ->
                match getRequiredInt32Property root "start_line" with
                | Ok startLine ->
                    match getRequiredInt32Property root "end_line" with
                    | Ok endLine ->
                        sprintf "🛠️  [Tool] Executing read_file_lines: %s (lines %d-%d)" filePath startLine endLine
                        |> config.writeLine

                        return config.tools.readFileLines filePath startLine endLine
                    | Error err -> return Error err
                | Error err -> return Error err
            | Error err -> return Error err
        }

    let handleFindFiles config (root: System.Text.Json.JsonElement) =
        match getRequiredStringProperty root "pattern" with
        | Ok pattern ->
            let directoryPath =
                tryGetStringProperty root "directory_path" |> Option.defaultValue ""

            sprintf "🛠️  [Tool] Executing find_files: '%s' in %s" pattern directoryPath
            |> config.writeLine

            async { return config.tools.findFiles pattern directoryPath }
        | Error err -> async { return Error err }

    let readFileReg =
        { definition =
            { ``type`` = "function"
              ``function`` =
                { name = "read_file"
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
        { definition =
            { ``type`` = "function"
              ``function`` =
                { name = "write_file"
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
        { definition =
            { ``type`` = "function"
              ``function`` =
                { name = "run_command"
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
        { definition =
            { ``type`` = "function"
              ``function`` =
                { name = "list_directory"
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
        { definition =
            { ``type`` = "function"
              ``function`` =
                { name = "grep_search"
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
        { definition =
            { ``type`` = "function"
              ``function`` =
                { name = "patch_file"
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
        { definition =
            { ``type`` = "function"
              ``function`` =
                { name = "read_file_lines"
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
        { definition =
            { ``type`` = "function"
              ``function`` =
                { name = "find_files"
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

    let isReadOnlyTool (toolCall: LlmClient.ToolCall) =
        toolRegistrations
        |> Array.filter (fun r -> r.readOnly)
        |> Array.map (fun r -> r.definition.``function``.name)
        |> set
        |> fun s -> s.Contains toolCall.``function``.name

    let confirmToolCall config (toolCall: LlmClient.ToolCall) =
        match config.autoConfirm with
        | All ->
            sprintf "🟢 [Auto-confirm] Executing tool '%s' (auto-confirm all)." toolCall.``function``.name
            |> config.writeLine

            true
        | ReadsOnly when isReadOnlyTool toolCall ->
            sprintf "🟢 [Auto-confirm] Executing read tool '%s' (auto-confirm reads)." toolCall.``function``.name
            |> config.writeLine

            true
        | _ ->
            sprintf
                "\n❓ [Confirm] Execute tool '%s' with arguments: %s? (y/N): "
                toolCall.``function``.name
                toolCall.``function``.arguments
            |> config.write

            let response = config.readLine ()

            not (System.String.IsNullOrWhiteSpace response)
            && response.Trim().ToLower() = "y"

    let toolHandlers: Map<string, AgentConfig -> System.Text.Json.JsonElement -> Async<Result<string, string>>> =
        toolRegistrations
        |> Array.map (fun r -> r.definition.``function``.name, r.handler)
        |> Map.ofArray

    let executeToolCall config (toolCall: LlmClient.ToolCall) =
        async {
            if not (config.confirmToolCall config toolCall) then
                sprintf "⚠️  [Tool] Execution of '%s' cancelled by user." toolCall.``function``.name
                |> config.writeLine

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
