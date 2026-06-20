namespace CodingAgent

module AgentInstruction =
    type ResponseAction =
        | Continue of LlmClient.ChatMessage list
        | Stop of string * LlmClient.ChatMessage list

    let formatToolResult config (call: LlmClient.ToolCall) =
        function
        | Ok result ->
            config.interactive.writeLine "  ✅ [Success]"
            LlmClient.toolResultMessage call.id call.``function``.name result
        | Error errMsg ->
            $"  ❌ [Failure] {errMsg}" |> config.interactive.writeLine
            LlmClient.toolResultMessage call.id call.``function``.name errMsg

    let executeToolCalls config (responseMessage: LlmClient.ResponseMessage) =
        async {
            let! results =
                responseMessage.tool_calls
                |> Array.map (fun call ->
                    async {
                        let! toolResult = AgentToolCall.executeToolCall config call
                        return formatToolResult config call toolResult
                    })
                |> Async.Parallel

            return results |> Array.toList
        }

    let processResponse config (messages: LlmClient.ChatMessage list) (responseMessage: LlmClient.ResponseMessage) =
        async {
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

    let accumulateUsage state (usage: LlmClient.Usage) =
        { state with
            promptTokens = state.promptTokens + usage.prompt_tokens
            completionTokens = state.completionTokens + usage.completion_tokens }

    let accumulateUsageIfPresent state (response: LlmClient.ChatResponse) =
        if not (isNull response.usage) then
            accumulateUsage state response.usage
        else
            state

    let handleActionResult config state action =
        match action with
        | Continue nextMsgs ->
            let nextIteration = state.iterationCount + 1

            if nextIteration >= config.runtimeConfig.maxToolCallIterations then
                $"  ⚠️  [Limit] Exceeded {config.runtimeConfig.maxToolCallIterations} tool call iterations. Forcing stop."
                |> config.interactive.writeLine

                { state with
                    messages = []
                    result =
                        $"Exceeded maximum tool call iterations ({config.runtimeConfig.maxToolCallIterations})."
                        |> fun err -> Failed(err, state.promptTokens, state.completionTokens) }
            else
                { state with
                    messages = nextMsgs
                    iterationCount = nextIteration
                    result = InProgress }
        | Stop(content, nextMsgs) ->
            { state with
                messages = []
                result = Completed(content, nextMsgs, state.promptTokens, state.completionTokens) }

    let handleOkResponseWithChoices config state (message: LlmClient.ResponseMessage) =
        async {
            let! action = processResponse config state.messages message
            return handleActionResult config state action
        }

    let handleOkResponse config state (response: LlmClient.ChatResponse) =
        async {
            let state' = accumulateUsageIfPresent state response

            if response.choices.Length > 0 then
                return! handleOkResponseWithChoices config state' response.choices.[0].message
            else
                return
                    { state' with
                        messages = []
                        result = Failed("API returned no choices.", state'.promptTokens, state'.completionTokens) }
        }

    let processResponseResult config state (responseResult: Result<LlmClient.ChatResponse, string>) =
        async {
            match responseResult with
            | Ok response -> return! handleOkResponse config state response
            | Error errMsg ->
                return
                    { state with
                        messages = []
                        result = Failed(errMsg, state.promptTokens, state.completionTokens) }
        }

    let showThinkingIndicator interactive = interactive.write "🤖 Thinking... "

    let hideThinkingIndicator interactive = interactive.writeLine "Done."

    let rec instructionLoop config client state =
        async {
            match state.result with
            | Completed(content, msgs, pt, ct) -> return Ok(content, msgs, pt, ct)
            | Failed(err, pt, ct) -> return Error(err, pt, ct)
            | InProgress ->
                showThinkingIndicator config.interactive

                let! responseResult =
                    LlmClient.sendChatRequest
                        client
                        config.llmClientConfig
                        (AgentToolCall.toolsDefinition ())
                        state.messages

                hideThinkingIndicator config.interactive
                let! nextState = processResponseResult config state responseResult
                return! instructionLoop config client nextState
        }

    let reportResponse config responseContent =
        if not (System.String.IsNullOrWhiteSpace responseContent) then
            $"\n🤖 {responseContent}" |> config.interactive.writeLine

    let reportInstructionError config errMsg =
        $"\n❌ An error occurred: {errMsg}" |> config.interactive.writeLine

    let handleInstructionException config (ex: System.Exception) =
        $"\n❌ An unexpected error occurred: {ex.Message}"
        |> config.interactive.writeLine

    let processInstruction config client fallbackMessages messages =
        let state =
            { messages = messages
              promptTokens = 0
              completionTokens = 0
              iterationCount = 0
              result = InProgress }

        async {
            try
                let! result = instructionLoop config client state

                match result with
                | Ok(responseContent, updatedMessages, pTokens, cTokens) ->
                    reportResponse config responseContent
                    return updatedMessages, pTokens, cTokens
                | Error(errMsg, pTokens, cTokens) ->
                    reportInstructionError config errMsg
                    return fallbackMessages, pTokens, cTokens
            with ex ->
                handleInstructionException config ex
                return fallbackMessages, 0, 0
        }
