namespace CodingAgent

module Program =
    let hasArg args flag = args |> Array.contains flag

    let pickAutoConfirm args =
        if hasArg args "--auto-confirm" then All
        elif hasArg args "--auto-confirm-reads" then ReadsOnly
        else Off

    let pickSessionToLoad args =
        let idx = args |> Array.tryFindIndex (fun a -> a = "--load")

        match idx with
        | Some i when i + 1 < args.Length -> Some args.[i + 1]
        | _ -> None

    let newLlmClientConfig apiKey =
        let model =
            let m = System.Environment.GetEnvironmentVariable "OPENAI_MODEL"
            if System.String.IsNullOrWhiteSpace m then "gpt-4o" else m

        let endpoint =
            let ep = System.Environment.GetEnvironmentVariable "OPENAI_API_BASE"

            if System.String.IsNullOrWhiteSpace ep then
                "https://api.openai.com/v1/chat/completions"
            else
                ep

        { apiKey = apiKey
          model = model
          endpoint = endpoint
          maxRetries = 3
          timeoutSeconds = 120 }

    let reportSandboxStatus sandboxMode =
        match sandboxMode with
        | Sandbox.BwrapSandbox -> printfn "✓ Sandbox: bwrap detected. OS-level isolation enabled."
        | Sandbox.FallbackOnly ->
            printfn "⚠ Warning: bwrap not found or failed to initialize. Running in fallback mode (denylist only)."

    let newAgentConfig args llmClientConfig =
        let systemPrompt =
            "You are an AI coding assistant that can read files, write files, and execute shell commands. You operate in a ReAct loop. When asked to do something, use your tools to accomplish the task. When the task is complete, provide a final response to the user."

        let fileSystem = FileOps.defaultFileSystem
        let sessionsDir = ".agents/sessions"
        let commandTimeoutMs = 120000
        let maxFileSizeBytes = 100L * 1024L * 1024L
        let maxOutputBytes = 1 * 1024 * 1024
        let maxDisplay = 100
        let sandboxMode = Sandbox.detectSandboxMode ()
        reportSandboxStatus sandboxMode

        { llmClientConfig = llmClientConfig
          tools =
            { readFile = Tools.readFile fileSystem maxFileSizeBytes
              writeFile = Tools.writeFile fileSystem maxFileSizeBytes
              runCommand =
                Tools.runCommand fileSystem maxOutputBytes commandTimeoutMs sandboxMode fileSystem.workspaceRoot
              listDirectory = Tools.listDirectory fileSystem
              grepSearch = Tools.grepSearch fileSystem maxDisplay maxFileSizeBytes
              patchFile = Tools.patchFile fileSystem maxFileSizeBytes
              readFileLines = Tools.readFileLines fileSystem maxFileSizeBytes
              findFiles = Tools.findFiles fileSystem maxDisplay
              moveFile = Tools.moveFile fileSystem
              createDirectory = Tools.createDirectory fileSystem
              deleteFile = Tools.deleteFile fileSystem
              undo = fun () -> Tools.undo fileSystem }
          sessionStore =
            { saveSession = Session.save fileSystem
              loadSession = Session.load fileSystem
              listSessions = Session.list fileSystem sessionsDir
              sessionPath = Session.pathForName sessionsDir
              timestampedSessionName = Session.timestampedName }
          fileSystem = fileSystem
          interactive =
            { write = printf "%s"
              writeLine = printfn "%s"
              readLine = System.Console.ReadLine
              confirmToolCall = AgentToolCall.confirmToolCall }
          runtimeConfig =
            { systemPrompt = systemPrompt
              maxHistory = 20
              autoConfirm = args |> pickAutoConfirm
              commandTimeoutMs = commandTimeoutMs
              maxToolCallIterations = 25
              maxFileSizeBytes = maxFileSizeBytes
              maxOutputBytes = maxOutputBytes
              sandboxMode = sandboxMode } }

    [<EntryPoint>]
    let main args =
        let apiKey = System.Environment.GetEnvironmentVariable "OPENAI_API_KEY"

        if System.String.IsNullOrWhiteSpace apiKey then
            printfn "Error: OPENAI_API_KEY environment variable is not set."
            printfn "Please set it using: export OPENAI_API_KEY='your_api_key_here'"
            1
        else
            let llmClientConfig = newLlmClientConfig apiKey
            use handle = LlmClient.createClient llmClientConfig

            handle.PostAsync
            |> AgentLoop.start (args |> pickSessionToLoad) (newAgentConfig args llmClientConfig)

            0
