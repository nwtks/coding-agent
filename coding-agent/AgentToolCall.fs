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
        | MoveFile
        | CreateDirectory
        | DeleteFile

    module ToolName =
        let pairs =
            [| ReadFile, "read_file"
               WriteFile, "write_file"
               RunCommand, "run_command"
               ListDirectory, "list_directory"
               GrepSearch, "grep_search"
               PatchFile, "patch_file"
               ReadFileLines, "read_file_lines"
               FindFiles, "find_files"
               MoveFile, "move_file"
               CreateDirectory, "create_directory"
               DeleteFile, "delete_file" |]

        let toolNameToString = pairs |> Map.ofArray

        let stringToToolName = pairs |> Array.map (fun (k, v) -> v, k) |> Map.ofArray

        let toString name = Map.find name toolNameToString
        let fromString s = Map.tryFind s stringToToolName

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
        | true, _ -> $"Property '{name}' must be a string." |> Error
        | false, _ -> $"Missing required property '{name}' in tool arguments." |> Error

    let getRequiredInt32Property (root: System.Text.Json.JsonElement) (name: string) =
        match root.TryGetProperty name with
        | true, el when el.ValueKind = System.Text.Json.JsonValueKind.Number -> el.GetInt32() |> Ok
        | true, _ -> $"Property '{name}' must be an integer." |> Error
        | false, _ -> $"Missing required property '{name}' in tool arguments." |> Error

    let handleReadFile config (root: System.Text.Json.JsonElement) =
        match getRequiredStringProperty root "file_path" with
        | Ok filePath ->
            $"🛠️  [Tool] Executing read_file: {filePath}" |> config.interactive.writeLine
            async { return config.tools.readFile filePath }
        | Error err -> async { return Error err }

    let handleWriteFile config (root: System.Text.Json.JsonElement) =
        async {
            let parsed =
                getRequiredStringProperty root "file_path"
                |> Result.bind (fun filePath ->
                    getRequiredStringProperty root "content"
                    |> Result.map (fun content -> filePath, content))

            match parsed with
            | Ok(filePath, content) ->
                $"🛠️  [Tool] Executing write_file: {filePath}" |> config.interactive.writeLine
                return config.tools.writeFile filePath content
            | Error err -> return Error err
        }

    let handleRunCommand config (root: System.Text.Json.JsonElement) =
        async {
            match getRequiredStringProperty root "command_line" with
            | Ok commandLine ->
                let cwd = tryGetStringProperty root "cwd" |> Option.defaultValue ""

                $"🛠️  [Tool] Executing run_command: {commandLine} (cwd: {cwd})"
                |> config.interactive.writeLine

                return! config.tools.runCommand commandLine cwd
            | Error err -> return Error err
        }

    let handleListDirectory config (root: System.Text.Json.JsonElement) =
        let directoryPath =
            tryGetStringProperty root "directory_path" |> Option.defaultValue ""

        $"🛠️  [Tool] Executing list_directory: {directoryPath}"
        |> config.interactive.writeLine

        async { return config.tools.listDirectory directoryPath }

    let handleGrepSearch config (root: System.Text.Json.JsonElement) =
        match getRequiredStringProperty root "query" with
        | Ok query ->
            let directoryPath =
                tryGetStringProperty root "directory_path" |> Option.defaultValue ""

            let isRegex =
                match root.TryGetProperty "is_regex" with
                | true, el when el.ValueKind = System.Text.Json.JsonValueKind.True -> true
                | _ -> false

            let ignoreCase =
                match root.TryGetProperty "ignore_case" with
                | true, el when el.ValueKind = System.Text.Json.JsonValueKind.False -> false
                | _ -> true

            $"🛠️  [Tool] Executing grep_search: '{query}' in {directoryPath} (regex: {isRegex}, ignore_case: {ignoreCase})"
            |> config.interactive.writeLine

            async { return config.tools.grepSearch query isRegex ignoreCase directoryPath }
        | Error err -> async { return Error err }

    let handlePatchFile config (root: System.Text.Json.JsonElement) =
        async {
            let parsed =
                getRequiredStringProperty root "file_path"
                |> Result.bind (fun filePath ->
                    getRequiredStringProperty root "target"
                    |> Result.bind (fun target ->
                        getRequiredStringProperty root "replacement"
                        |> Result.map (fun replacement -> filePath, target, replacement)))

            match parsed with
            | Ok(filePath, target, replacement) ->
                $"🛠️  [Tool] Executing patch_file: {filePath}" |> config.interactive.writeLine
                return config.tools.patchFile filePath target replacement
            | Error err -> return Error err
        }

    let handleReadFileLines config (root: System.Text.Json.JsonElement) =
        async {
            let parsed =
                getRequiredStringProperty root "file_path"
                |> Result.bind (fun filePath ->
                    getRequiredInt32Property root "start_line"
                    |> Result.bind (fun startLine ->
                        getRequiredInt32Property root "end_line"
                        |> Result.map (fun endLine -> filePath, startLine, endLine)))

            match parsed with
            | Ok(filePath, startLine, endLine) ->
                $"🛠️  [Tool] Executing read_file_lines: {filePath} (lines {startLine}-{endLine})"
                |> config.interactive.writeLine

                return config.tools.readFileLines filePath startLine endLine
            | Error err -> return Error err
        }

    let handleFindFiles config (root: System.Text.Json.JsonElement) =
        match getRequiredStringProperty root "pattern" with
        | Ok pattern ->
            let directoryPath =
                tryGetStringProperty root "directory_path" |> Option.defaultValue ""

            $"🛠️  [Tool] Executing find_files: '{pattern}' in {directoryPath}"
            |> config.interactive.writeLine

            async { return config.tools.findFiles pattern directoryPath }
        | Error err -> async { return Error err }

    let handleMoveFile config (root: System.Text.Json.JsonElement) =
        async {
            let parsed =
                getRequiredStringProperty root "source"
                |> Result.bind (fun source ->
                    getRequiredStringProperty root "destination"
                    |> Result.map (fun destination -> source, destination))

            match parsed with
            | Ok(source, destination) ->
                let overwrite =
                    match root.TryGetProperty "overwrite" with
                    | true, el when el.ValueKind = System.Text.Json.JsonValueKind.True -> true
                    | _ -> false

                $"🛠️  [Tool] Executing move_file: '{source}' -> '{destination}' (overwrite: {overwrite})"
                |> config.interactive.writeLine

                return config.tools.moveFile source destination overwrite
            | Error err -> return Error err
        }

    let handleCreateDirectory config (root: System.Text.Json.JsonElement) =
        async {
            match getRequiredStringProperty root "path" with
            | Error err -> return Error err
            | Ok path ->
                let existOk =
                    match root.TryGetProperty "exist_ok" with
                    | true, el when el.ValueKind = System.Text.Json.JsonValueKind.True -> true
                    | _ -> false

                $"🛠️  [Tool] Executing create_directory: '{path}' (exist_ok: {existOk})"
                |> config.interactive.writeLine

                return config.tools.createDirectory path existOk
        }

    let handleDeleteFile config (root: System.Text.Json.JsonElement) =
        match getRequiredStringProperty root "file_path" with
        | Ok filePath ->
            $"🛠️  [Tool] Executing delete_file: '{filePath}'"
            |> config.interactive.writeLine

            async { return config.tools.deleteFile filePath }
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
                  description =
                    "Searches for text within files recursively. Supports plain text (default) or regular expression matching."
                  parameters =
                    {| ``type`` = "object"
                       properties =
                        {| query =
                            {| ``type`` = "string"
                               description = "The text pattern/query to search for (plain text by default)." |}
                           directory_path =
                            {| ``type`` = "string"
                               description =
                                "The path to the directory to search (optional, defaults to current directory)." |}
                           is_regex =
                            {| ``type`` = "boolean"
                               description =
                                "If true, treat the query as a regular expression (optional, defaults to false)." |}
                           ignore_case =
                            {| ``type`` = "boolean"
                               description =
                                "If true, ignore case when matching (optional, defaults to true). Set to false for case-sensitive search." |} |}
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

    let moveFileReg =
        { toolName = MoveFile
          definition =
            { ``type`` = "function"
              ``function`` =
                { name = ToolName.toString MoveFile
                  description =
                    "Moves a file from one location to another within the workspace. Optionally overwrites the destination if it exists."
                  parameters =
                    {| ``type`` = "object"
                       properties =
                        {| source =
                            {| ``type`` = "string"
                               description = "The path to the source file to move." |}
                           destination =
                            {| ``type`` = "string"
                               description = "The destination path to move the file to." |}
                           overwrite =
                            {| ``type`` = "boolean"
                               description =
                                "Whether to overwrite the destination if it already exists (optional, defaults to false)." |} |}
                       required = [| "source"; "destination" |] |} } }
          handler = handleMoveFile
          readOnly = false }

    let createDirectoryReg =
        { toolName = CreateDirectory
          definition =
            { ``type`` = "function"
              ``function`` =
                { name = ToolName.toString CreateDirectory
                  description =
                    "Creates a directory (and any missing parent directories) at the specified path. By default (exist_ok=false), returns an error if the directory already exists."
                  parameters =
                    {| ``type`` = "object"
                       properties =
                        {| path =
                            {| ``type`` = "string"
                               description = "The path of the directory to create." |}
                           exist_ok =
                            {| ``type`` = "boolean"
                               description =
                                "If true, do not error when the directory already exists (optional, defaults to false)." |} |}
                       required = [| "path" |] |} } }
          handler = handleCreateDirectory
          readOnly = false }

    let deleteFileReg =
        { toolName = DeleteFile
          definition =
            { ``type`` = "function"
              ``function`` =
                { name = ToolName.toString DeleteFile
                  description =
                    "Deletes a file by moving it to the trash directory (.agents/trash/). The file can be recovered manually from there if needed."
                  parameters =
                    {| ``type`` = "object"
                       properties =
                        {| file_path =
                            {| ``type`` = "string"
                               description = "The path to the file to delete." |} |}
                       required = [| "file_path" |] |} } }
          handler = handleDeleteFile
          readOnly = false }

    let toolRegistrations: ToolRegistration array =
        [| readFileReg
           writeFileReg
           runCommandReg
           listDirectoryReg
           grepSearchReg
           patchFileReg
           readFileLinesReg
           findFilesReg
           moveFileReg
           createDirectoryReg
           deleteFileReg |]

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
        $"\n❓ [Confirm] Execute tool '{toolCall.``function``.name}' with arguments: {toolCall.``function``.arguments}? (y/N): "
        |> interactive.write

        let response = interactive.readLine ()

        not (System.String.IsNullOrWhiteSpace response)
        && response.Trim().ToLower() = "y"

    let confirmToolCall interactive runtimeConfig (toolCall: LlmClient.ToolCall) =
        match runtimeConfig.autoConfirm with
        | All ->
            $"🟢 [Auto-confirm] Executing tool '{toolCall.``function``.name}' (auto-confirm all)."
            |> interactive.writeLine

            true
        | ReadsOnly when isReadOnlyTool toolCall ->
            $"🟢 [Auto-confirm] Executing read tool '{toolCall.``function``.name}' (auto-confirm reads)."
            |> interactive.writeLine

            true
        | _ -> promptToolConfirmation interactive toolCall

    let toolHandlers =
        toolRegistrations
        |> Array.map (fun r -> ToolName.toString r.toolName, r.handler)
        |> Map.ofArray

    let executeToolCall config (toolCall: LlmClient.ToolCall) =
        async {
            if not (config.interactive.confirmToolCall config.interactive config.runtimeConfig toolCall) then
                $"⚠️  [Tool] Execution of '{toolCall.``function``.name}' cancelled by user."
                |> config.interactive.writeLine

                return Error "Tool execution cancelled by user."
            else
                try
                    use jsonDoc = System.Text.Json.JsonDocument.Parse toolCall.``function``.arguments
                    let root = jsonDoc.RootElement
                    let toolName = toolCall.``function``.name

                    match toolHandlers |> Map.tryFind toolName with
                    | Some handler -> return! handler config root
                    | None -> return $"Unknown function '{toolName}'." |> Error
                with ex ->
                    return
                        $"Failed to parse arguments for tool '{toolCall.``function``.name}': {ex.Message}"
                        |> Error
        }
