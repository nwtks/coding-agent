namespace CodingAgent

module Program =
    let pickAutoConfirm args =
        if args |> Array.contains "--auto-confirm" then
            All
        elif args |> Array.contains "--auto-confirm-reads" then
            ReadsOnly
        else
            Off

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

    let newAgentConfig args llmClientConfig =
        let systemPrompt =
            "You are an AI coding assistant that can read files, write files, and execute shell commands. You operate in a ReAct loop. When asked to do something, use your tools to accomplish the task. When the task is complete, provide a final response to the user."

        let fileSystem = FileOps.defaultFileSystem
        let sessionsDir = ".agents/sessions"
        let commandTimeoutMs = 120000

        { llmClientConfig = llmClientConfig
          tools =
            { readFile = Tools.readFile fileSystem
              writeFile = Tools.writeFile fileSystem
              runCommand = Tools.runCommand fileSystem commandTimeoutMs
              listDirectory = Tools.listDirectory fileSystem
              grepSearch = Tools.grepSearch fileSystem
              patchFile = Tools.patchFile fileSystem
              readFileLines = Tools.readFileLines fileSystem
              findFiles = Tools.findFiles fileSystem }
          sessionStore = Session.newSessionStore fileSystem sessionsDir
          fileSystem = fileSystem
          write = printf "%s"
          writeLine = printfn "%s"
          readLine = System.Console.ReadLine
          confirmToolCall = AgentToolCall.confirmToolCall
          systemPrompt = systemPrompt
          maxHistory = 20
          autoConfirm = args |> pickAutoConfirm
          commandTimeoutMs = commandTimeoutMs
          maxToolCallIterations = 25 }

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
