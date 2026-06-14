module CodingAgent.ProgramTests

open Xunit
open CodingAgent

[<Theory>]
[<InlineData("--auto-confirm", "All")>]
[<InlineData("--auto-confirm-reads", "ReadsOnly")>]
[<InlineData("--other-arg", "Off")>]
[<InlineData("--auto-confirm --auto-confirm-reads", "All")>]
let ``pickAutoConfirm picks correct auto-confirm mode based on flags`` (argsStr: string, expectedModeName: string) =
    let args = argsStr.Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
    let result = Program.pickAutoConfirm args

    let expected =
        match expectedModeName with
        | "All" -> All
        | "ReadsOnly" -> ReadsOnly
        | _ -> Off

    Assert.Equal(expected, result)

[<Theory>]
[<InlineData("--load mysession", true, "mysession")>]
[<InlineData("--load", false, "")>]
[<InlineData("--other --load", false, "")>]
[<InlineData("--something-else", false, "")>]
let ``pickSessionToLoad returns Some when --load is followed by a name, None otherwise``
    (argsStr: string, expectSome: bool, expectedName: string)
    =
    let args = argsStr.Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
    let result = Program.pickSessionToLoad args

    if expectSome then
        Assert.Equal(Some expectedName, result)
    else
        Assert.True result.IsNone

[<Fact>]
let ``newLlmClientConfig reads model and endpoint from OPENAI_MODEL and OPENAI_API_BASE env vars`` () =
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
let ``newLlmClientConfig falls back to defaults when environment variables are empty or unset`` () =
    try
        System.Environment.SetEnvironmentVariable("OPENAI_MODEL", "")
        System.Environment.SetEnvironmentVariable("OPENAI_API_BASE", "")
        let config = Program.newLlmClientConfig "my-key"
        Assert.Equal("my-key", config.apiKey)
        Assert.Equal("gpt-4o", config.model)
        Assert.Equal("https://api.openai.com/v1/chat/completions", config.endpoint)
        Assert.Equal(120, config.timeoutSeconds)
    finally
        System.Environment.SetEnvironmentVariable("OPENAI_MODEL", null)
        System.Environment.SetEnvironmentVariable("OPENAI_API_BASE", null)

[<Fact>]
let ``newAgentConfig sets autoConfirm to All when --auto-confirm is passed`` () =
    let llmConfig =
        { apiKey = "test-key"
          model = "gpt-4o"
          endpoint = "https://test.api/v1/chat/completions"
          maxRetries = 0
          timeoutSeconds = 30 }

    let config = Program.newAgentConfig [| "--auto-confirm" |] llmConfig
    Assert.Equal(All, config.autoConfirm)
    Assert.Equal("test-key", config.llmClientConfig.apiKey)
    Assert.Equal(20, config.maxHistory)
    Assert.Equal(25, config.maxToolCallIterations)

[<Fact>]
let ``newAgentConfig defaults autoConfirm to Off when no flags are provided`` () =
    let llmConfig =
        { apiKey = "test-key"
          model = "gpt-4o"
          endpoint = "https://test.api/v1/chat/completions"
          maxRetries = 0
          timeoutSeconds = 30 }

    let config = Program.newAgentConfig [||] llmConfig
    Assert.Equal(Off, config.autoConfirm)
