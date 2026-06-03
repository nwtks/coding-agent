namespace CodingAgent

type LlmClientConfig =
    { apiKey: string
      model: string
      endpoint: string }

type LlmClientPostAsync = string -> System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage>

module LlmClient =
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

    type Usage =
        { prompt_tokens: int
          completion_tokens: int
          total_tokens: int }

    type ChatResponse =
        { id: string
          choices: Choice array
          usage: Usage | null }

    let userMessage content =
        { role = "user"
          content = content
          name = null
          tool_call_id = null
          tool_calls = null }

    let systemMessage content =
        { role = "system"
          content = content
          name = null
          tool_call_id = null
          tool_calls = null }

    let assistantMessage content =
        { role = "assistant"
          content = content
          name = null
          tool_call_id = null
          tool_calls = null }

    let toolCallMessage toolCalls =
        { role = "assistant"
          content = ""
          name = null
          tool_call_id = null
          tool_calls = toolCalls }

    let toolResultMessage toolCallId name content =
        { role = "tool"
          content = content
          name = name
          tool_call_id = toolCallId
          tool_calls = null }

    let mutable private httpClient: System.Net.Http.HttpClient option = None

    let getClient config =
        match httpClient with
        | Some client -> client
        | None ->
            let client = new System.Net.Http.HttpClient()

            client.DefaultRequestHeaders.Authorization <-
                System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.apiKey)

            httpClient <- Some client
            client

    let createClient config =
        let client = getClient config

        fun content ->
            client.PostAsync(
                config.endpoint,
                new System.Net.Http.StringContent(content, System.Text.Encoding.UTF8, "application/json")
            )

    let disposeClient () =
        match httpClient with
        | Some client ->
            client.Dispose()
            httpClient <- None
        | None -> ()

    let serializeOptions =
        let options =
            System.Text.Json.JsonSerializerOptions(
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower
            )

        options.DefaultIgnoreCondition <- System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        options

    let handleResponse request (responseBody: string) (response: System.Net.Http.HttpResponseMessage) =
        if response.IsSuccessStatusCode then
            try
                System.Text.Json.JsonSerializer.Deserialize<ChatResponse>(responseBody, serializeOptions)
                |> Ok
            with ex ->
                sprintf "Failed to deserialize response: %s\nResponse: %s" ex.Message responseBody
                |> Error
        else
            sprintf
                "API Error: %d %s\n%s\nRequest: %s"
                (int response.StatusCode)
                response.ReasonPhrase
                responseBody
                request
            |> Error

    let sendChatRequest (client: LlmClientPostAsync) (config: LlmClientConfig) tools messages =
        task {
            let request =
                { model = config.model
                  messages = messages |> List.toArray
                  tools = tools }

            let json = System.Text.Json.JsonSerializer.Serialize(request, serializeOptions)

            try
                let! response = client json
                let! responseBody = response.Content.ReadAsStringAsync()
                return handleResponse json responseBody response
            with ex ->
                return sprintf "HTTP request failed: %s" ex.Message |> Error
        }
