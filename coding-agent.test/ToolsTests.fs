module CodingAgent.ToolsTests

open Xunit
open CodingAgent
open TestHelpers

[<Fact>]
let ``writeFile writes file successfully and readFile reads it back`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "test_file.txt")

    let writeContent = "Hello, F# Coding Agent!"
    let writeResult = Tools.writeFile mock.FileSystem 0L tempFile writeContent

    match writeResult with
    | Ok msg -> Assert.Contains("Successfully wrote to", msg)
    | Error err -> failwithf "Expected Ok, but got Error: %s" err

    let readResult = Tools.readFile mock.FileSystem 0L tempFile

    match readResult with
    | Ok content -> Assert.Equal(writeContent, content)
    | Error err -> failwithf "Expected Ok, but got Error: %s" err

[<Fact>]
let ``writeFile creates parent directories if they do not exist`` () =
    let mock = MockFileSystem()

    let tempParentDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "parent")

    let tempNestedFile =
        System.IO.Path.Combine(tempParentDir, "child_dir", "nested_file.txt")

    let writeResult = Tools.writeFile mock.FileSystem 0L tempNestedFile "nested content"

    match writeResult with
    | Ok msg -> Assert.Contains("Successfully wrote to", msg)
    | Error err -> failwithf "Expected Ok, but got Error: %s" err

    let readResult = Tools.readFile mock.FileSystem 0L tempNestedFile

    match readResult with
    | Ok content -> Assert.Equal("nested content", content)
    | Error err -> failwithf "Expected Ok, but got Error: %s" err

[<Fact>]
let ``readFile succeeds when file is within maxFileSizeBytes`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "small_file.txt")

    mock.AddFile tempFile "hi"
    let result = Tools.readFile mock.FileSystem 10L tempFile

    match result with
    | Ok content -> Assert.Equal("hi", content)
    | Error err -> failwithf "Expected Ok, but got Error: %s" err

[<Fact>]
let ``readFile returns Error when file exceeds maxFileSizeBytes`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "large_file.txt")

    mock.AddFile tempFile "hello"
    let result = Tools.readFile mock.FileSystem 3L tempFile

    match result with
    | Error msg ->
        Assert.Contains("too large", msg)
        Assert.Contains("5 bytes", msg)
    | Ok _ -> failwith "Expected Error, but got Ok"

[<Fact>]
let ``readFile with maxFileSizeBytes 0 means no limit`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "no_limit_file.txt")

    mock.AddFile tempFile "content"
    let result = Tools.readFile mock.FileSystem 0L tempFile

    match result with
    | Ok content -> Assert.Equal("content", content)
    | Error err -> failwithf "Expected Ok, but got Error: %s" err

[<Fact>]
let ``readFile returns Error for non-existent file`` () =
    let mock = MockFileSystem()

    let nonExistentFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "non_existent.txt")

    let result = Tools.readFile mock.FileSystem 0L nonExistentFile

    match result with
    | Error msg -> Assert.Contains("not found", msg)
    | Ok _ -> failwith "Expected Error, but got Ok"

[<Fact>]
let ``readFile returns Error on empty path`` () =
    let mock = MockFileSystem()
    let readResult = Tools.readFile mock.FileSystem 0L ""

    match readResult with
    | Error msg -> Assert.Contains("not found", msg)
    | Ok _ -> failwith "Expected Error, but got Ok"

[<Fact>]
let ``readFile returns Error for path outside workspace`` () =
    let mock = MockFileSystem()
    let result = Tools.readFile mock.FileSystem 0L "/etc/passwd"

    match result with
    | Error msg -> Assert.Contains("Access denied", msg)
    | Ok _ -> failwith "Expected Error, but got Ok"

[<Fact>]
let ``writeFile succeeds when content is within maxFileSizeBytes`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "small_write.txt")

    let result = Tools.writeFile mock.FileSystem 100L tempFile "hi"

    match result with
    | Ok msg -> Assert.Contains("Successfully wrote to", msg)
    | Error err -> failwithf "Expected Ok, but got Error: %s" err

[<Fact>]
let ``writeFile returns Error when content exceeds maxFileSizeBytes`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "large_write.txt")

    let result = Tools.writeFile mock.FileSystem 5L tempFile "hello world"

    match result with
    | Error msg ->
        Assert.Contains("too large", msg)
        Assert.Contains("11 bytes", msg)
    | Ok _ -> failwith "Expected Error, but got Ok"

[<Fact>]
let ``writeFile with maxFileSizeBytes 0 means no limit`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "no_limit_write.txt")

    let content = String.replicate 1000 "x"
    let result = Tools.writeFile mock.FileSystem 0L tempFile content

    match result with
    | Ok msg -> Assert.Contains("Successfully wrote to", msg)
    | Error err -> failwithf "Expected Ok, but got Error: %s" err

[<Fact>]
let ``writeFile returns Error on invalid path`` () =
    let mock = MockFileSystem()
    let writeResult = Tools.writeFile mock.FileSystem 0L "/etc/readonly.txt" "content"

    match writeResult with
    | Error msg -> Assert.Contains("Access denied", msg)
    | Ok _ -> failwith "Expected Error, but got Ok"

[<Fact>]
let ``runCommand executes echo command successfully`` () =
    let mock = MockFileSystem()
    mock.AddDir System.Environment.CurrentDirectory
    let result = Tools.runCommand mock.FileSystem 15000 "echo hello_from_test" ""

    match result with
    | Ok output -> Assert.Contains("hello_from_test", output)
    | Error err -> failwithf "Expected Ok, but got Error: %s" err

[<Fact>]
let ``runCommand executes in custom working directory`` () =
    let mock = MockFileSystem()
    let tempDir = System.IO.Path.Combine(System.Environment.CurrentDirectory, "cmd_dir")
    // runCommand spawns a real process, so we need a real directory
    System.IO.Directory.CreateDirectory tempDir |> ignore

    try
        mock.AddDir tempDir

        let isWindows =
            System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform
                System.Runtime.InteropServices.OSPlatform.Windows

        let cmd = if isWindows then "cd" else "pwd"
        let result = Tools.runCommand mock.FileSystem 15000 cmd tempDir

        match result with
        | Ok output -> Assert.True(output.Length > 0)
        | Error err -> failwithf "Expected Ok, but got Error: %s" err
    finally
        if System.IO.Directory.Exists tempDir then
            System.IO.Directory.Delete(tempDir, true)

[<Fact>]
let ``runCommand returns Error for non-zero exit code`` () =
    let mock = MockFileSystem()
    mock.AddDir System.Environment.CurrentDirectory
    let result = Tools.runCommand mock.FileSystem 15000 "exit 42" ""

    match result with
    | Error msg -> Assert.Contains("exited with code 42", msg)
    | Ok output -> failwithf "Expected Error, but got Ok with: %s" output

[<Fact>]
let ``runCommand returns Error when command execution fails with exception`` () =
    let mock = MockFileSystem()
    mock.AddDir System.Environment.CurrentDirectory
    let result = Tools.runCommand mock.FileSystem 15000 null ""

    match result with
    | Error msg -> Assert.Contains("Failed operating", msg)
    | Ok output -> failwithf "Expected Error, but got Ok with: %s" output

[<Fact>]
let ``runCommand returns Error for dangerous cd / command`` () =
    let mock = MockFileSystem()
    mock.AddDir System.Environment.CurrentDirectory
    let result = Tools.runCommand mock.FileSystem 15000 "cd /" ""

    match result with
    | Error msg -> Assert.Contains("dangerous pattern", msg)
    | Ok _ -> failwith "Expected Error, but got Ok"

[<Fact>]
let ``runCommand returns Error for dangerous rm -rf / command`` () =
    let mock = MockFileSystem()
    mock.AddDir System.Environment.CurrentDirectory
    let result = Tools.runCommand mock.FileSystem 15000 "rm -rf /" ""

    match result with
    | Error msg -> Assert.Contains("potentially dangerous", msg)
    | Ok _ -> failwith "Expected Error, but got Ok"

[<Fact>]
let ``listDirectory lists files and folders correctly`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "test_dir")

    let subDir = System.IO.Path.Combine(tempDir, "sub_folder")
    let tempFile = System.IO.Path.Combine(tempDir, "test_file.txt")

    mock.AddDir tempDir
    mock.AddDir subDir
    mock.AddFile tempFile "temp content"

    let result = Tools.listDirectory mock.FileSystem tempDir

    match result with
    | Ok msg ->
        Assert.Contains("Contents of directory", msg)
        Assert.Contains("[DIR]  sub_folder", msg)
        Assert.Contains("[FILE] test_file.txt", msg)
    | Error err -> failwithf "Expected Ok, but got Error: %s" err

[<Fact>]
let ``listDirectory returns Error for non-existent directory`` () =
    let mock = MockFileSystem()

    let nonExistentDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "non_existent_dir")

    let result = Tools.listDirectory mock.FileSystem nonExistentDir

    match result with
    | Error msg -> Assert.Contains("not found", msg)
    | Ok _ -> failwith "Expected Error, but got Ok"

[<Fact>]
let ``listDirectory with empty argument lists current directory`` () =
    let mock = MockFileSystem()
    mock.AddDir System.Environment.CurrentDirectory
    let result = Tools.listDirectory mock.FileSystem ""

    match result with
    | Ok msg -> Assert.Contains("Contents of directory", msg)
    | Error err -> failwithf "Expected Ok, but got Error: %s" err

[<Fact>]
let ``grepSearch finds matches and ignores build/git folders`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "grep_test")

    let subDirClean = System.IO.Path.Combine(tempDir, "src")
    let subDirBin = System.IO.Path.Combine(tempDir, "bin")
    mock.AddDir tempDir
    mock.AddDir subDirClean
    mock.AddDir subDirBin

    mock.AddFile
        (System.IO.Path.Combine(subDirClean, "hello.txt"))
        "Hello world line 1\nTargetKeyword exists here\nSome other text"

    mock.AddFile (System.IO.Path.Combine(subDirBin, "ignored.txt")) "TargetKeyword exists here too but in bin folder"
    let result = Tools.grepSearch mock.FileSystem "TargetKeyword" tempDir

    match result with
    | Ok msg ->
        Assert.Contains("hello.txt", msg)
        Assert.Contains("TargetKeyword exists here", msg)
        Assert.DoesNotContain("ignored.txt", msg)
    | Error err -> failwithf "Expected Ok, but got Error: %s" err

[<Fact>]
let ``grepSearch returns no matches message when query is not found`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "grep_nomatch")

    mock.AddDir tempDir
    mock.AddFile (System.IO.Path.Combine(tempDir, "hello.txt")) "Hello world\nSome other text"

    let result =
        Tools.grepSearch mock.FileSystem "QueryThatWillNeverMatch_XYZ123" tempDir

    match result with
    | Ok msg ->
        Assert.Contains("No matches found for", msg)
        Assert.Contains("QueryThatWillNeverMatch_XYZ123", msg)
    | Error err -> failwithf "Expected Ok, but got Error: %s" err

[<Fact>]
let ``grepSearch notifies when more than 100 matches are truncated`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "grep_truncated")

    mock.AddDir tempDir

    let manyLines =
        [| 1..101 |]
        |> Array.map (fun i -> sprintf "TargetKeyword line %d" i)
        |> String.concat "\n"

    mock.AddFile (System.IO.Path.Combine(tempDir, "many_matches.txt")) manyLines
    let result = Tools.grepSearch mock.FileSystem "TargetKeyword" tempDir

    match result with
    | Ok msg ->
        Assert.Contains("showing first 100 of more than 100", msg)
        Assert.Contains("many_matches.txt:1:", msg)
        Assert.DoesNotContain("line 101", msg)
    | Error err -> failwithf "Expected Ok, but got Error: %s" err

[<Fact>]
let ``grepSearch does not show truncation message when 100 or fewer matches`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "grep_full")

    mock.AddDir tempDir

    let fiftyLines =
        [| 1..50 |]
        |> Array.map (fun i -> sprintf "TargetKeyword match %d" i)
        |> String.concat "\n"

    mock.AddFile (System.IO.Path.Combine(tempDir, "fifty_matches.txt")) fiftyLines
    let result = Tools.grepSearch mock.FileSystem "TargetKeyword" tempDir

    match result with
    | Ok msg ->
        Assert.DoesNotContain("showing first", msg)
        Assert.DoesNotContain("more than", msg)
        Assert.Contains("fifty_matches.txt:50:", msg)
    | Error err -> failwithf "Expected Ok, but got Error: %s" err

[<Fact>]
let ``grepSearch returns Error for non-existent directory`` () =
    let mock = MockFileSystem()

    let nonExistentDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "non_existent_dir")

    let result = Tools.grepSearch mock.FileSystem "test" nonExistentDir

    match result with
    | Error msg -> Assert.Contains("not found", msg)
    | Ok _ -> failwith "Expected Error, but got Ok"

[<Fact>]
let ``patchFile successfully replaces target content`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "patch_test.txt")

    mock.AddFile tempFile "original line 1\nold_block_to_replace\noriginal line 3"

    let result =
        Tools.patchFile mock.FileSystem tempFile "old_block_to_replace" "new_substituted_block"

    match result with
    | Ok msg ->
        Assert.Contains("Successfully patched file", msg)
        let updatedContent = mock.FileSystem.readFile tempFile
        Assert.Contains("new_substituted_block", updatedContent)
        Assert.DoesNotContain("old_block_to_replace", updatedContent)
        Assert.Contains("original line 1", updatedContent)
    | Error err -> failwithf "Expected Ok, but got Error: %s" err

[<Fact>]
let ``patchFile returns Error if target content is not found`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "patch_test.txt")

    mock.AddFile tempFile "original line 1\noriginal line 2"
    let result = Tools.patchFile mock.FileSystem tempFile "missing_target" "replacement"

    match result with
    | Error msg -> Assert.Contains("not found", msg)
    | Ok _ -> failwith "Expected Error, but got Ok"

[<Fact>]
let ``patchFile returns Error when target appears multiple times`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "patch_test.txt")

    mock.AddFile tempFile "duplicate_target\nsome other line\nduplicate_target\nend"

    let result =
        Tools.patchFile mock.FileSystem tempFile "duplicate_target" "replacement"

    match result with
    | Error msg ->
        Assert.Contains("found 2 times", msg)
        Assert.Contains("must be unique", msg)
    | Ok _ -> failwith "Expected Error, but got Ok"

[<Fact>]
let ``patchFile returns Error if file does not exist`` () =
    let mock = MockFileSystem()

    let nonExistentFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "non_existent.txt")

    let result = Tools.patchFile mock.FileSystem nonExistentFile "target" "replacement"

    match result with
    | Error msg -> Assert.Contains("not found", msg)
    | Ok _ -> failwith "Expected Error, but got Ok"

[<Fact>]
let ``patchFile returns Error for path outside workspace`` () =
    let mock = MockFileSystem()
    let result = Tools.patchFile mock.FileSystem "/etc/missing" "target" "replacement"

    match result with
    | Error msg -> Assert.Contains("Access denied", msg)
    | Ok _ -> failwith "Expected Error, but got Ok"

[<Fact>]
let ``readFileLines returns correct line range`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "read_lines_test.txt")

    mock.AddFile tempFile "line1\nline2\nline3\nline4\nline5"
    let result = Tools.readFileLines mock.FileSystem 0L tempFile 2 4

    match result with
    | Ok content -> Assert.Equal("line2\nline3\nline4", content)
    | Error err -> failwithf "Expected Ok, but got Error: %s" err

[<Fact>]
let ``readFileLines returns Error for negative startLine`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "read_lines_test.txt")

    mock.AddFile tempFile "line1\nline2\nline3"
    let result = Tools.readFileLines mock.FileSystem 0L tempFile -5 10

    match result with
    | Error msg -> Assert.Contains("start_line must be >= 1", msg)
    | Ok _ -> failwithf "Expected Error, but got Ok"

[<Fact>]
let ``readFileLines returns Error for negative endLine`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "read_lines_test.txt")

    mock.AddFile tempFile "line1\nline2\nline3"
    let result = Tools.readFileLines mock.FileSystem 0L tempFile 1 -1

    match result with
    | Error msg -> Assert.Contains("end_line must be >= 1", msg)
    | Ok _ -> failwithf "Expected Error, but got Ok"

[<Fact>]
let ``readFileLines returns Error if startLine is greater than endLine`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "read_lines_test.txt")

    mock.AddFile tempFile "line1\nline2"
    let result = Tools.readFileLines mock.FileSystem 0L tempFile 5 2

    match result with
    | Error msg -> Assert.Contains("cannot be greater than", msg)
    | Ok _ -> failwith "Expected Error, but got Ok"

[<Fact>]
let ``readFileLines succeeds when file is within maxFileSizeBytes`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "small_lines.txt")

    mock.AddFile tempFile "line1\nline2\nline3"
    let result = Tools.readFileLines mock.FileSystem 100L tempFile 1 2

    match result with
    | Ok content -> Assert.Equal("line1\nline2", content)
    | Error err -> failwithf "Expected Ok, but got Error: %s" err

[<Fact>]
let ``readFileLines returns Error when file exceeds maxFileSizeBytes`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "large_lines.txt")

    mock.AddFile tempFile "line1\nline2\nline3"
    let result = Tools.readFileLines mock.FileSystem 10L tempFile 1 2

    match result with
    | Error msg -> Assert.Contains("too large", msg)
    | Ok _ -> failwith "Expected Error, but got Ok"

[<Fact>]
let ``readFileLines returns Error if file does not exist`` () =
    let mock = MockFileSystem()

    let nonExistentFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "non_existent.txt")

    let result = Tools.readFileLines mock.FileSystem 0L nonExistentFile 1 5

    match result with
    | Error msg -> Assert.Contains("not found", msg)
    | Ok _ -> failwith "Expected Error, but got Ok"

[<Fact>]
let ``findFiles finds matching files recursively and ignores build folders`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "find_test")

    let srcDir = System.IO.Path.Combine(tempDir, "src")
    let binDir = System.IO.Path.Combine(tempDir, "bin")
    mock.AddDir tempDir
    mock.AddDir srcDir
    mock.AddDir binDir
    mock.AddFile (System.IO.Path.Combine(srcDir, "target_file.txt")) "hello"
    mock.AddFile (System.IO.Path.Combine(binDir, "target_file.txt")) "ignored"
    mock.AddFile (System.IO.Path.Combine(srcDir, "other.log")) "log"
    let result = Tools.findFiles mock.FileSystem "*target*" tempDir

    match result with
    | Ok msg ->
        Assert.Contains("target_file.txt", msg)
        Assert.Contains("src/target_file.txt", msg)
        Assert.DoesNotContain("bin/target_file.txt", msg)
        Assert.DoesNotContain("other.log", msg)
    | Error err -> failwithf "Expected Ok, but got Error: %s" err

[<Fact>]
let ``findFiles returns message when no files match`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "find_test")

    mock.AddDir tempDir
    let result = Tools.findFiles mock.FileSystem "*.nonexistent" tempDir

    match result with
    | Ok msg -> Assert.Contains("No files matching pattern", msg)
    | Error err -> failwithf "Expected Ok, but got Error: %s" err

[<Fact>]
let ``findFiles truncates results at 100 and shows warning`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "find_truncate")

    mock.AddDir tempDir

    for i in 1..110 do
        mock.AddFile (System.IO.Path.Combine(tempDir, sprintf "file%d.txt" i)) "content"

    let result = Tools.findFiles mock.FileSystem "*.txt" tempDir

    match result with
    | Ok msg ->
        Assert.Contains("showing first 100", msg)
        Assert.Contains("more than 100", msg)
    | Error err -> failwithf "Expected Ok, but got Error: %s" err

[<Fact>]
let ``findFiles returns Error if directory does not exist`` () =
    let mock = MockFileSystem()

    let nonExistentDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "non_existent_dir")

    let result = Tools.findFiles mock.FileSystem "*.fs" nonExistentDir

    match result with
    | Error msg -> Assert.Contains("not found", msg)
    | Ok _ -> failwith "Expected Error, but got Ok"
