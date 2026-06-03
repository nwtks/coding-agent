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
        sprintf
            "📊 Token usage this session: %d prompt + %d completion = %d total"
            promptSession
            completionSession
            (promptSession + completionSession)
        |> config.writeLine

    let splitCommand (command: string) =
        command.Trim().Split(' ', System.StringSplitOptions.RemoveEmptyEntries)

    let handleAutoConfirmCommand config command =
        let parts = splitCommand command

        if parts.Length >= 2 && parts.[0] = "/autoconfirm" then
            match parts.[1].ToLower() with
            | "on" ->
                config.writeLine "🟢 Auto-confirm mode: ON (all tools)"
                Some { config with autoConfirm = All }
            | "off" ->
                config.writeLine "🔴 Auto-confirm mode: OFF"
                Some { config with autoConfirm = Off }
            | "reads" ->
                config.writeLine "🟡 Auto-confirm mode: READS ONLY"
                Some { config with autoConfirm = ReadsOnly }
            | _ ->
                config.writeLine "Usage: /autoconfirm on|off|reads"
                Some config
        else
            None

    let handleSaveCommand config command messages =
        let parts = splitCommand command

        if parts.Length >= 1 && parts.[0] = "/save" then
            let path =
                if parts.Length >= 2 then
                    parts.[1]
                else
                    config.sessionStore.timestampedSessionName ()
                |> config.sessionStore.sessionPath

            match config.sessionStore.saveSession path messages with
            | Ok() -> sprintf "💾 Session saved to '%s'" path |> config.writeLine
            | Error err -> sprintf "❌ %s" err |> config.writeLine

            true
        else
            false

    let handleLoadCommand config command =
        let parts = splitCommand command

        if parts.Length >= 2 && parts.[0] = "/load" then
            let path = config.sessionStore.sessionPath parts.[1]

            match config.sessionStore.loadSession path with
            | Ok msgs ->
                sprintf "📂 Session loaded from '%s' (%d messages)" path (List.length msgs)
                |> config.writeLine

                Some msgs
            | Error err ->
                sprintf "❌ %s" err |> config.writeLine
                None
        else if parts.Length = 1 && parts.[0] = "/load" then
            let files = config.sessionStore.listSessions ()

            if not (Seq.isEmpty files) then
                config.writeLine "📂 Available sessions:"
                files |> Seq.iter config.writeLine
            else
                config.writeLine "📂 No saved sessions found."

            None
        else
            None

    let handleExitCommand config promptSession completionSession messages =
        let autoSavePath =
            config.sessionStore.timestampedSessionName () |> config.sessionStore.sessionPath

        match config.sessionStore.saveSession autoSavePath messages with
        | Ok() -> config.writeLine (sprintf "💾 Session auto-saved to '%s'" autoSavePath)
        | Error _ -> ()

        printUsage config promptSession completionSession
        config.writeLine "Goodbye!"

    let handleClearCommand config promptSession completionSession =
        printUsage config promptSession completionSession
        config.writeLine "🧹 Context cleared."
        [ LlmClient.systemMessage config.systemPrompt ]

    let rec repl config client promptSession completionSession messages =
        task {
            config.write "\n> "
            let input = config.readLine ()

            if System.String.IsNullOrWhiteSpace input then
                return! repl config client promptSession completionSession messages
            elif input = "/exit" then
                handleExitCommand config promptSession completionSession messages
            elif input = "/clear" then
                return!
                    repl
                        config
                        client
                        promptSession
                        completionSession
                        (handleClearCommand config promptSession completionSession)
            elif input.StartsWith "/autoconfirm" then
                match handleAutoConfirmCommand config input with
                | Some newConfig -> return! repl newConfig client promptSession completionSession messages
                | None -> return! repl config client promptSession completionSession messages
            elif input.StartsWith "/save" then
                handleSaveCommand config input messages |> ignore
                return! repl config client promptSession completionSession messages
            elif input.StartsWith "/load" then
                match handleLoadCommand config input with
                | Some loadedMsgs ->
                    config.writeLine "📂 Session loaded. Context restored."
                    return! repl config client promptSession completionSession loadedMsgs
                | None -> return! repl config client promptSession completionSession messages
            else
                let! nextMsgs, promptTokens, completionTokens =
                    messages @ [ LlmClient.userMessage input ]
                    |> AgentResponse.runAgentLoop config client messages

                return!
                    repl
                        config
                        client
                        (promptSession + promptTokens)
                        (completionSession + completionTokens)
                        (truncateMessages config.maxHistory nextMsgs)
        }

    let loadAgentsMd config filePath =
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
            sprintf "  ⚠️  Warning: Could not read '%s': %s" filePath ex.Message
            |> config.writeLine

            None

    let updateConfig config =
        match loadAgentsMd config "AGENTS.md" with
        | Some content ->
            config.writeLine "ℹ️ Loaded project instructions from AGENTS.md."

            let enrichedPrompt =
                sprintf "%s\n\n[Project Guidelines from AGENTS.md]\n%s" config.systemPrompt content

            { config with
                systemPrompt = enrichedPrompt }
        | None -> config

    let initialMessages sessionToLoad config =
        match sessionToLoad with
        | Some name ->
            let path = config.sessionStore.sessionPath name

            match config.sessionStore.loadSession path with
            | Ok msgs ->
                sprintf "📂 Session loaded from '%s' (%d messages)" path (List.length msgs)
                |> config.writeLine

                LlmClient.systemMessage config.systemPrompt :: msgs
            | Error err ->
                sprintf "❌ %s" err |> config.writeLine
                [ LlmClient.systemMessage config.systemPrompt ]
        | None -> [ LlmClient.systemMessage config.systemPrompt ]

    let start sessionToLoad config client =
        config.writeLine "🚀 F# Coding Agent started! Type '/exit' or '/clear'."
        let updatedConfig = updateConfig config
        let messages = initialMessages sessionToLoad updatedConfig
        (repl updatedConfig client 0 0 messages).GetAwaiter().GetResult()
