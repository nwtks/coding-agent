module CodingAgent.ToolsTests

open Xunit
open CodingAgent
open TestHelpers

[<Theory>]
[<InlineData("inside", "/home/test/test.txt")>]
[<InlineData("outside", "/etc/passwd")>]
let ``resolvePathInWorkspace returns Ok for paths inside workspace and Error for outside``
    (scenario: string, path: string)
    =
    let mock = MockFileSystem()
    let result = Tools.resolvePathInWorkspace mock.FileSystem path

    match scenario with
    | "inside" -> Assert.Equal(path, assertOk result)
    | "outside" ->
        let msg = assertError result
        Assert.Contains("Access denied", msg)
        Assert.Contains("outside the workspace", msg)
    | _ -> failwith "unknown scenario"

[<Theory>]
[<InlineData(true, false)>]
[<InlineData(false, false)>]
[<InlineData(false, true)>]
let ``withExistingFile handles file existence and workspace boundary checks`` (addFile: bool, isOutside: bool) =
    let mock = MockFileSystem()

    let path =
        if isOutside then
            "/etc/passwd"
        else
            System.IO.Path.Combine(System.Environment.CurrentDirectory, "test.txt")

    if addFile then
        mock.AddFile path "content"

    let result = Tools.withExistingFile mock.FileSystem path (fun _ _ -> Ok "ok")

    if addFile then
        Assert.Equal("ok", assertOk result)
    elif isOutside then
        Assert.Contains("Access denied", assertError result)
    else
        Assert.Contains("not found", assertError result)

[<Fact>]
let ``withExistingFile catches exceptions and returns Error`` () =
    let mock = MockFileSystem()
    let path = System.IO.Path.Combine(System.Environment.CurrentDirectory, "error.txt")
    mock.AddFile path "content"

    let result =
        Tools.withExistingFile mock.FileSystem path (fun _ _ -> raise (new System.InvalidOperationException "Boom"))

    Assert.Contains("Failed operating", assertError result)

[<Theory>]
[<InlineData(true, false)>]
[<InlineData(false, false)>]
[<InlineData(false, true)>]
let ``withExistingDir handles directory existence and workspace boundary checks`` (addDir: bool, isOutside: bool) =
    let mock = MockFileSystem()

    let dir =
        if isOutside then
            "/etc/"
        else
            System.IO.Path.Combine(System.Environment.CurrentDirectory, "test_dir")

    if addDir then
        mock.AddDir dir

    let result = Tools.withExistingDir mock.FileSystem dir (fun _ _ -> Ok "ok")

    if addDir then
        Assert.Equal("ok", assertOk result)
    elif isOutside then
        Assert.Contains("Access denied", assertError result)
    else
        Assert.Contains("not found", assertError result)

[<Fact>]
let ``withExistingDir catches exceptions and returns Error`` () =
    let mock = MockFileSystem()
    let dir = System.IO.Path.Combine(System.Environment.CurrentDirectory, "error_dir")
    mock.AddDir dir

    let result =
        Tools.withExistingDir mock.FileSystem dir (fun _ _ -> raise (new System.InvalidOperationException "Boom"))

    Assert.Contains("Failed operating", assertError result)

[<Theory>]
[<InlineData("hi", 10L, true, "")>]
[<InlineData("hello world", 5L, false, "too large")>]
[<InlineData("some content", 0L, true, "")>]
let ``checkFileSize validates file size limits``
    (content: string, maxSize: int64, expectOk: bool, expectedMsg: string)
    =
    let mock = MockFileSystem()

    let path =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "size_test.txt")

    mock.AddFile path content
    let result = Tools.checkFileSize mock.FileSystem path maxSize

    if expectOk then
        Assert.True(Result.isOk result)
    else
        Assert.Contains(expectedMsg, assertError result)

[<Fact>]
let ``readFile reads file within size limit`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "small_file.txt")

    mock.AddFile tempFile "hi"
    let result = Tools.readFile mock.FileSystem 10L tempFile
    Assert.Equal("hi", assertOk result)

[<Fact>]
let ``readFile returns Error when file exceeds size limit`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "read_oversize.txt")

    mock.AddFile tempFile "content"
    let result = Tools.readFile mock.FileSystem 1L tempFile
    Assert.Contains("too large", assertError result)

[<Theory>]
[<InlineData(false, "Hello, F# Coding Agent!")>]
[<InlineData(true, "nested content")>]
[<InlineData(false, "")>]
[<InlineData(false, "line1\nline2\nline3")>]
let ``writeFile writes content and readFile reads it back`` (useNestedPath: bool, content: string) =
    let mock = MockFileSystem()

    let tempFile =
        if useNestedPath then
            System.IO.Path.Combine(System.Environment.CurrentDirectory, "parent", "child_dir", "nested_file.txt")
        else
            System.IO.Path.Combine(System.Environment.CurrentDirectory, "test_file.txt")

    let writeResult = Tools.writeFile mock.FileSystem 0L tempFile content
    Assert.Contains("Successfully wrote to", assertOk writeResult)
    let readResult = Tools.readFile mock.FileSystem 0L tempFile
    Assert.Equal(content, assertOk readResult)

[<Theory>]
[<InlineData(100L, "hi", true, "", "")>]
[<InlineData(5L, "hello world", false, "too large", "11 bytes")>]
[<InlineData(0L, "x", true, "", "")>]
let ``writeFile enforces file size limits``
    (maxSize: int64, content: string, expectOk: bool, expectedMsg1: string, expectedMsg2: string)
    =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "write_size_test.txt")

    let result = Tools.writeFile mock.FileSystem maxSize tempFile content

    if expectOk then
        Assert.Contains("Successfully wrote to", assertOk result)
    else
        let msg = assertError result
        Assert.Contains(expectedMsg1, msg)
        Assert.Contains(expectedMsg2, msg)

[<Fact>]
let ``writeFile returns Error when FileSystem.writeFile throws`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "throw_file.txt")

    mock.AddFile tempFile "initial"

    let fs =
        { mock.FileSystem with
            writeFile = fun _ _ -> failwith "Disk full" }

    let result = Tools.writeFile fs 0L tempFile "new content"
    Assert.Contains("Failed writing to file", assertError result)

[<Theory>]
[<InlineData("short", 100, "short", false)>]
[<InlineData("hello world this is a long line", 10, "hello worl", true)>]
[<InlineData("any length", 0, "any length", false)>]
let ``truncateLine truncates long lines`` (line: string, maxLength: int, expectedStart: string, expectTruncated: bool) =
    let result = Tools.truncateLine line maxLength
    Assert.StartsWith(expectedStart, result)

    if expectTruncated then
        Assert.EndsWith("[line truncated]", result)
    else
        Assert.DoesNotContain("[line truncated]", result)

[<Theory>]
[<InlineData("stdout", "stderr", true, true)>]
[<InlineData("", "stderr", false, true)>]
[<InlineData("stdout", "", true, false)>]
let ``formatCommandResult formats output and error sections``
    (stdout: string, stderr: string, hasOutput: bool, hasError: bool)
    =
    let result = Tools.formatCommandResult stdout stderr

    if hasOutput then
        Assert.Contains("Output:", result)
        Assert.Contains(stdout, result)
    else
        Assert.DoesNotContain("Output:", result)

    if hasError then
        Assert.Contains("Error:", result)
        Assert.Contains(stderr, result)
    else
        Assert.DoesNotContain("Error:", result)

[<Fact>]
let ``runCommand executes a simple echo command and captures its output`` () =
    async {
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
    |> Async.RunSynchronously

[<Fact>]
let ``runCommand executes in custom working directory`` () =
    let mock = MockFileSystem()

    let result =
        withTempDir "cmd_dir" (fun tempDir ->
            mock.AddDir tempDir

            let isWindows =
                System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform
                    System.Runtime.InteropServices.OSPlatform.Windows

            let cmd = if isWindows then "cd" else "pwd"

            Tools.runCommand
                mock.FileSystem
                1000000
                15000
                Sandbox.FallbackOnly
                System.Environment.CurrentDirectory
                cmd
                tempDir
            |> Async.RunSynchronously)

    Assert.True((assertOk result).Length > 0)

[<Fact>]
let ``runCommand truncates output when it exceeds maxOutputBytes`` () =
    async {
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
    |> Async.RunSynchronously

[<Fact>]
let ``runCommand returns Error when the command exits with a non-zero status code`` () =
    async {
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
    |> Async.RunSynchronously

[<Fact>]
let ``runCommand returns Error when the command parameter is null`` () =
    async {
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
    |> Async.RunSynchronously

[<Fact>]
let ``runCommand times out when command exceeds timeout`` () =
    async {
        let mock = MockFileSystem()
        mock.AddDir System.Environment.CurrentDirectory

        let! result =
            Tools.runCommand
                mock.FileSystem
                1000000
                100
                Sandbox.FallbackOnly
                System.Environment.CurrentDirectory
                "sleep 5"
                ""

        Assert.Contains("timed out", assertError result)
    }
    |> Async.RunSynchronously

[<Theory>]
[<InlineData("populated")>]
[<InlineData("empty")>]
[<InlineData("cwd")>]
let ``listDirectory shows files/folders, empty directory, or current directory`` (scenario: string) =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "test_dir")

    let subDir = System.IO.Path.Combine(tempDir, "sub_folder")

    match scenario with
    | "populated" ->
        mock.AddDir tempDir
        mock.AddDir subDir
        mock.AddFile (System.IO.Path.Combine(tempDir, "test_file.txt")) "content"
    | "empty" -> mock.AddDir tempDir
    | "cwd" -> mock.AddDir System.Environment.CurrentDirectory
    | _ -> failwith "unknown scenario"

    let dirArg = if scenario = "cwd" then "" else tempDir
    let result = Tools.listDirectory mock.FileSystem dirArg
    let msg = assertOk result

    match scenario with
    | "populated" ->
        Assert.Contains("[DIR]  sub_folder", msg)
        Assert.Contains("[FILE] test_file.txt", msg)
    | "empty" ->
        Assert.DoesNotContain("[DIR]", msg)
        Assert.DoesNotContain("[FILE]", msg)
    | "cwd" -> ()
    | _ -> failwith "unknown scenario"

    Assert.Contains("Contents of directory", msg)

[<Theory>]
[<InlineData("bin", true)>]
[<InlineData("obj", true)>]
[<InlineData("node_modules", true)>]
[<InlineData(".git", true)>]
[<InlineData("src", false)>]
let ``isIgnored returns expected result`` (dirName: string, expected: bool) =
    let mock = MockFileSystem()

    let path =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, dirName, "some_file")

    Assert.Equal(expected, Tools.isIgnored mock.FileSystem path)

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
    let result = Tools.grepSearch mock.FileSystem 100 "TargetKeyword" tempDir
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
    let result = Tools.grepSearch mock.FileSystem 100 "hello" tempDir
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
        Tools.grepSearch mock.FileSystem 100 "QueryThatWillNeverMatch_XYZ123" tempDir

    let msg = assertOk result
    Assert.Contains("No matches found for", msg)
    Assert.Contains("QueryThatWillNeverMatch_XYZ123", msg)

[<Fact>]
let ``grepSearch with no files returns no matches`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "grep_empty")

    mock.AddDir tempDir
    let result = Tools.grepSearch mock.FileSystem 100 "anything" tempDir
    Assert.Contains("No matches found", assertOk result)

[<Theory>]
[<InlineData(101, true)>]
[<InlineData(50, false)>]
let ``grepSearch truncates result list at 100 matches with overflow notice`` (lineCount: int, expectTruncated: bool) =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "grep_trunc_test")

    mock.AddDir tempDir

    let lines =
        [| 1..lineCount |]
        |> Array.map (fun i -> $"TargetKeyword line {i}")
        |> String.concat "\n"

    mock.AddFile (System.IO.Path.Combine(tempDir, "matches.txt")) lines
    let result = Tools.grepSearch mock.FileSystem 100 "TargetKeyword" tempDir
    let msg = assertOk result

    if expectTruncated then
        Assert.Contains("showing first 100 of more than 100", msg)
        Assert.Contains("matches.txt:1:", msg)
        Assert.DoesNotContain("line 101", msg)
    else
        Assert.DoesNotContain("showing first", msg)
        Assert.DoesNotContain("more than", msg)
        Assert.Contains("matches.txt:50:", msg)

[<Fact>]
let ``grepSearch truncates lines exceeding maxLineLength`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "grep_longline_test")

    mock.AddDir tempDir

    let shortLine = "short match here"
    let longLine = "MATCH " + String.replicate 110000 "x"
    mock.AddFile (System.IO.Path.Combine(tempDir, "test.txt")) (shortLine + "\n" + longLine)

    let result = Tools.grepSearch mock.FileSystem 100 "match" tempDir
    let msg = assertOk result
    Assert.Contains("short match here", msg)
    Assert.Contains("... [line truncated]", msg)
    Assert.DoesNotContain(String.replicate 110000 "x", msg)

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

    let result = Tools.grepSearch fs 100 "match" tempDir
    let msg = assertOk result
    Assert.Contains("⚠️  Warning: Skipped unreadable file 'src/bad.txt'", msg)
    Assert.Contains("good.txt:2: match found", msg)

[<Fact>]
let ``grepSearch warns when file is unreadable and relativePath also fails`` () =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "grep_doublefail")

    mock.AddDir tempDir
    let badFile = System.IO.Path.Combine(tempDir, "bad.txt")
    mock.AddFile badFile "match here"

    let fs =
        { mock.FileSystem with
            readLines = fun _ -> failwith "Access denied"
            relativePath =
                fun basePath file ->
                    if basePath = tempDir then
                        failwith "Path error too"
                    else
                        mock.FileSystem.relativePath basePath file }

    let result = Tools.grepSearch fs 100 "match" tempDir
    Assert.Contains("⚠️  Warning: Skipped unreadable file", assertOk result)

[<Theory>]
[<InlineData("hello world", "xyz", 0)>]
[<InlineData("hello world", "world", 1)>]
[<InlineData("ababab", "ab", 3)>]
let ``countOccurrences counts pattern occurrences`` (text: string, pattern: string, expected: int) =
    Assert.Equal(expected, Tools.countOccurrences text pattern 0 0)

[<Theory>]
[<InlineData("replace")>]
[<InlineData("noop")>]
let ``patchFile replaces target content or performs no-op when old equals new`` (scenario: string) =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "patch_test.txt")

    if scenario = "noop" then
        mock.AddFile tempFile "some content"
    else
        mock.AddFile tempFile "original line 1\nold_block_to_replace\noriginal line 3"

    let result =
        match scenario with
        | "replace" -> Tools.patchFile mock.FileSystem tempFile "old_block_to_replace" "new_substituted_block"
        | "noop" -> Tools.patchFile mock.FileSystem tempFile "content" "content"
        | _ -> failwith "unknown scenario"

    Assert.Contains("Successfully patched", assertOk result)

    match scenario with
    | "replace" ->
        let updatedContent = mock.FileSystem.readFile tempFile
        Assert.Contains("new_substituted_block", updatedContent)
        Assert.DoesNotContain("old_block_to_replace", updatedContent)
        Assert.Contains("original line 1", updatedContent)
    | "noop" ->
        let readResult = Tools.readFile mock.FileSystem 0L tempFile
        Assert.Equal("some content", assertOk readResult)
    | _ -> failwith "unknown scenario"

[<Fact>]
let ``patchFile returns Error when target content is not found`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "patch_not_found.txt")

    mock.AddFile tempFile "some content here"

    let result =
        Tools.patchFile mock.FileSystem tempFile "nonexistent target" "replacement"

    Assert.Contains("Target content to patch not found", assertError result)

[<Fact>]
let ``patchFile returns Error when target content appears multiple times`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "patch_dup.txt")

    mock.AddFile tempFile "duplicate\nduplicate\nduplicate"

    let result = Tools.patchFile mock.FileSystem tempFile "duplicate" "replacement"
    Assert.Contains("Target content found 3 times", assertError result)

[<Theory>]
[<InlineData(1, 10, true, "")>]
[<InlineData(0, 10, false, "start_line must be >= 1")>]
[<InlineData(1, 0, false, "end_line must be >= 1")>]
[<InlineData(10, 5, false, "cannot be greater than")>]
let ``checkLineRange validates start_line and end_line constraints``
    (startLine: int, endLine: int, expectValid: bool, expectedMsg: string)
    =
    match Tools.checkLineRange startLine endLine with
    | Ok() -> Assert.True expectValid
    | Error err ->
        Assert.False expectValid
        Assert.Contains(expectedMsg, err)

[<Theory>]
[<InlineData(2, 4, "line2\nline3\nline4")>]
[<InlineData(10, 20, "")>]
let ``readFileLines returns specified line range or empty when startLine is beyond file``
    (startLine: int, endLine: int, expected: string)
    =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "read_lines_test.txt")

    mock.AddFile tempFile "line1\nline2\nline3\nline4\nline5"
    let result = Tools.readFileLines mock.FileSystem 0L tempFile startLine endLine
    Assert.Equal(expected, assertOk result)

[<Fact>]
let ``readFileLines returns Error when file exceeds size limit`` () =
    let mock = MockFileSystem()

    let tempFile =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "readlines_oversize.txt")

    mock.AddFile tempFile "content"

    let result = Tools.readFileLines mock.FileSystem 1L tempFile 1 5
    Assert.Contains("too large", assertError result)

[<Theory>]
[<InlineData("finds")>]
[<InlineData("no-match")>]
[<InlineData("truncate")>]
let ``findFiles finds matching files, reports none, or truncates overflow`` (scenario: string) =
    let mock = MockFileSystem()

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "find_test")

    let srcDir = System.IO.Path.Combine(tempDir, "src")
    let binDir = System.IO.Path.Combine(tempDir, "bin")
    mock.AddDir tempDir

    match scenario with
    | "finds" ->
        mock.AddDir srcDir
        mock.AddDir binDir
        mock.AddFile (System.IO.Path.Combine(srcDir, "target_file.txt")) "hello"
        mock.AddFile (System.IO.Path.Combine(binDir, "target_file.txt")) "ignored"
        mock.AddFile (System.IO.Path.Combine(srcDir, "other.log")) "log"
    | "no-match" -> ()
    | "truncate" ->
        for i in 1..110 do
            mock.AddFile (System.IO.Path.Combine(tempDir, $"file{i}.txt")) "content"
    | _ -> failwith "unknown scenario"

    let pattern =
        match scenario with
        | "finds" -> "*target*"
        | "no-match" -> "*.nonexistent"
        | "truncate" -> "*.txt"
        | _ -> failwith "unknown scenario"

    let result = Tools.findFiles mock.FileSystem 100 pattern tempDir
    let msg = assertOk result

    match scenario with
    | "finds" ->
        Assert.Contains("target_file.txt", msg)
        Assert.Contains("src/target_file.txt", msg)
        Assert.DoesNotContain("bin/target_file.txt", msg)
        Assert.DoesNotContain("other.log", msg)
    | "no-match" -> Assert.Contains("No files matching pattern", msg)
    | "truncate" ->
        Assert.Contains("showing first 100", msg)
        Assert.Contains("more than 100", msg)
    | _ -> failwith "unknown scenario"
