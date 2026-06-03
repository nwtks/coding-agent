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

    let rec runLoop config client messages state =
        task {
            match state.result with
            | Some result -> return result
            | None ->
                config.write "🤖 Thinking... "

                let! responseResult =
                    LlmClient.sendChatRequest client config.llmClientConfig AgentToolCall.toolsDefinition state.messages

                config.writeLine "Done."

                return!
                    responseResult
                    |> handleResponseResult config state
                    |> runLoop config client messages
        }

    let runAgentLoop config client messages currentMessages =
        let state =
            { messages = currentMessages
              promptTokens = 0
              completionTokens = 0
              result = None }

        task {
            try
                let! result = runLoop config client currentMessages state

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
