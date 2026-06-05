module CodingAgent.ToolsTests

open Xunit
open CodingAgent
open TestHelpers

let assertOk (result: Result<'a, string>) : 'a =
    match result with
    | Ok value -> value
    | Error err ->
        Assert.Fail $"Expected Ok, but got Error: {err}"
        Unchecked.defaultof<'a>

let assertError (result: Result<'a, string>) : string =
    match result with
    | Error msg -> msg
    | Ok _ ->
        Assert.Fail "Expected Error, but got Ok"
        ""

[<Fact>]
let ``readFile reads file within size limit`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "small_file.txt")

    mock.AddFile tempFile "hi"
    let result = Tools.readFile mock.FileSystem 10L tempFile
    Assert.Equal("hi", assertOk result)

[<Fact>]
let ``readFile rejects file exceeding size limit`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "large_file.txt")

    mock.AddFile tempFile "hello"
    let result = Tools.readFile mock.FileSystem 3L tempFile
    let msg = assertError result
    Assert.Contains("too large", msg)
    Assert.Contains("5 bytes", msg)

[<Fact>]
let ``readFile with maxFileSizeBytes 0 bypasses size limit`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "no_limit_file.txt")

    mock.AddFile tempFile "content"
    let result = Tools.readFile mock.FileSystem 0L tempFile
    Assert.Equal("content", assertOk result)

[<Fact>]
let ``readFile rejects non-existent file`` () =
    let mock = MockFileSystem()

    let nonExistentFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "non_existent.txt")

    let result = Tools.readFile mock.FileSystem 0L nonExistentFile
    Assert.Contains("not found", assertError result)

[<Fact>]
let ``readFile rejects empty path`` () =
    let mock = MockFileSystem()
    let readResult = Tools.readFile mock.FileSystem 0L ""
    Assert.Contains("not found", assertError readResult)

[<Fact>]
let ``readFile rejects path outside workspace`` () =
    let mock = MockFileSystem()
    let result = Tools.readFile mock.FileSystem 0L "/etc/passwd"
    Assert.Contains("Access denied", assertError result)

[<Fact>]
let ``writeFile writes file and readFile reads it back`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "test_file.txt")

    let writeContent = "Hello, F# Coding Agent!"
    let writeResult = Tools.writeFile mock.FileSystem 0L tempFile writeContent
    Assert.Contains("Successfully wrote to", assertOk writeResult)
    let readResult = Tools.readFile mock.FileSystem 0L tempFile
    Assert.Equal(writeContent, assertOk readResult)

[<Fact>]
let ``writeFile creates parent directories if they do not exist`` () =
    let mock = MockFileSystem()

    let tempParentDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "parent")

    let tempNestedFile =
        System.IO.Path.Combine(tempParentDir, "child_dir", "nested_file.txt")

    let writeResult = Tools.writeFile mock.FileSystem 0L tempNestedFile "nested content"
    Assert.Contains("Successfully wrote to", assertOk writeResult)
    let readResult = Tools.readFile mock.FileSystem 0L tempNestedFile
    Assert.Equal("nested content", assertOk readResult)

[<Fact>]
let ``writeFile writes empty content`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "empty_content.txt")

    let writeResult = Tools.writeFile mock.FileSystem 0L tempFile ""
    Assert.Contains("Successfully wrote to", assertOk writeResult)
    let readResult = Tools.readFile mock.FileSystem 0L tempFile
    Assert.Equal("", assertOk readResult)

[<Fact>]
let ``writeFile writes content with newlines`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "multiline.txt")

    let content = "line1\nline2\nline3"
    let writeResult = Tools.writeFile mock.FileSystem 0L tempFile content
    Assert.Contains("Successfully wrote to", assertOk writeResult)
    let readResult = Tools.readFile mock.FileSystem 0L tempFile
    Assert.Equal(content, assertOk readResult)

[<Fact>]
let ``writeFile writes content within size limit`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "small_write.txt")

    let result = Tools.writeFile mock.FileSystem 100L tempFile "hi"
    Assert.Contains("Successfully wrote to", assertOk result)

[<Fact>]
let ``writeFile rejects content exceeding size limit`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "large_write.txt")

    let result = Tools.writeFile mock.FileSystem 5L tempFile "hello world"
    let msg = assertError result
    Assert.Contains("too large", msg)
    Assert.Contains("11 bytes", msg)

[<Fact>]
let ``writeFile with maxFileSizeBytes 0 bypasses size limit`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "no_limit_write.txt")

    let content = String.replicate 1000 "x"
    let result = Tools.writeFile mock.FileSystem 0L tempFile content
    Assert.Contains("Successfully wrote to", assertOk result)

[<Fact>]
let ``writeFile rejects path outside workspace`` () =
    let mock = MockFileSystem()
    let writeResult = Tools.writeFile mock.FileSystem 0L "/etc/readonly.txt" "content"
    Assert.Contains("Access denied", assertError writeResult)

[<Fact>]
let ``runCommand executes simple echo command`` () =
    task {
        let mock = MockFileSystem()
        mock.AddDir System.Environment.CurrentDirectory

        let! result =
            Tools.runCommand
                mock.FileSystem
                1000000
                15000
                Sandbox.FallbackOnly
                System.Environment.CurrentDirectory
                "echo hello_from_test"
                ""

        match result with
        | Ok output -> Assert.Contains("hello_from_test", output)
        | Error err -> failwithf "Expected Ok, but got Error: %s" err
    }

[<Fact>]
let ``runCommand executes in custom working directory`` () =
    task {
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

            let! result =
                Tools.runCommand
                    mock.FileSystem
                    1000000
                    15000
                    Sandbox.FallbackOnly
                    System.Environment.CurrentDirectory
                    cmd
                    tempDir

            Assert.True((assertOk result).Length > 0)
        finally
            if System.IO.Directory.Exists tempDir then
                System.IO.Directory.Delete(tempDir, true)
    }

[<Fact>]
let ``runCommand truncates output when it exceeds maxOutputBytes`` () =
    task {
        let mock = MockFileSystem()
        mock.AddDir System.Environment.CurrentDirectory

        let! result =
            Tools.runCommand
                mock.FileSystem
                10
                15000
                Sandbox.FallbackOnly
                System.Environment.CurrentDirectory
                "echo 'abcdefghijklmnop'"
                ""

        let output = assertOk result
        Assert.Contains("truncated", output)
        Assert.DoesNotContain("lmnop", output)
    }

[<Fact>]
let ``runCommand truncates individual lines exceeding maxLineLength`` () =
    task {
        let mock = MockFileSystem()
        mock.AddDir System.Environment.CurrentDirectory

        let! result =
            Tools.runCommand
                mock.FileSystem
                (1024 * 1024)
                15000
                Sandbox.FallbackOnly
                System.Environment.CurrentDirectory
                "awk 'BEGIN{for(i=1;i<=200000;i++) printf \"a\"}'"
                ""

        let output = assertOk result
        Assert.Contains("[line truncated]", output)
    }

[<Fact>]
let ``runCommand fails on non-zero exit status`` () =
    task {
        let mock = MockFileSystem()
        mock.AddDir System.Environment.CurrentDirectory

        let! result =
            Tools.runCommand
                mock.FileSystem
                1000000
                15000
                Sandbox.FallbackOnly
                System.Environment.CurrentDirectory
                "exit 42"
                ""

        Assert.Contains("exited with code 42", assertError result)
    }

[<Fact>]
let ``runCommand fails on null command`` () =
    task {
        let mock = MockFileSystem()
        mock.AddDir System.Environment.CurrentDirectory

        let! result =
            Tools.runCommand
                mock.FileSystem
                1000000
                15000
                Sandbox.FallbackOnly
                System.Environment.CurrentDirectory
                null
                ""

        Assert.Contains("Failed operating", assertError result)
    }

[<Theory>]
[<InlineData("cd /", "dangerous pattern")>]
[<InlineData("rm -rf /", "dangerous pattern")>]
let ``runCommand blocks dangerous command`` (command: string, expectedSubstr: string) =
    task {
        let mock = MockFileSystem()
        mock.AddDir System.Environment.CurrentDirectory

        let! result =
            Tools.runCommand
                mock.FileSystem
                1000000
                15000
                Sandbox.FallbackOnly
                System.Environment.CurrentDirectory
                command
                ""

        Assert.Contains(expectedSubstr, assertError result)
    }

[<Fact>]
let ``listDirectory shows files and folders in directory`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "test_dir")

    let subDir = System.IO.Path.Combine(tempDir, "sub_folder")
    let tempFile = System.IO.Path.Combine(tempDir, "test_file.txt")

    mock.AddDir tempDir
    mock.AddDir subDir
    mock.AddFile tempFile "temp content"

    let result = Tools.listDirectory mock.FileSystem tempDir
    let msg = assertOk result
    Assert.Contains("Contents of directory", msg)
    Assert.Contains("[DIR]  sub_folder", msg)
    Assert.Contains("[FILE] test_file.txt", msg)

[<Fact>]
let ``listDirectory with empty directory shows header only`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "empty_dir")

    mock.AddDir tempDir
    let result = Tools.listDirectory mock.FileSystem tempDir
    let msg = assertOk result
    Assert.Contains("Contents of directory", msg)
    Assert.DoesNotContain("[DIR]", msg)
    Assert.DoesNotContain("[FILE]", msg)

[<Fact>]
let ``listDirectory with empty argument lists current directory`` () =
    let mock = MockFileSystem()
    mock.AddDir System.Environment.CurrentDirectory
    let result = Tools.listDirectory mock.FileSystem ""
    Assert.Contains("Contents of directory", assertOk result)

[<Fact>]
let ``listDirectory rejects non-existent directory`` () =
    let mock = MockFileSystem()

    let nonExistentDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "non_existent_dir")

    let result = Tools.listDirectory mock.FileSystem nonExistentDir
    Assert.Contains("not found", assertError result)

[<Fact>]
let ``listDirectory rejects path outside workspace`` () =
    let mock = MockFileSystem()
    let result = Tools.listDirectory mock.FileSystem "/etc/"
    Assert.Contains("Access denied", assertError result)

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
    let msg = assertOk result
    Assert.Contains("hello.txt", msg)
    Assert.Contains("TargetKeyword exists here", msg)
    Assert.DoesNotContain("ignored.txt", msg)

[<Fact>]
let ``grepSearch matches case-insensitively`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "grep_case")

    mock.AddDir tempDir
    mock.AddFile (System.IO.Path.Combine(tempDir, "test.txt")) "HELLO world\nGoodbye"
    let result = Tools.grepSearch mock.FileSystem "hello" tempDir
    let msg = assertOk result
    Assert.Contains("test.txt:1:", msg)
    Assert.Contains("HELLO world", msg)

[<Fact>]
let ``grepSearch reports no matches for absent query`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "grep_nomatch")

    mock.AddDir tempDir
    mock.AddFile (System.IO.Path.Combine(tempDir, "hello.txt")) "Hello world\nSome other text"

    let result =
        Tools.grepSearch mock.FileSystem "QueryThatWillNeverMatch_XYZ123" tempDir

    let msg = assertOk result
    Assert.Contains("No matches found for", msg)
    Assert.Contains("QueryThatWillNeverMatch_XYZ123", msg)

[<Fact>]
let ``grepSearch with no files returns no matches`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "grep_empty")

    mock.AddDir tempDir
    let result = Tools.grepSearch mock.FileSystem "anything" tempDir
    Assert.Contains("No matches found", assertOk result)

[<Fact>]
let ``grepSearch truncates results when more than 100 matches`` () =
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
    let msg = assertOk result
    Assert.Contains("showing first 100 of more than 100", msg)
    Assert.Contains("many_matches.txt:1:", msg)
    Assert.DoesNotContain("line 101", msg)

[<Fact>]
let ``grepSearch omits truncation notice when 100 or fewer matches`` () =
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
    let msg = assertOk result
    Assert.DoesNotContain("showing first", msg)
    Assert.DoesNotContain("more than", msg)
    Assert.Contains("fifty_matches.txt:50:", msg)

[<Fact>]
let ``grepSearch warns when files are unreadable`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "grep_warn")

    let subDir = System.IO.Path.Combine(tempDir, "src")
    mock.AddDir tempDir
    mock.AddDir subDir
    let goodFile = System.IO.Path.Combine(subDir, "good.txt")
    let badFile = System.IO.Path.Combine(subDir, "bad.txt")
    mock.AddFile goodFile "hello world\nmatch found"
    mock.AddFile badFile "match here too"

    let fs =
        { mock.FileSystem with
            readLines =
                fun path ->
                    if path = badFile then
                        failwith "Permission denied"
                    else
                        mock.FileSystem.readLines path }

    let result = Tools.grepSearch fs "match" tempDir
    let msg = assertOk result
    Assert.Contains("⚠️  Warning: Skipped unreadable file 'src/bad.txt'", msg)
    Assert.Contains("good.txt:2: match found", msg)

[<Fact>]
let ``grepSearch rejects non-existent directory`` () =
    let mock = MockFileSystem()

    let nonExistentDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "non_existent_dir")

    let result = Tools.grepSearch mock.FileSystem "test" nonExistentDir
    Assert.Contains("not found", assertError result)

[<Fact>]
let ``grepSearch rejects path outside workspace`` () =
    let mock = MockFileSystem()
    let result = Tools.grepSearch mock.FileSystem "test" "/etc/"
    Assert.Contains("Access denied", assertError result)

[<Fact>]
let ``patchFile replaces target content in file`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "patch_test.txt")

    mock.AddFile tempFile "original line 1\nold_block_to_replace\noriginal line 3"

    let result =
        Tools.patchFile mock.FileSystem tempFile "old_block_to_replace" "new_substituted_block"

    Assert.Contains("Successfully patched file", assertOk result)
    let updatedContent = mock.FileSystem.readFile tempFile
    Assert.Contains("new_substituted_block", updatedContent)
    Assert.DoesNotContain("old_block_to_replace", updatedContent)
    Assert.Contains("original line 1", updatedContent)

[<Fact>]
let ``patchFile with identical content succeeds as no-op`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "noop_patch.txt")

    mock.AddFile tempFile "some content"
    let result = Tools.patchFile mock.FileSystem tempFile "content" "content"
    Assert.Contains("Successfully patched", assertOk result)
    let readResult = Tools.readFile mock.FileSystem 0L tempFile
    Assert.Equal("some content", assertOk readResult)

[<Fact>]
let ``patchFile rejects absent target content`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "patch_test.txt")

    mock.AddFile tempFile "original line 1\noriginal line 2"
    let result = Tools.patchFile mock.FileSystem tempFile "missing_target" "replacement"
    Assert.Contains("not found", assertError result)

[<Fact>]
let ``patchFile rejects ambiguous target appearing multiple times`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "patch_test.txt")

    mock.AddFile tempFile "duplicate_target\nsome other line\nduplicate_target\nend"

    let result =
        Tools.patchFile mock.FileSystem tempFile "duplicate_target" "replacement"

    let msg = assertError result
    Assert.Contains("found 2 times", msg)
    Assert.Contains("must be unique", msg)

[<Fact>]
let ``patchFile rejects non-existent file`` () =
    let mock = MockFileSystem()

    let nonExistentFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "non_existent.txt")

    let result = Tools.patchFile mock.FileSystem nonExistentFile "target" "replacement"
    Assert.Contains("not found", assertError result)

[<Fact>]
let ``patchFile rejects path outside workspace`` () =
    let mock = MockFileSystem()
    let result = Tools.patchFile mock.FileSystem "/etc/missing" "target" "replacement"
    Assert.Contains("Access denied", assertError result)

[<Fact>]
let ``readFileLines returns specified line range`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "read_lines_test.txt")

    mock.AddFile tempFile "line1\nline2\nline3\nline4\nline5"
    let result = Tools.readFileLines mock.FileSystem 0L tempFile 2 4
    Assert.Equal("line2\nline3\nline4", assertOk result)

[<Fact>]
let ``readFileLines rejects negative startLine`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "read_lines_test.txt")

    mock.AddFile tempFile "line1\nline2\nline3"
    let result = Tools.readFileLines mock.FileSystem 0L tempFile -5 10
    Assert.Contains("start_line must be >= 1", assertError result)

[<Fact>]
let ``readFileLines rejects negative endLine`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "read_lines_test.txt")

    mock.AddFile tempFile "line1\nline2\nline3"
    let result = Tools.readFileLines mock.FileSystem 0L tempFile 1 -1
    Assert.Contains("end_line must be >= 1", assertError result)

[<Fact>]
let ``readFileLines rejects startLine larger than endLine`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "read_lines_test.txt")

    mock.AddFile tempFile "line1\nline2"
    let result = Tools.readFileLines mock.FileSystem 0L tempFile 5 2
    Assert.Contains("cannot be greater than", assertError result)

[<Fact>]
let ``readFileLines returns empty string when startLine exceeds file length`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "short_file.txt")

    mock.AddFile tempFile "line1\nline2\nline3"
    let result = Tools.readFileLines mock.FileSystem 0L tempFile 10 20
    Assert.Equal("", assertOk result)

[<Fact>]
let ``readFileLines with maxFileSizeBytes 0 bypasses size limit`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "bypass_lines.txt")

    mock.AddFile tempFile "line1\nline2\nline3"
    let result = Tools.readFileLines mock.FileSystem 0L tempFile 1 3
    Assert.Equal("line1\nline2\nline3", assertOk result)

[<Fact>]
let ``readFileLines rejects file exceeding size limit`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "large_readlines.txt")

    mock.AddFile tempFile "line1\nline2\nline3"
    let result = Tools.readFileLines mock.FileSystem 10L tempFile 1 3
    let msg = assertError result
    Assert.Contains("too large", msg)
    Assert.Contains("17 bytes", msg)

[<Fact>]
let ``readFileLines rejects non-existent file`` () =
    let mock = MockFileSystem()

    let nonExistentFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "non_existent.txt")

    let result = Tools.readFileLines mock.FileSystem 0L nonExistentFile 1 5
    Assert.Contains("not found", assertError result)

[<Fact>]
let ``readFileLines rejects path outside workspace`` () =
    let mock = MockFileSystem()
    let result = Tools.readFileLines mock.FileSystem 0L "/etc/passwd" 1 5
    Assert.Contains("Access denied", assertError result)

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
    let msg = assertOk result
    Assert.Contains("target_file.txt", msg)
    Assert.Contains("src/target_file.txt", msg)
    Assert.DoesNotContain("bin/target_file.txt", msg)
    Assert.DoesNotContain("other.log", msg)

[<Fact>]
let ``findFiles reports no files matching pattern`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "find_test")

    mock.AddDir tempDir
    let result = Tools.findFiles mock.FileSystem "*.nonexistent" tempDir
    Assert.Contains("No files matching pattern", assertOk result)

[<Fact>]
let ``findFiles truncates results at 100 with warning`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "find_truncate")

    mock.AddDir tempDir

    for i in 1..110 do
        mock.AddFile (System.IO.Path.Combine(tempDir, sprintf "file%d.txt" i)) "content"

    let result = Tools.findFiles mock.FileSystem "*.txt" tempDir
    let msg = assertOk result
    Assert.Contains("showing first 100", msg)
    Assert.Contains("more than 100", msg)

[<Fact>]
let ``findFiles rejects non-existent directory`` () =
    let mock = MockFileSystem()

    let nonExistentDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "non_existent_dir")

    let result = Tools.findFiles mock.FileSystem "*.fs" nonExistentDir
    Assert.Contains("not found", assertError result)

[<Fact>]
let ``findFiles rejects path outside workspace`` () =
    let mock = MockFileSystem()
    let result = Tools.findFiles mock.FileSystem "*.txt" "/etc/"
    Assert.Contains("Access denied", assertError result)
