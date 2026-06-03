namespace CodingAgent

module AgentResponse =
    type ResponseAction =
        | Continue of LlmClient.ChatMessage list
        | Stop of string * LlmClient.ChatMessage list

    let toolResultMessages config (responseMessage: LlmClient.ResponseMessage) =
        responseMessage.tool_calls
        |> Array.map (fun call ->
            match AgentToolCall.executeToolCall config call with
            | Ok result ->
                config.writeLine "  ✅ [Success]"
                LlmClient.toolResultMessage call.id call.``function``.name result
            | Error errMsg ->
                sprintf "  ❌ [Failure] %s" errMsg |> config.writeLine
                LlmClient.toolResultMessage call.id call.``function``.name errMsg)
        |> Array.toList

    let handleResponse config (messages: LlmClient.ChatMessage list) (responseMessage: LlmClient.ResponseMessage) =
        let assistantMsg: LlmClient.ChatMessage =
            { role = responseMessage.role
              content = responseMessage.content
              name = null
              tool_call_id = null
              tool_calls = responseMessage.tool_calls }

        let nextMessages = messages @ [ assistantMsg ]

        if not (isNull responseMessage.tool_calls) && responseMessage.tool_calls.Length > 0 then
            Continue(nextMessages @ toolResultMessages config responseMessage)
        else
            Stop(responseMessage.content, nextMessages)

    type LoopState =
        { messages: LlmClient.ChatMessage list
          promptTokens: int
          completionTokens: int
          iterationCount: int
          result: Result<string * LlmClient.ChatMessage list * int * int, string * int * int> option }

    let handleResponseResult config state (responseResult: Result<LlmClient.ChatResponse, string>) =
        match responseResult with
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
                    let nextIteration = state.iterationCount + 1

                    if nextIteration >= config.maxToolCallIterations then
                        sprintf
                            "  ⚠️  [Limit] Exceeded %d tool call iterations. Forcing stop."
                            config.maxToolCallIterations
                        |> config.writeLine

                        { messages = []
                          promptTokens = newPrompt
                          completionTokens = newCompletion
                          iterationCount = nextIteration
                          result =
                            (sprintf "Error: Exceeded maximum tool call iterations (%d)." config.maxToolCallIterations,
                             newPrompt,
                             newCompletion)
                            |> Error
                            |> Some }
                    else
                        { messages = nextMsgs
                          promptTokens = newPrompt
                          completionTokens = newCompletion
                          iterationCount = nextIteration
                          result = None }
                | Stop(content, nextMsgs) ->
                    { messages = []
                      promptTokens = newPrompt
                      completionTokens = newCompletion
                      iterationCount = state.iterationCount
                      result = (content, nextMsgs, newPrompt, newCompletion) |> Ok |> Some }
            else
                { messages = []
                  promptTokens = newPrompt
                  completionTokens = newCompletion
                  iterationCount = state.iterationCount
                  result = ("Error: API returned no choices.", newPrompt, newCompletion) |> Error |> Some }
        | Error errMsg ->
            { messages = []
              promptTokens = state.promptTokens
              completionTokens = state.completionTokens
              iterationCount = state.iterationCount
              result = (errMsg, state.promptTokens, state.completionTokens) |> Error |> Some }

    let rec runLoop config client state =
        task {
            match state.result with
            | Some result -> return result
            | None ->
                config.write "🤖 Thinking... "

                let! responseResult =
                    LlmClient.sendChatRequest client config.llmClientConfig AgentToolCall.toolsDefinition state.messages

                config.writeLine "Done."
                return! responseResult |> handleResponseResult config state |> runLoop config client
        }

    let runAgentLoop config client messages currentMessages =
        let state =
            { messages = currentMessages
              promptTokens = 0
              completionTokens = 0
              iterationCount = 0
              result = None }

        task {
            try
                let! result = runLoop config client state

                match result with
                | Ok(responseContent, updatedMessages, pTokens, cTokens) ->
                    if not (System.String.IsNullOrWhiteSpace responseContent) then
                        sprintf "\n🤖 %s" responseContent |> config.writeLine

                    return updatedMessages, pTokens, cTokens
                | Error(errMsg, pTokens, cTokens) ->
                    sprintf "\n❌ An error occurred: %s" errMsg |> config.writeLine
                    return messages, pTokens, cTokens
            with ex ->
                sprintf
                    "\n❌ An unexpected error occurred: %s"
                    (if not (isNull ex.InnerException) then
                         ex.InnerException.Message
                     else
                         ex.Message)
                |> config.writeLine

                return messages, 0, 0
        }
