module CodingAgent.ProgramTests

open Xunit
open CodingAgent

[<Fact>]
let ``pickAutoConfirm returns All when --auto-confirm is present`` () =
    let result = Program.pickAutoConfirm [| "--auto-confirm" |]
    Assert.Equal(All, result)

[<Fact>]
let ``pickAutoConfirm returns ReadsOnly when --auto-confirm-reads is present`` () =
    let result = Program.pickAutoConfirm [| "--auto-confirm-reads" |]
    Assert.Equal(ReadsOnly, result)

[<Fact>]
let ``pickAutoConfirm returns Off when no auto-confirm args`` () =
    let result = Program.pickAutoConfirm [| "--other-arg" |]
    Assert.Equal(Off, result)

[<Fact>]
let ``pickAutoConfirm ignores --auto-confirm-reads when --auto-confirm present`` () =
    let result = Program.pickAutoConfirm [| "--auto-confirm"; "--auto-confirm-reads" |]
    Assert.Equal(All, result)

[<Fact>]
let ``pickSessionToLoad returns Some name when --load with argument`` () =
    let result = Program.pickSessionToLoad [| "--load"; "mysession" |]
    Assert.Equal(Some "mysession", result)

[<Fact>]
let ``pickSessionToLoad returns None when --load without argument`` () =
    let result = Program.pickSessionToLoad [| "--load" |]
    Assert.True result.IsNone

[<Fact>]
let ``pickSessionToLoad returns None when --load at end without argument`` () =
    let result = Program.pickSessionToLoad [| "--other"; "--load" |]
    Assert.True result.IsNone

[<Fact>]
let ``pickSessionToLoad returns None when no --load arg`` () =
    let result = Program.pickSessionToLoad [| "--something-else" |]
    Assert.True result.IsNone

[<Fact>]
let ``newLlmClientConfig uses environment variables when set`` () =
    try
        System.Environment.SetEnvironmentVariable("OPENAI_MODEL", "gpt-4-turbo")
        System.Environment.SetEnvironmentVariable("OPENAI_API_BASE", "https://custom.api/v1/chat/completions")
        let config = Program.newLlmClientConfig "my-key"
        Assert.Equal("my-key", config.apiKey)
        Assert.Equal("gpt-4-turbo", config.model)
        Assert.Equal("https://custom.api/v1/chat/completions", config.endpoint)
    finally
        System.Environment.SetEnvironmentVariable("OPENAI_MODEL", null)
        System.Environment.SetEnvironmentVariable("OPENAI_API_BASE", null)

[<Fact>]
let ``newLlmClientConfig uses defaults when env vars are empty`` () =
    try
        System.Environment.SetEnvironmentVariable("OPENAI_MODEL", "")
        System.Environment.SetEnvironmentVariable("OPENAI_API_BASE", "")
        let config = Program.newLlmClientConfig "my-key"
        Assert.Equal("my-key", config.apiKey)
        Assert.Equal("gpt-4o", config.model)
        Assert.Equal("https://api.openai.com/v1/chat/completions", config.endpoint)
    finally
        System.Environment.SetEnvironmentVariable("OPENAI_MODEL", null)
        System.Environment.SetEnvironmentVariable("OPENAI_API_BASE", null)

[<Fact>]
let ``newAgentConfig correctly sets autoConfirm from args`` () =
    let llmConfig =
        { apiKey = "test-key"
          model = "gpt-4o"
          endpoint = "https://test.api/v1/chat/completions" }

    let config = Program.newAgentConfig [| "--auto-confirm" |] llmConfig
    Assert.Equal(All, config.autoConfirm)
    Assert.Equal("test-key", config.llmClientConfig.apiKey)
    Assert.Equal(20, config.maxHistory)

[<Fact>]
let ``newAgentConfig uses Off autoConfirm by default`` () =
    let llmConfig =
        { apiKey = "test-key"
          model = "gpt-4o"
          endpoint = "https://test.api/v1/chat/completions" }

    let config = Program.newAgentConfig [||] llmConfig
    Assert.Equal(Off, config.autoConfirm)
