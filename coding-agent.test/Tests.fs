module CodingAgent.Tests

open System
open System.IO
open Xunit
open CodingAgent

[<Fact>]
let ``Tools.writeFile writes file successfully and readFile reads it back`` () =
    let tempFile =
        Path.Combine(Path.GetTempPath(), sprintf "test_file_%s.txt" (Guid.NewGuid().ToString()))

    try
        let writeContent = "Hello, F# Coding Agent!"

        let writeResult = Tools.writeFile tempFile writeContent

        match writeResult with
        | Ok msg -> Assert.Contains("Successfully wrote to", msg)
        | Error err -> failwithf "Expected Ok, but got Error: %s" err

        let readResult = Tools.readFile tempFile

        match readResult with
        | Ok content -> Assert.Equal(writeContent, content)
        | Error err -> failwithf "Expected Ok, but got Error: %s" err

    finally
        if File.Exists tempFile then
            File.Delete tempFile

[<Fact>]
let ``Tools.readFile returns Error for non-existent file`` () =
    let nonExistentFile =
        Path.Combine(Path.GetTempPath(), sprintf "non_existent_%s.txt" (Guid.NewGuid().ToString()))

    let result = Tools.readFile nonExistentFile

    match result with
    | Ok _ -> failwith "Expected Error, but got Ok"
    | Error msg -> Assert.Contains("not found", msg)

[<Fact>]
let ``Tools.listDirectory lists files and folders correctly`` () =
    let tempDir =
        Path.Combine(Path.GetTempPath(), sprintf "test_dir_%s" (Guid.NewGuid().ToString()))

    let subDir = Path.Combine(tempDir, "sub_folder")
    let tempFile = Path.Combine(tempDir, "test_file.txt")

    try
        Directory.CreateDirectory tempDir |> ignore
        Directory.CreateDirectory subDir |> ignore
        File.WriteAllText(tempFile, "temp content")

        let result = Tools.listDirectory tempDir

        match result with
        | Ok msg ->
            Assert.Contains("Contents of directory", msg)
            Assert.Contains("[DIR]  sub_folder", msg)
            Assert.Contains("[FILE] test_file.txt (12 bytes)", msg)
        | Error err -> failwithf "Expected Ok, but got Error: %s" err

    finally
        if Directory.Exists tempDir then
            Directory.Delete(tempDir, true)

[<Fact>]
let ``Agent.handleResponse returns Stop action for assistant message without tool calls`` () =
    let responseMsg =
        { role = "assistant"
          content = "I have completed the task."
          tool_calls = null }

    let currentMessages = []
    let action = Agent.handleResponse responseMsg currentMessages

    match action with
    | Stop(content, nextMessages) ->
        Assert.Equal("I have completed the task.", content)
        let msg = Assert.Single nextMessages
        Assert.Equal("assistant", msg.role)
        Assert.Equal("I have completed the task.", msg.content)
    | Continue _ -> failwith "Expected Stop, but got Continue"

[<Fact>]
let ``Agent.handleResponse returns Continue action for assistant message with tool calls`` () =
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
    let action = Agent.handleResponse responseMsg currentMessages

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
