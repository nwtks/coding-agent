namespace CodingAgent

module AgentToolCall =
    let toolsDefinition: LlmClient.ToolDef array =
        [| { ``type`` = "function"
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
                              description = "The directory path to search in (optional, defaults to current directory)." |} |}
                      required = [| "pattern" |] |} } } |]

    let readOnlyTools =
        set
            [ "read_file"
              "read_file_lines"
              "list_directory"
              "grep_search"
              "find_files" ]

    let isReadOnlyTool (toolCall: LlmClient.ToolCall) =
        readOnlyTools.Contains toolCall.``function``.name

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

    let tryGetJsonPropertyValue (json: System.Text.Json.JsonElement) (propertyName: string) defaultValue =
        let mutable el = Unchecked.defaultof<System.Text.Json.JsonElement>

        if json.TryGetProperty(propertyName, &el) then
            el.GetString()
        else
            defaultValue

    let executeToolCall config (toolCall: LlmClient.ToolCall) =
        try
            if config.confirmToolCall config toolCall then
                use jsonDoc = System.Text.Json.JsonDocument.Parse toolCall.``function``.arguments
                let root = jsonDoc.RootElement

                match toolCall.``function``.name with
                | "read_file" ->
                    let filePath = root.GetProperty("file_path").GetString()
                    sprintf "🛠️  [Tool] Executing read_file: %s" filePath |> config.writeLine
                    config.tools.readFile filePath
                | "write_file" ->
                    let filePath = root.GetProperty("file_path").GetString()
                    let content = root.GetProperty("content").GetString()
                    sprintf "🛠️  [Tool] Executing write_file: %s" filePath |> config.writeLine
                    config.tools.writeFile filePath content
                | "run_command" ->
                    let commandLine = root.GetProperty("command_line").GetString()
                    let cwd = tryGetJsonPropertyValue root "cwd" ""

                    sprintf "🛠️  [Tool] Executing run_command: %s (cwd: %s)" commandLine cwd
                    |> config.writeLine

                    config.tools.runCommand commandLine cwd
                | "list_directory" ->
                    let directoryPath = tryGetJsonPropertyValue root "directory_path" ""

                    sprintf "🛠️  [Tool] Executing list_directory: %s" directoryPath
                    |> config.writeLine

                    config.tools.listDirectory directoryPath
                | "grep_search" ->
                    let query = root.GetProperty("query").GetString()
                    let directoryPath = tryGetJsonPropertyValue root "directory_path" ""

                    sprintf "🛠️  [Tool] Executing grep_search: '%s' in %s" query directoryPath
                    |> config.writeLine

                    config.tools.grepSearch query directoryPath
                | "patch_file" ->
                    let filePath = root.GetProperty("file_path").GetString()
                    let target = root.GetProperty("target").GetString()
                    let replacement = root.GetProperty("replacement").GetString()
                    sprintf "🛠️  [Tool] Executing patch_file: %s" filePath |> config.writeLine
                    config.tools.patchFile filePath target replacement
                | "read_file_lines" ->
                    let filePath = root.GetProperty("file_path").GetString()
                    let startLine = root.GetProperty("start_line").GetInt32()
                    let endLine = root.GetProperty("end_line").GetInt32()

                    sprintf "🛠️  [Tool] Executing read_file_lines: %s (lines %d-%d)" filePath startLine endLine
                    |> config.writeLine

                    config.tools.readFileLines filePath startLine endLine
                | "find_files" ->
                    let pattern = root.GetProperty("pattern").GetString()
                    let directoryPath = tryGetJsonPropertyValue root "directory_path" ""

                    sprintf "🛠️  [Tool] Executing find_files: '%s' in %s" pattern directoryPath
                    |> config.writeLine

                    config.tools.findFiles pattern directoryPath
                | _ -> sprintf "Error: Unknown function '%s'." toolCall.``function``.name |> Error
            else
                sprintf "⚠️  [Tool] Execution of '%s' cancelled by user." toolCall.``function``.name
                |> config.writeLine

                Error "Error: Tool execution cancelled by user."
        with ex ->
            sprintf "Failed executing tool call '%s': %s" toolCall.``function``.name ex.Message
            |> Error
