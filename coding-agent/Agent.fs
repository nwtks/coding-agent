namespace CodingAgent

type ToolImplementations =
    { readFile: string -> Result<string, string>
      writeFile: string -> string -> Result<string, string>
      runCommand: string -> string -> Result<string, string>
      listDirectory: string -> Result<string, string> }

type AgentConfig =
    { llmClientConfig: LlmClientConfig
      tools: ToolImplementations
      write: string -> unit
      writeLine: string -> unit
      readLine: unit -> string
      confirmToolCall: AgentConfig -> ToolCall -> bool
      systemPrompt: string
      maxHistory: int }

module Agent =
    let toolsDefinition =
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
                      required = [||] |} } } |]

    let confirmToolCall config (toolCall: ToolCall) =
        sprintf
            "\n❓ [Confirm] Execute tool '%s' with arguments: %s? (y/N): "
            toolCall.``function``.name
            toolCall.``function``.arguments
        |> config.write

        let response = config.readLine ()

        not (System.String.IsNullOrWhiteSpace response)
        && response.Trim().ToLower() = "y"

    let tryGetJsonPropertyValue (root: System.Text.Json.JsonElement) (propertyName: string) defaultValue =
        let hasProperty = ref Unchecked.defaultof<System.Text.Json.JsonElement>

        if root.TryGetProperty(propertyName, hasProperty) then
            hasProperty.Value.GetString()
        else
            defaultValue

    let executeToolCall config (toolCall: ToolCall) =
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
                | _ -> sprintf "Error: Unknown function '%s'." toolCall.``function``.name |> Error
            else
                sprintf "⚠️  [Tool] Execution of '%s' cancelled by user." toolCall.``function``.name
                |> config.writeLine

                Error "Error: Tool execution cancelled by user."
        with ex ->
            sprintf "Error executing tool call '%s': %s" toolCall.``function``.name ex.Message
            |> Error

    let toolResultMessages config responseMessage =
        responseMessage.tool_calls
        |> Array.map (fun call ->
            match executeToolCall config call with
            | Ok result ->
                config.writeLine "  ✅ [Success]"
                LlmClient.toolResultMessage call.id call.``function``.name result
            | Error errMsg ->
                sprintf "  ❌ [Failure] %s" errMsg |> config.writeLine
                LlmClient.toolResultMessage call.id call.``function``.name errMsg)
        |> Array.toList

    type ResponseAction =
        | Continue of ChatMessage list
        | Stop of string * ChatMessage list

    let handleResponse config messages responseMessage =
        let assistantMsg =
            { role = responseMessage.role
              content = responseMessage.content
              name = Unchecked.defaultof<string>
              tool_call_id = Unchecked.defaultof<string>
              tool_calls = responseMessage.tool_calls }

        let nextMessages = messages @ [ assistantMsg ]

        if not (isNull responseMessage.tool_calls) && responseMessage.tool_calls.Length > 0 then
            Continue(nextMessages @ toolResultMessages config responseMessage)
        else
            Stop(responseMessage.content, nextMessages)

    type LoopState =
        { messages: ChatMessage list
          promptTokens: int
          completionTokens: int
          result: Result<string * list<ChatMessage> * int * int, string * int * int> option }

    let handleResponseResult config state =
        function
        | Ok response ->
            let newPrompt, newCompletion =
                if not (isNull response.usage) then
                    state.promptTokens + response.usage.prompt_tokens,
                    state.completionTokens + response.usage.completion_tokens
                else
                    state.promptTokens, state.completionTokens

            if response.choices.Length > 0 then
                match response.choices.[0].message |> handleResponse config state.messages with
                | Continue nextMsgs ->
                    { messages = nextMsgs
                      promptTokens = newPrompt
                      completionTokens = newCompletion
                      result = None }
                | Stop(content, nextMsgs) ->
                    { messages = []
                      promptTokens = newPrompt
                      completionTokens = newCompletion
                      result = Ok(content, nextMsgs, newPrompt, newCompletion) |> Some }
            else
                { messages = []
                  promptTokens = newPrompt
                  completionTokens = newCompletion
                  result = Error("Error: API returned no choices.", newPrompt, newCompletion) |> Some }
        | Error errMsg ->
            { messages = []
              promptTokens = state.promptTokens
              completionTokens = state.completionTokens
              result = Error(errMsg, state.promptTokens, state.completionTokens) |> Some }

    let runLoop config client messages =
        task {
            let mutable state =
                { messages = messages
                  promptTokens = 0
                  completionTokens = 0
                  result = None }

            while not (List.isEmpty state.messages) do
                config.write "🤖 Thinking... "

                let! responseResult =
                    LlmClient.sendChatRequest client config.llmClientConfig toolsDefinition state.messages

                config.writeLine "Done."
                state <- responseResult |> handleResponseResult config state

            return state.result.Value
        }

    let runAgentLoop config client messages currentMessages =
        try
            let resultTask = currentMessages |> runLoop config client
            resultTask.Wait()

            match resultTask.Result with
            | Ok(responseContent, updatedMessages, pTokens, cTokens) ->
                if not (System.String.IsNullOrWhiteSpace responseContent) then
                    sprintf "\n🤖 %s" responseContent |> config.writeLine

                updatedMessages, pTokens, cTokens
            | Error(errMsg, pTokens, cTokens) ->
                sprintf "\n❌ An error occurred: %s" errMsg |> config.writeLine
                messages, pTokens, cTokens
        with ex ->
            sprintf
                "\n❌ An unexpected error occurred: %s"
                (if not (isNull ex.InnerException) then
                     ex.InnerException.Message
                 else
                     ex.Message)
            |> config.writeLine

            messages, 0, 0

    let truncateMessages maxHistory (messages: ChatMessage list) =
        match messages with
        | systemMsg :: rest ->
            if rest.Length > maxHistory then
                let mutable truncated = rest |> List.skip (rest.Length - maxHistory)

                while truncated.Length > 0 && truncated.Head.role = "tool" do
                    truncated <- truncated.Tail

                systemMsg :: truncated
            else
                messages
        | [] -> []

    let printUsage config promptSession completionSession =
        sprintf
            "📊 Token usage this session: %d prompt + %d completion = %d total"
            promptSession
            completionSession
            (promptSession + completionSession)
        |> config.writeLine

    [<TailCall>]
    let rec repl config client promptSession completionSession messages =
        config.write "\n> "
        let input = config.readLine ()

        if input = "exit" || System.String.IsNullOrWhiteSpace input then
            printUsage config promptSession completionSession
            config.writeLine "Goodbye!"
        elif input = "clear" then
            printUsage config promptSession completionSession
            config.writeLine "🧹 Context cleared."

            [ LlmClient.systemMessage config.systemPrompt ]
            |> repl config client promptSession completionSession
        else
            let nextMsgs, promptTokens, completionTokens =
                messages @ [ LlmClient.userMessage input ]
                |> runAgentLoop config client messages

            nextMsgs
            |> truncateMessages config.maxHistory
            |> repl config client (promptSession + promptTokens) (completionSession + completionTokens)

    let start config client =
        config.writeLine "🚀 F# Coding Agent started! Type 'exit' or 'clear'."
        [ LlmClient.systemMessage config.systemPrompt ] |> repl config client 0 0
