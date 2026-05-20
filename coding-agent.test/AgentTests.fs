module CodingAgent.AgentTests

open Xunit
open CodingAgent

let mockConfig =
    { llmClientConfig =
        { apiKey = ""
          model = ""
          endpoint = "" }
      systemPrompt = ""
      maxHistory = 20
      write = ignore
      writeLine = ignore
      readLine = fun () -> "" }

[<Fact>]
let ``handleResponse returns Stop action for assistant message without tool calls`` () =
    let responseMsg =
        { role = "assistant"
          content = "I have completed the task."
          tool_calls = null }

    let currentMessages = []
    let action = Agent.handleResponse mockConfig responseMsg currentMessages

    match action with
    | Stop(content, nextMessages) ->
        Assert.Equal("I have completed the task.", content)
        let msg = Assert.Single nextMessages
        Assert.Equal("assistant", msg.role)
        Assert.Equal("I have completed the task.", msg.content)
    | Continue _ -> failwith "Expected Stop, but got Continue"

[<Fact>]
let ``handleResponse returns Continue action for assistant message with tool calls`` () =
    let dummyToolCall =
        { id = "call_123"
          ``type`` = "function"
          ``function`` =
            { name = "read_file"
              arguments = "{\"filePath\": \"non_existent.txt\"}" } }

    let responseMsg =
        { role = "assistant"
          content = null
          tool_calls = [| dummyToolCall |] }

    let currentMessages = []
    let action = Agent.handleResponse mockConfig responseMsg currentMessages

    match action with
    | Continue nextMessages ->
        Assert.Equal(2, nextMessages.Length)
        let assistantMsg = nextMessages.[0]
        Assert.Equal("assistant", assistantMsg.role)
        Assert.Equal(1, assistantMsg.tool_calls.Length)
        let resultMsg = nextMessages.[1]
        Assert.Equal("tool", resultMsg.role)
        Assert.Equal("call_123", resultMsg.tool_call_id)
        Assert.Contains("not found", resultMsg.content)
    | Stop _ -> failwith "Expected Continue, but got Stop"

[<Fact>]
let ``truncateMessages does not truncate if length is within limits`` () =
    let sysMsg = LlmClient.systemMessage "System"
    let userMsg = LlmClient.userMessage "Hello"
    let messages = [ sysMsg; userMsg ]
    let result = Agent.truncateMessages 20 messages
    Assert.Equal(2, result.Length)
    Assert.Equal("system", result.[0].role)

[<Fact>]
let ``truncateMessages truncates to maxHistory + 1 if exceeded`` () =
    let sysMsg = LlmClient.systemMessage "System"

    let messages =
        sysMsg :: [ for i in 1..25 -> LlmClient.userMessage (sprintf "Msg %d" i) ]

    let result = Agent.truncateMessages 20 messages
    Assert.Equal(21, result.Length) // 1 system + 20 messages
    Assert.Equal("system", result.[0].role)
    Assert.Equal("user", result.[1].role)
    Assert.Equal("Msg 6", result.[1].content)

[<Fact>]
let ``truncateMessages removes orphaned tool message after truncation`` () =
    let sysMsg = LlmClient.systemMessage "System"

    let makeMsg i =
        if i = 6 then
            { role = "tool"
              content = "tool result"
              name = "some_tool"
              tool_call_id = "123"
              tool_calls = null }
        else
            LlmClient.userMessage (sprintf "Msg %d" i)

    let messages = sysMsg :: [ for i in 1..25 -> makeMsg i ]
    let result = Agent.truncateMessages 20 messages
    Assert.Equal(20, result.Length)
    Assert.Equal("system", result.[0].role)
    Assert.Equal("user", result.[1].role)
    Assert.Equal("Msg 7", result.[1].content)
