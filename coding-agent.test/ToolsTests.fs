module CodingAgent.ToolsTests

open System
open System.IO
open Xunit
open CodingAgent

[<Fact>]
let ``writeFile writes file successfully and readFile reads it back`` () =
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
let ``readFile returns Error for non-existent file`` () =
    let nonExistentFile =
        Path.Combine(Path.GetTempPath(), sprintf "non_existent_%s.txt" (Guid.NewGuid().ToString()))

    let result = Tools.readFile nonExistentFile

    match result with
    | Ok _ -> failwith "Expected Error, but got Ok"
    | Error msg -> Assert.Contains("not found", msg)

[<Fact>]
let ``listDirectory lists files and folders correctly`` () =
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
