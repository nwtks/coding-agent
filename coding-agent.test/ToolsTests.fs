module CodingAgent.ToolsTests

open Xunit
open CodingAgent

[<Fact>]
let ``readFile returns Error for non-existent file`` () =
    let nonExistentFile =
        System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            sprintf "non_existent_%s.txt" (System.Guid.NewGuid().ToString())
        )

    let result = Tools.readFile nonExistentFile

    match result with
    | Ok _ -> failwith "Expected Error, but got Ok"
    | Error msg -> Assert.Contains("not found", msg)

[<Fact>]
let ``readFile returns Error on empty path`` () =
    let readResult = Tools.readFile ""

    match readResult with
    | Error msg -> Assert.Contains("not found", msg)
    | Ok _ -> failwith "Expected Error, but got Ok"

[<Fact>]
let ``writeFile writes file successfully and readFile reads it back`` () =
    let tempFile =
        System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            sprintf "test_file_%s.txt" (System.Guid.NewGuid().ToString())
        )

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
        if System.IO.File.Exists tempFile then
            System.IO.File.Delete tempFile

[<Fact>]
let ``writeFile creates parent directories if they do not exist`` () =
    let tempParentDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), sprintf "parent_%s" (System.Guid.NewGuid().ToString()))

    let tempNestedFile =
        System.IO.Path.Combine(tempParentDir, "child_dir", "nested_file.txt")

    try
        let writeResult = Tools.writeFile tempNestedFile "nested content"

        match writeResult with
        | Ok msg -> Assert.Contains("Successfully wrote to", msg)
        | Error err -> failwithf "Expected Ok, but got Error: %s" err

        let readResult = Tools.readFile tempNestedFile

        match readResult with
        | Ok content -> Assert.Equal("nested content", content)
        | Error err -> failwithf "Expected Ok, but got Error: %s" err
    finally
        if System.IO.Directory.Exists tempParentDir then
            System.IO.Directory.Delete(tempParentDir, true)

[<Fact>]
let ``writeFile returns Error on invalid path`` () =
    let writeResult = Tools.writeFile "" "content"

    match writeResult with
    | Error msg -> Assert.Contains("Error writing to file", msg)
    | Ok _ -> failwith "Expected Error, but got Ok"

[<Fact>]
let ``runCommand executes echo command successfully`` () =
    let result = Tools.runCommand "echo hello_from_test" ""

    match result with
    | Ok output -> Assert.Contains("hello_from_test", output)
    | Error err -> failwithf "Expected Ok, but got Error: %s" err

[<Fact>]
let ``runCommand executes in custom working directory`` () =
    let tempDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), sprintf "cmd_dir_%s" (System.Guid.NewGuid().ToString()))

    System.IO.Directory.CreateDirectory tempDir |> ignore

    try
        let isWindows =
            System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform
                System.Runtime.InteropServices.OSPlatform.Windows

        let cmd = if isWindows then "cd" else "pwd"
        let result = Tools.runCommand cmd tempDir

        match result with
        | Ok output ->
            let dirName = System.IO.Path.GetFileName tempDir
            Assert.Contains(dirName, output)
        | Error err -> failwithf "Expected Ok, but got Error: %s" err
    finally
        System.IO.Directory.Delete(tempDir, true)

[<Fact>]
let ``runCommand returns Error for non-zero exit code`` () =
    let result = Tools.runCommand "exit 42" ""

    match result with
    | Error msg -> Assert.Contains("exited with code 42", msg)
    | Ok output -> failwithf "Expected Error, but got Ok with: %s" output

[<Fact>]
let ``runCommand returns Error when command execution fails with exception`` () =
    let result = Tools.runCommand null ""

    match result with
    | Error msg -> Assert.Contains("Error executing command", msg)
    | Ok output -> failwithf "Expected Error, but got Ok with: %s" output

[<Fact>]
let ``listDirectory lists files and folders correctly`` () =
    let tempDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), sprintf "test_dir_%s" (System.Guid.NewGuid().ToString()))

    let subDir = System.IO.Path.Combine(tempDir, "sub_folder")
    let tempFile = System.IO.Path.Combine(tempDir, "test_file.txt")

    try
        System.IO.Directory.CreateDirectory tempDir |> ignore
        System.IO.Directory.CreateDirectory subDir |> ignore
        System.IO.File.WriteAllText(tempFile, "temp content")
        let result = Tools.listDirectory tempDir

        match result with
        | Ok msg ->
            Assert.Contains("Contents of directory", msg)
            Assert.Contains("[DIR]  sub_folder", msg)
            Assert.Contains("[FILE] test_file.txt (12 bytes)", msg)
        | Error err -> failwithf "Expected Ok, but got Error: %s" err
    finally
        if System.IO.Directory.Exists tempDir then
            System.IO.Directory.Delete(tempDir, true)

[<Fact>]
let ``listDirectory returns Error for non-existent directory`` () =
    let nonExistentDir =
        System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            sprintf "non_existent_dir_%s" (System.Guid.NewGuid().ToString())
        )

    let result = Tools.listDirectory nonExistentDir

    match result with
    | Error msg -> Assert.Contains("not found", msg)
    | Ok _ -> failwith "Expected Error, but got Ok"

[<Fact>]
let ``listDirectory with empty argument lists current directory`` () =
    let result = Tools.listDirectory ""

    match result with
    | Ok msg -> Assert.Contains("Contents of directory", msg)
    | Error err -> failwithf "Expected Ok, but got Error: %s" err

[<Fact>]
let ``grepSearch finds matches and ignores build/git folders`` () =
    let tempDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), sprintf "grep_test_%s" (System.Guid.NewGuid().ToString()))

    let subDirClean = System.IO.Path.Combine(tempDir, "src")
    let subDirBin = System.IO.Path.Combine(tempDir, "bin")

    try
        System.IO.Directory.CreateDirectory subDirClean |> ignore
        System.IO.Directory.CreateDirectory subDirBin |> ignore

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(subDirClean, "hello.txt"),
            "Hello world line 1\nTargetKeyword exists here\nSome other text"
        )

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(subDirBin, "ignored.txt"),
            "TargetKeyword exists here too but in bin folder"
        )

        let result = Tools.grepSearch "TargetKeyword" tempDir

        match result with
        | Ok msg ->
            Assert.Contains("hello.txt", msg)
            Assert.Contains("TargetKeyword exists here", msg)
            Assert.DoesNotContain("ignored.txt", msg)
        | Error err -> failwithf "Expected Ok, but got Error: %s" err
    finally
        if System.IO.Directory.Exists tempDir then
            System.IO.Directory.Delete(tempDir, true)

[<Fact>]
let ``grepSearch returns Error for non-existent directory`` () =
    let result = Tools.grepSearch "test" "/definitely/does/not/exist/folder"

    match result with
    | Error msg -> Assert.Contains("not found", msg)
    | Ok _ -> failwith "Expected Error, but got Ok"
