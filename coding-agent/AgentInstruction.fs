namespace CodingAgent

module AgentInstruction =
    type ResponseAction =
        | Continue of LlmClient.ChatMessage list
        | Stop of string * LlmClient.ChatMessage list

    let executeToolCalls config (responseMessage: LlmClient.ResponseMessage) =
        task {
            let! results =
                responseMessage.tool_calls
                |> Array.map (fun call ->
                    task {
                        let! toolResult = AgentToolCall.executeToolCall config call

                        match toolResult with
                        | Ok result ->
                            config.writeLine "  ✅ [Success]"
                            return LlmClient.toolResultMessage call.id call.``function``.name result
                        | Error errMsg ->
                            sprintf "  ❌ [Failure] %s" errMsg |> config.writeLine
                            return LlmClient.toolResultMessage call.id call.``function``.name errMsg
                    })
                |> System.Threading.Tasks.Task.WhenAll

            return results |> Array.toList
        }

    let processResponse config (messages: LlmClient.ChatMessage list) (responseMessage: LlmClient.ResponseMessage) =
        task {
            let assistantMsg: LlmClient.ChatMessage =
                { role = responseMessage.role
                  content = responseMessage.content
                  name = null
                  tool_call_id = null
                  tool_calls = responseMessage.tool_calls }

            let nextMessages = messages @ [ assistantMsg ]

            if not (isNull responseMessage.tool_calls) && responseMessage.tool_calls.Length > 0 then
                let! toolMsgs = executeToolCalls config responseMessage
                return Continue(nextMessages @ toolMsgs)
            else
                return Stop(responseMessage.content, nextMessages)
        }

    type LoopResult =
        | InProgress
        | Completed of
            responseContent: string *
            messages: LlmClient.ChatMessage list *
            promptTokens: int *
            completionTokens: int
        | Failed of errorMessage: string * promptTokens: int * completionTokens: int

    type LoopState =
        { messages: LlmClient.ChatMessage list
          promptTokens: int
          completionTokens: int
          iterationCount: int
          result: LoopResult }

    let accumulateUsage (state: LoopState) (usage: LlmClient.Usage) =
        { state with
            promptTokens = state.promptTokens + usage.prompt_tokens
            completionTokens = state.completionTokens + usage.completion_tokens }

    let processResponseResult config state (responseResult: Result<LlmClient.ChatResponse, string>) =
        task {
            match responseResult with
            | Ok response ->
                let state' =
                    if not (isNull response.usage) then
                        accumulateUsage state response.usage
                    else
                        state

                if response.choices.Length > 0 then
                    let! action = response.choices.[0].message |> processResponse config state.messages

                    match action with
                    | Continue nextMsgs ->
                        let nextIteration = state'.iterationCount + 1

                        if nextIteration >= config.maxToolCallIterations then
                            sprintf
                                "  ⚠️  [Limit] Exceeded %d tool call iterations. Forcing stop."
                                config.maxToolCallIterations
                            |> config.writeLine

                            return
                                { state' with
                                    messages = []
                                    result =
                                        sprintf
                                            "Exceeded maximum tool call iterations (%d)."
                                            config.maxToolCallIterations
                                        |> fun err -> Failed(err, state'.promptTokens, state'.completionTokens) }
                        else
                            return
                                { state' with
                                    messages = nextMsgs
                                    iterationCount = nextIteration
                                    result = InProgress }
                    | Stop(content, nextMsgs) ->
                        return
                            { state' with
                                messages = []
                                result = Completed(content, nextMsgs, state'.promptTokens, state'.completionTokens) }
                else
                    return
                        { state' with
                            messages = []
                            result = Failed("API returned no choices.", state'.promptTokens, state'.completionTokens) }
            | Error errMsg ->
                return
                    { state with
                        messages = []
                        result = Failed(errMsg, state.promptTokens, state.completionTokens) }
        }

    let rec instructionLoop config client state =
        task {
            match state.result with
            | Completed(content, msgs, pt, ct) -> return Ok(content, msgs, pt, ct)
            | Failed(err, pt, ct) -> return Error(err, pt, ct)
            | InProgress ->
                config.write "🤖 Thinking... "

                let! responseResult =
                    LlmClient.sendChatRequest client config.llmClientConfig AgentToolCall.toolsDefinition state.messages

                config.writeLine "Done."
                let! nextState = processResponseResult config state responseResult
                return! instructionLoop config client nextState
        }

    let processInstruction config client fallbackMessages messages =
        let state =
            { messages = messages
              promptTokens = 0
              completionTokens = 0
              iterationCount = 0
              result = InProgress }

        task {
            try
                let! result = instructionLoop config client state

                match result with
                | Ok(responseContent, updatedMessages, pTokens, cTokens) ->
                    if not (System.String.IsNullOrWhiteSpace responseContent) then
                        sprintf "\n🤖 %s" responseContent |> config.writeLine

                    return updatedMessages, pTokens, cTokens
                | Error(errMsg, pTokens, cTokens) ->
                    sprintf "\n❌ An error occurred: %s" errMsg |> config.writeLine
                    return fallbackMessages, pTokens, cTokens
            with ex ->
                sprintf "\n❌ An unexpected error occurred: %s" ex.Message |> config.writeLine

                return fallbackMessages, 0, 0
        }
