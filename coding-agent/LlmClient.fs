namespace CodingAgent

type LlmClientConfig =
    { apiKey: string
      model: string
      endpoint: string
      maxRetries: int
      timeoutSeconds: int }

type LlmClientPostAsync = string -> Async<System.Net.Http.HttpResponseMessage>

type LlmClientHandle(client: System.Net.Http.HttpClient, postAsync: LlmClientPostAsync) =
    member _.PostAsync = postAsync

    interface System.IDisposable with
        member _.Dispose() = client.Dispose()

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

    let createClient config =
        let client = new System.Net.Http.HttpClient()
        client.Timeout <- System.TimeSpan.FromSeconds(float config.timeoutSeconds)

        client.DefaultRequestHeaders.Authorization <-
            System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.apiKey)

        let postAsync =
            fun content ->
                async {
                    let! response =
                        client.PostAsync(
                            config.endpoint,
                            new System.Net.Http.StringContent(content, System.Text.Encoding.UTF8, "application/json")
                        )
                        |> Async.AwaitTask

                    return response
                }

        new LlmClientHandle(client, postAsync)

    let serializeOptions =
        let options =
            System.Text.Json.JsonSerializerOptions(
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower
            )

        options.DefaultIgnoreCondition <- System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        options

    let isRetryableStatusCode code =
        code = 429 || code = 502 || code = 503 || code = 504

    let delayMs (rng: System.Random) retryCount =
        500 * int (System.Math.Pow(2.0, float retryCount)) + rng.Next(0, 500)

    let deserializeChatResponse (responseBody: string) : Result<ChatResponse, string> =
        try
            System.Text.Json.JsonSerializer.Deserialize<ChatResponse>(responseBody, serializeOptions)
            |> Ok
        with ex ->
            $"Failed to deserialize response: {ex.Message}\nResponse: {responseBody}"
            |> Error

    let formatApiError (response: System.Net.Http.HttpResponseMessage) responseBody =
        $"API Error: {int response.StatusCode} {response.ReasonPhrase}\n{responseBody}"

    type ApiCallResult =
        | ApiSuccess of ChatResponse
        | ApiError of string
        | ApiRetryable of string

    let callApiOnce (client: LlmClientPostAsync) request =
        async {
            try
                let! response = client request
                let! responseBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask

                if response.IsSuccessStatusCode then
                    match deserializeChatResponse responseBody with
                    | Ok chatResponse -> return ApiSuccess chatResponse
                    | Error err -> return ApiError err
                elif isRetryableStatusCode (int response.StatusCode) then
                    return formatApiError response responseBody |> ApiRetryable
                else
                    return formatApiError response responseBody |> ApiError
            with ex ->
                return $"HTTP request failed: {ex.Message}" |> ApiRetryable
        }

    let rec attemptRequest (client: LlmClientPostAsync) (rng: System.Random) maxRetries retryCount request =
        async {
            let! result = callApiOnce client request

            match result with
            | ApiSuccess chatResponse -> return Ok chatResponse
            | ApiError err -> return Error err
            | ApiRetryable _ when retryCount < maxRetries ->
                do! Async.Sleep(delayMs rng retryCount)
                return! attemptRequest client rng maxRetries (retryCount + 1) request
            | ApiRetryable body -> return Error body
        }

    let sendChatRequest (client: LlmClientPostAsync) (config: LlmClientConfig) tools messages =
        let request =
            { model = config.model
              messages = messages |> List.toArray
              tools = tools }

        System.Text.Json.JsonSerializer.Serialize(request, serializeOptions)
        |> attemptRequest client System.Random.Shared config.maxRetries 0
