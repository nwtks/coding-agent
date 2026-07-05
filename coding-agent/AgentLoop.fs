namespace CodingAgent

module AgentLoop =
    [<TailCall>]
    let rec dropTrailingTool (messages: LlmClient.ChatMessage list) =
        match messages with
        | head :: tail when head.role = "tool" -> dropTrailingTool tail
        | _ -> messages

    let truncateMessages maxHistory messages =
        match messages with
        | systemMsg :: rest ->
            if rest.Length > maxHistory then
                rest
                |> List.skip (rest.Length - maxHistory)
                |> dropTrailingTool
                |> fun truncated -> systemMsg :: truncated
            else
                messages
        | [] -> []

    let printUsage config promptSession completionSession =
        $"📊 Token usage this session: {promptSession} prompt + {completionSession} completion = {promptSession + completionSession} total"
        |> config.interactive.writeLine

    let splitCommand (command: string) =
        command.Trim().Split(' ', System.StringSplitOptions.RemoveEmptyEntries)

    let setAutoConfirmMode config =
        function
        | "on" ->
            config.interactive.writeLine "🟢 Auto-confirm mode: ON"

            Some
                { config with
                    runtimeConfig =
                        { config.runtimeConfig with
                            autoConfirm = All } }
        | "off" ->
            config.interactive.writeLine "🔴 Auto-confirm mode: OFF"

            Some
                { config with
                    runtimeConfig =
                        { config.runtimeConfig with
                            autoConfirm = Off } }
        | "reads" ->
            config.interactive.writeLine "🟡 Auto-confirm mode: READS ONLY"

            Some
                { config with
                    runtimeConfig =
                        { config.runtimeConfig with
                            autoConfirm = ReadsOnly } }
        | _ ->
            config.interactive.writeLine "Usage: /autoconfirm on|off|reads"
            None

    let handleAutoConfirmCommand config command =
        let parts = splitCommand command

        if parts.Length >= 2 && parts.[0] = "/autoconfirm" then
            setAutoConfirmMode config (parts.[1].ToLower())
        else
            None

    let resolveSavePath (parts: string array) sessionStore =
        (if parts.Length >= 2 then
             parts.[1]
         else
             sessionStore.timestampedSessionName ())
        |> sessionStore.sessionPath

    let handleSaveCommand config command messages =
        let parts = splitCommand command

        if parts.Length >= 1 && parts.[0] = "/save" then
            let path = resolveSavePath parts config.sessionStore

            match config.sessionStore.saveSession path messages with
            | Ok() -> $"💾 Session saved to '{path}'" |> config.interactive.writeLine
            | Error err -> $"❌ {err}" |> config.interactive.writeLine

            true
        else
            false

    let loadSessionAtPath config sessionName =
        let path = config.sessionStore.sessionPath sessionName

        match config.sessionStore.loadSession path with
        | Ok msgs ->
            $"📂 Session loaded from '{path}' ({List.length msgs} messages)"
            |> config.interactive.writeLine

            Some msgs
        | Error err ->
            $"❌ {err}" |> config.interactive.writeLine
            None

    let listSessions config =
        let files = config.sessionStore.listSessions ()

        if Seq.isEmpty files then
            config.interactive.writeLine "📂 No saved sessions found."
        else
            config.interactive.writeLine "📂 Available sessions:"
            files |> Seq.iter config.interactive.writeLine

        None

    let handleLoadCommand config command =
        let parts = splitCommand command

        if parts.Length = 0 || parts.[0] <> "/load" then
            None
        elif parts.Length >= 2 then
            loadSessionAtPath config parts.[1]
        else
            listSessions config

    let handleExitCommand config promptSession completionSession messages =
        let autoSavePath =
            config.sessionStore.timestampedSessionName () |> config.sessionStore.sessionPath

        match config.sessionStore.saveSession autoSavePath messages with
        | Ok() -> $"💾 Session auto-saved to '{autoSavePath}'" |> config.interactive.writeLine
        | Error _ -> ()

        printUsage config promptSession completionSession
        config.interactive.writeLine "Goodbye!"

    let handleClearCommand config promptSession completionSession =
        printUsage config promptSession completionSession
        config.interactive.writeLine "🧹 Context cleared."
        [ LlmClient.systemMessage config.runtimeConfig.systemPrompt ]

    type ReplAction =
        | Continue
        | Exit
        | Clear
        | ShowUsage
        | AutoConfirm of AgentConfig
        | Load of LlmClient.ChatMessage list
        | Query of string

    let handleAutoConfirmBranch config input =
        match handleAutoConfirmCommand config input with
        | Some newConfig -> AutoConfirm newConfig
        | None -> Continue

    let handleLoadBranch config input =
        match handleLoadCommand config input with
        | Some msgs -> Load msgs
        | None -> Continue

    let private handleBasicCommand input =
        if System.String.IsNullOrWhiteSpace input then Some Continue
        elif input = "/exit" then Some Exit
        elif input = "/clear" then Some Clear
        elif input = "/token" then Some ShowUsage
        else None

    let handleInput config messages input =
        match handleBasicCommand input with
        | Some cmd -> cmd
        | None ->
            if input.StartsWith "/autoconfirm" then
                handleAutoConfirmBranch config input
            elif input.StartsWith "/save" then
                handleSaveCommand config input messages |> ignore
                Continue
            elif input.StartsWith "/load" then
                handleLoadBranch config input
            else
                Query input

    let rec repl config client promptSession completionSession messages =
        async {
            config.interactive.write "\n> "
            let input = config.interactive.readLine ()

            match handleInput config messages input with
            | Exit -> handleExitCommand config promptSession completionSession messages
            | Clear ->
                return!
                    repl
                        config
                        client
                        promptSession
                        completionSession
                        (handleClearCommand config promptSession completionSession)
            | AutoConfirm newConfig -> return! repl newConfig client promptSession completionSession messages
            | Load loadedMsgs ->
                config.interactive.writeLine "📂 Session loaded. Context restored."
                return! repl config client promptSession completionSession loadedMsgs
            | ShowUsage ->
                printUsage config promptSession completionSession
                return! repl config client promptSession completionSession messages
            | Query queryInput -> return! replAsync config client promptSession completionSession messages queryInput
            | Continue -> return! repl config client promptSession completionSession messages
        }

    and replAsync config client promptSession completionSession messages input =
        async {
            let! nextMsgs, promptTokens, completionTokens =
                messages @ [ LlmClient.userMessage input ]
                |> AgentInstruction.processInstruction config client messages

            return!
                repl
                    config
                    client
                    (promptSession + promptTokens)
                    (completionSession + completionTokens)
                    (truncateMessages config.runtimeConfig.maxHistory nextMsgs)
        }

    let loadFileContent config filePath =
        try
            if config.fileSystem.existsFile filePath then
                let content = config.fileSystem.readFile filePath

                if not (System.String.IsNullOrWhiteSpace content) then
                    Some content
                else
                    None
            else
                None
        with ex ->
            $"  ⚠️  Warning: Could not read '{filePath}': {ex.Message}"
            |> config.interactive.writeLine

            None

    let enrichSystemPrompt config projectGuidelines =
        config.interactive.writeLine "ℹ️ Loaded project instructions from AGENTS.md."

        let enrichedPrompt =
            $"{config.runtimeConfig.systemPrompt}\n\n[Project Guidelines from AGENTS.md]\n{projectGuidelines}"

        { config with
            runtimeConfig =
                { config.runtimeConfig with
                    systemPrompt = enrichedPrompt } }

    let updateConfig config =
        match loadFileContent config "AGENTS.md" with
        | Some content -> enrichSystemPrompt config content
        | None -> config

    let tryLoadSessionMessages sessionToLoad config =
        match sessionToLoad with
        | Some name ->
            let path = config.sessionStore.sessionPath name

            match config.sessionStore.loadSession path with
            | Ok msgs ->
                $"📂 Session loaded from '{path}' ({List.length msgs} messages)"
                |> config.interactive.writeLine

                LlmClient.systemMessage config.runtimeConfig.systemPrompt :: msgs |> Some
            | Error err ->
                $"❌ {err}" |> config.interactive.writeLine
                None
        | None -> None

    let initializeSession sessionToLoad config =
        match tryLoadSessionMessages sessionToLoad config with
        | Some msgs -> msgs
        | None -> [ LlmClient.systemMessage config.runtimeConfig.systemPrompt ]

    let start sessionToLoad config client =
        config.interactive.writeLine "🚀 F# Coding Agent started! Type '/exit' or '/clear'."
        let updatedConfig = updateConfig config
        let messages = initializeSession sessionToLoad updatedConfig
        repl updatedConfig client 0 0 messages |> Async.RunSynchronously
