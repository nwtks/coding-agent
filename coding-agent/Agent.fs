namespace CodingAgent

type ResponseAction =
    | Continue of ChatMessage list
    | Stop of string * ChatMessage list

module Agent =
    let toolsDefinition =
        [| { ``type`` = "function"
             ``function`` =
               { name = "read_file"
                 description = "Reads the content of a file."
                 parameters =
                   {| ``type`` = "object"
                      properties =
                       {| filePath =
                           {| ``type`` = "string"
                              description = "The path to the file to read." |} |}
                      required = [| "filePath" |] |} } }
           { ``type`` = "function"
             ``function`` =
               { name = "write_file"
                 description = "Writes content to a file. Overwrites if it exists."
                 parameters =
                   {| ``type`` = "object"
                      properties =
                       {| filePath =
                           {| ``type`` = "string"
                              description = "The path to the file to write." |}
                          content =
                           {| ``type`` = "string"
                              description = "The content to write to the file." |} |}
                      required = [| "filePath"; "content" |] |} } }
           { ``type`` = "function"
             ``function`` =
               { name = "run_command"
                 description = "Executes a shell command."
                 parameters =
                   {| ``type`` = "object"
                      properties =
                       {| commandLine =
                           {| ``type`` = "string"
                              description = "The bash shell command to execute." |}
                          cwd =
                           {| ``type`` = "string"
                              description = "The current working directory (optional)." |} |}
                      required = [| "commandLine" |] |} } }
           { ``type`` = "function"
             ``function`` =
               { name = "list_directory"
                 description = "Lists files and subdirectories within a directory."
                 parameters =
                   {| ``type`` = "object"
                      properties =
                       {| directoryPath =
                           {| ``type`` = "string"
                              description =
                               "The path to the directory to list (optional, defaults to current directory)." |} |}
                      required = [||] |} } } |]

    let executeToolCall (toolCall: ToolCall) =
        try
            use jsonDoc = System.Text.Json.JsonDocument.Parse toolCall.``function``.arguments
            let root = jsonDoc.RootElement

            match toolCall.``function``.name with
            | "read_file" ->
                let filePath = root.GetProperty("filePath").GetString()
                printfn "🛠️  [Tool] Executing read_file: %s" filePath
                Tools.readFile filePath
            | "write_file" ->
                let filePath = root.GetProperty("filePath").GetString()
                let content = root.GetProperty("content").GetString()
                printfn "🛠️  [Tool] Executing write_file: %s" filePath
                Tools.writeFile filePath content
            | "run_command" ->
                let commandLine = root.GetProperty("commandLine").GetString()

                let cwd =
                    let hasCwd = ref Unchecked.defaultof<System.Text.Json.JsonElement>

                    if root.TryGetProperty("cwd", hasCwd) then
                        hasCwd.Value.GetString()
                    else
                        ""

                printfn "🛠️  [Tool] Executing run_command: %s (cwd: %s)" commandLine cwd
                Tools.runCommand commandLine cwd
            | "list_directory" ->
                let directoryPath =
                    let hasPath = ref Unchecked.defaultof<System.Text.Json.JsonElement>

                    if root.TryGetProperty("directoryPath", hasPath) then
                        hasPath.Value.GetString()
                    else
                        ""

                printfn "🛠️  [Tool] Executing list_directory: %s" directoryPath
                Tools.listDirectory directoryPath
            | _ -> sprintf "Error: Unknown function '%s'." toolCall.``function``.name |> Error
        with ex ->
            sprintf "Error executing tool call '%s': %s" toolCall.``function``.name ex.Message
            |> Error

    let handleResponse responseMessage currentMessages =
        let assistantMsg =
            { role = responseMessage.role
              content = responseMessage.content
              name = Unchecked.defaultof<string>
              tool_call_id = Unchecked.defaultof<string>
              tool_calls = responseMessage.tool_calls }

        let nextMessages = currentMessages @ [ assistantMsg ]

        if not (isNull responseMessage.tool_calls) && responseMessage.tool_calls.Length > 0 then
            let toolResultMessages =
                responseMessage.tool_calls
                |> Array.map (fun call ->
                    match executeToolCall call with
                    | Ok result ->
                        printfn "  ✅ [Success]"
                        LlmClient.toolResultMessage call.id call.``function``.name result
                    | Error errMsg ->
                        printfn "  ❌ [Failure] %s" errMsg
                        LlmClient.toolResultMessage call.id call.``function``.name errMsg)
                |> Array.toList

            Continue(nextMessages @ toolResultMessages)
        else
            Stop(responseMessage.content, nextMessages)

    let runLoop client config messages =
        task {
            let mutable currentMessages = messages
            let mutable loop = true
            let mutable result = None
            let mutable error = None

            while loop do
                let request =
                    { model = config.model
                      messages = currentMessages |> List.toArray
                      tools = toolsDefinition }

                printf "🤖 Thinking... "
                let! responseResult = LlmClient.sendChatRequest client config request
                printfn "Done."

                match responseResult with
                | Error errMsg ->
                    error <- Some errMsg
                    loop <- false
                | Ok response ->
                    if response.choices.Length = 0 then
                        error <- Some "Error: API returned no choices."
                        loop <- false
                    else
                        let responseMessage = response.choices.[0].message

                        match handleResponse responseMessage currentMessages with
                        | Continue nextMsgs -> currentMessages <- nextMsgs
                        | Stop(content, nextMsgs) ->
                            result <- Some(content, nextMsgs)
                            loop <- false

            match error with
            | Some err -> return Error err
            | None -> return Ok result.Value
        }

    [<TailCall>]
    let rec repl client config messages =
        printf "\n> "
        let input = System.Console.ReadLine()

        if input = "exit" || System.String.IsNullOrWhiteSpace input then
            printfn "Goodbye!"
        else
            let userMsg = LlmClient.userMessage input
            let currentMessages = messages @ [ userMsg ]

            let nextMessages =
                try
                    let resultTask = runLoop client config currentMessages
                    resultTask.Wait()

                    match resultTask.Result with
                    | Ok(responseContent, updatedMessages) ->
                        if not (System.String.IsNullOrWhiteSpace responseContent) then
                            printfn "\n🤖 %s" responseContent

                        updatedMessages
                    | Error errMsg ->
                        printfn "\n❌ An error occurred: %s" errMsg
                        messages
                with ex ->
                    printfn
                        "\n❌ An unexpected error occurred: %s"
                        (if not (isNull ex.InnerException) then
                             ex.InnerException.Message
                         else
                             ex.Message)

                    messages

            repl client config nextMessages

    let start client config =
        printfn "🚀 F# Coding Agent started! Type 'exit' to quit."
        let initialMessages = [ LlmClient.systemMessage config.systemPrompt ]
        repl client config initialMessages
