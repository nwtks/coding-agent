namespace CodingAgent

type FunctionCall = { name: string; arguments: string }

type ToolCall =
    { id: string
      ``type``: string
      ``function``: FunctionCall }

type ChatMessage =
    { role: string
      content: string
      name: string | null
      tool_call_id: string | null
      tool_calls: ToolCall array | null }

type FunctionDef =
    { name: string
      description: string
      parameters: obj }

type ToolDef =
    { ``type``: string
      ``function``: FunctionDef }

type ChatRequest =
    { model: string
      messages: ChatMessage array
      tools: ToolDef array }

type ResponseMessage =
    { role: string
      content: string
      tool_calls: ToolCall array }

type Choice =
    { index: int
      message: ResponseMessage
      finish_reason: string }

type ChatResponse = { id: string; choices: Choice array }

type LlmClientConfig =
    { apiKey: string
      model: string
      endpoint: string
      systemPrompt: string }

module LlmClient =
    let userMessage content =
        { role = "user"
          content = content
          name = Unchecked.defaultof<string>
          tool_call_id = Unchecked.defaultof<string>
          tool_calls = Unchecked.defaultof<ToolCall array> }

    let systemMessage content =
        { role = "system"
          content = content
          name = Unchecked.defaultof<string>
          tool_call_id = Unchecked.defaultof<string>
          tool_calls = Unchecked.defaultof<ToolCall array> }

    let assistantMessage content =
        { role = "assistant"
          content = content
          name = Unchecked.defaultof<string>
          tool_call_id = Unchecked.defaultof<string>
          tool_calls = Unchecked.defaultof<ToolCall array> }

    let toolCallMessage toolCalls =
        { role = "assistant"
          content = ""
          name = Unchecked.defaultof<string>
          tool_call_id = Unchecked.defaultof<string>
          tool_calls = toolCalls }

    let toolResultMessage toolCallId name content =
        { role = "tool"
          content = content
          name = name
          tool_call_id = toolCallId
          tool_calls = Unchecked.defaultof<ToolCall array> }

    let createClient config =
        let client = new System.Net.Http.HttpClient()

        client.DefaultRequestHeaders.Authorization <-
            System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.apiKey)

        client

    let serializeOptions =
        let options =
            System.Text.Json.JsonSerializerOptions(
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower
            )

        options.DefaultIgnoreCondition <- System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        options

    let sendChatRequest (client: System.Net.Http.HttpClient) config request =
        task {
            let json = System.Text.Json.JsonSerializer.Serialize(request, serializeOptions)

            let content =
                new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json")

            try
                let! response = client.PostAsync(config.endpoint, content)
                let! responseBody = response.Content.ReadAsStringAsync()

                if not response.IsSuccessStatusCode then
                    return
                        sprintf
                            "API Error: %d %s\n%s\nRequest: %s"
                            (int response.StatusCode)
                            response.ReasonPhrase
                            responseBody
                            json
                        |> Error
                else
                    try
                        let result =
                            System.Text.Json.JsonSerializer.Deserialize<ChatResponse>(responseBody, serializeOptions)

                        return Ok result
                    with ex ->
                        return
                            sprintf "Failed to deserialize response: %s\nResponse: %s" ex.Message responseBody
                            |> Error
            with ex ->
                return sprintf "HTTP request failed: %s" ex.Message |> Error
        }
