namespace CodingAgent

module Program =
    let parseArgs args =
        if args |> Array.contains "--auto-confirm" then
            All
        elif args |> Array.contains "--auto-confirm-reads" then
            ReadsOnly
        else
            Off

    [<EntryPoint>]
    let main args =
        let apiKey = System.Environment.GetEnvironmentVariable "OPENAI_API_KEY"

        if System.String.IsNullOrWhiteSpace apiKey then
            printfn "Error: OPENAI_API_KEY environment variable is not set."
            printfn "Please set it using: export OPENAI_API_KEY='your_api_key_here'"
            1
        else
            let model =
                let m = System.Environment.GetEnvironmentVariable "OPENAI_MODEL"
                if System.String.IsNullOrWhiteSpace m then "gpt-4o" else m

            let endpoint =
                let ep = System.Environment.GetEnvironmentVariable "OPENAI_API_BASE"

                if System.String.IsNullOrWhiteSpace ep then
                    "https://api.openai.com/v1/chat/completions"
                else
                    ep

            let llmClientConfig =
                { apiKey = apiKey
                  model = model
                  endpoint = endpoint }

            let systemPrompt =
                "You are an AI coding assistant that can read files, write files, and execute shell commands. "
                + "You operate in a ReAct loop. When asked to do something, use your tools to accomplish the task. "
                + "When the task is complete, provide a final response to the user."

            let autoConfirmMode = parseArgs args

            let agentConfig =
                { llmClientConfig = llmClientConfig
                  tools =
                    { readFile = Tools.readFile
                      writeFile = Tools.writeFile
                      runCommand = Tools.runCommand
                      listDirectory = Tools.listDirectory
                      grepSearch = Tools.grepSearch
                      patchFile = Tools.patchFile
                      readFileLines = Tools.readFileLines
                      findFiles = Tools.findFiles }
                  write = printf "%s"
                  writeLine = printfn "%s"
                  readLine = System.Console.ReadLine
                  confirmToolCall = Agent.confirmToolCall
                  systemPrompt = systemPrompt
                  maxHistory = 20
                  autoConfirm = autoConfirmMode }

            LlmClient.createClient llmClientConfig |> Agent.start agentConfig
            0
