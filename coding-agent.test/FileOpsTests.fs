module CodingAgent.FileOpsTests

open Xunit
open CodingAgent

[<Theory>]
[<InlineData(null, "CURRENT_DIR")>]
[<InlineData("", "CURRENT_DIR")>]
[<InlineData("   ", "CURRENT_DIR")>]
[<InlineData("/tmp/test", "/tmp/test")>]
[<InlineData("src/main", "src/main")>]
let ``workingDir handles null, empty, whitespace, absolute, and relative inputs`` (input: string, expected: string) =
    let result = FileOps.workingDir input

    if expected = "CURRENT_DIR" then
        Assert.Equal(System.Environment.CurrentDirectory, result)
    else
        Assert.Equal(expected, result)

[<Fact>]
let ``resolveSymlinks leaves non-symlink path unchanged`` () =
    let tempDir =
        System.IO.Path.Combine(
            System.Environment.CurrentDirectory,
            sprintf "resolve_test_%s" (System.Guid.NewGuid().ToString "N")
        )

    try
        System.IO.Directory.CreateDirectory tempDir |> ignore
        let resolved = FileOps.resolveSymlinks tempDir
        Assert.Equal(System.IO.Path.GetFullPath tempDir, resolved)
    finally
        if System.IO.Directory.Exists tempDir then
            System.IO.Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveSymlinks resolves chained symlinks to final target`` () =
    let tempDir =
        System.IO.Path.Combine(
            System.Environment.CurrentDirectory,
            sprintf "chain_test_%s" (System.Guid.NewGuid().ToString "N")
        )

    try
        System.IO.Directory.CreateDirectory tempDir |> ignore
        let realTarget = System.IO.Path.Combine(tempDir, "real.txt")
        System.IO.File.WriteAllText(realTarget, "hello")

        let link1 = System.IO.Path.Combine(tempDir, "link1.txt")
        let link2 = System.IO.Path.Combine(tempDir, "link2.txt")

        let psi1 =
            System.Diagnostics.ProcessStartInfo("ln", sprintf "-s %s %s" realTarget link1)

        psi1.RedirectStandardOutput <- true
        psi1.RedirectStandardError <- true
        psi1.UseShellExecute <- false
        use p1 = System.Diagnostics.Process.Start psi1
        p1.WaitForExit 5000 |> ignore

        let psi2 = System.Diagnostics.ProcessStartInfo("ln", sprintf "-s %s %s" link1 link2)
        psi2.RedirectStandardOutput <- true
        psi2.RedirectStandardError <- true
        psi2.UseShellExecute <- false
        use p2 = System.Diagnostics.Process.Start psi2
        p2.WaitForExit 5000 |> ignore

        let resolved = FileOps.resolveSymlinks link2
        Assert.Equal(System.IO.Path.GetFullPath realTarget, resolved)
    finally
        if System.IO.Directory.Exists tempDir then
            System.IO.Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveSymlinks detects circular symlinks and returns without infinite loop`` () =
    let tempDir =
        System.IO.Path.Combine(
            System.Environment.CurrentDirectory,
            sprintf "circular_test_%s" (System.Guid.NewGuid().ToString "N")
        )

    try
        System.IO.Directory.CreateDirectory tempDir |> ignore
        let linkA = System.IO.Path.Combine(tempDir, "a.txt")
        let linkB = System.IO.Path.Combine(tempDir, "b.txt")
        let timeout = 10000

        let psi1 = System.Diagnostics.ProcessStartInfo("ln", sprintf "-s %s %s" linkB linkA)
        psi1.RedirectStandardOutput <- true
        psi1.RedirectStandardError <- true
        psi1.UseShellExecute <- false
        use p1 = System.Diagnostics.Process.Start psi1
        p1.WaitForExit timeout |> ignore

        let psi2 = System.Diagnostics.ProcessStartInfo("ln", sprintf "-s %s %s" linkA linkB)
        psi2.RedirectStandardOutput <- true
        psi2.RedirectStandardError <- true
        psi2.UseShellExecute <- false
        use p2 = System.Diagnostics.Process.Start psi2
        p2.WaitForExit timeout |> ignore

        let sw = System.Diagnostics.Stopwatch.StartNew()
        let resolved = FileOps.resolveSymlinks linkA
        sw.Stop()
        Assert.True(sw.ElapsedMilliseconds < 5000, "resolveSymlinks took too long — likely infinite loop")
        Assert.False(System.String.IsNullOrWhiteSpace resolved)
    finally
        if System.IO.Directory.Exists tempDir then
            try
                System.IO.File.Delete(System.IO.Path.Combine(tempDir, "a.txt"))
            with _ ->
                ()

            try
                System.IO.File.Delete(System.IO.Path.Combine(tempDir, "b.txt"))
            with _ ->
                ()

            System.IO.Directory.Delete(tempDir, true)

[<Theory>]
[<InlineData("test_temp", true)>]
[<InlineData("", false)>]
[<InlineData("/etc/passwd", false)>]
let ``isPathInWorkspace allows paths inside workspace and rejects empty or outside paths`` (input: string, expected: bool) =
    let path =
        if System.String.IsNullOrEmpty input then
            input
        else
            System.IO.Path.Combine(System.Environment.CurrentDirectory, input)

    Assert.Equal(expected, FileOps.isPathInWorkspace path)

[<Fact>]
let ``isPathInWorkspace blocks symlink that resolves to a path outside the workspace`` () =
    let tempDir =
        System.IO.Path.Combine(
            System.Environment.CurrentDirectory,
            sprintf "symlink_test_%s" (System.Guid.NewGuid().ToString "N")
        )

    try
        System.IO.Directory.CreateDirectory tempDir |> ignore
        let symlinkPath = System.IO.Path.Combine(tempDir, "etc_link")

        let psi =
            System.Diagnostics.ProcessStartInfo("ln", sprintf "-s /etc %s" symlinkPath)

        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        use p = System.Diagnostics.Process.Start(psi)
        p.WaitForExit 5000 |> ignore

        if System.IO.File.Exists symlinkPath || System.IO.Directory.Exists(symlinkPath) then
            let result = FileOps.isPathInWorkspace symlinkPath
            Assert.False(result, "Symlink to /etc should be blocked")
        else
            ()
    finally
        if System.IO.Directory.Exists tempDir then
            System.IO.Directory.Delete(tempDir, true)

[<Fact>]
let ``isPathInWorkspace allows symlink that resolves to a path inside the workspace`` () =
    let tempDir =
        System.IO.Path.Combine(
            System.Environment.CurrentDirectory,
            sprintf "symlink_in_test_%s" (System.Guid.NewGuid().ToString "N")
        )

    try
        System.IO.Directory.CreateDirectory tempDir |> ignore
        let targetFile = System.IO.Path.Combine(tempDir, "target.txt")
        System.IO.File.WriteAllText(targetFile, "hello")
        let symlinkPath = System.IO.Path.Combine(tempDir, "link.txt")

        let psi =
            System.Diagnostics.ProcessStartInfo("ln", sprintf "-s %s %s" targetFile symlinkPath)

        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        use p = System.Diagnostics.Process.Start(psi)
        p.WaitForExit 5000 |> ignore

        if System.IO.File.Exists symlinkPath then
            let result = FileOps.isPathInWorkspace symlinkPath
            Assert.True(result, "Symlink to file inside workspace should be allowed")
        else
            ()
    finally
        if System.IO.Directory.Exists tempDir then
            System.IO.Directory.Delete(tempDir, true)

[<Fact>]
let ``createParentDirectory creates the full parent directory hierarchy for a file path`` () =
    let tempDir =
        System.IO.Path.Combine(
            System.Environment.CurrentDirectory,
            sprintf "mkdir_test_%s" (System.Guid.NewGuid().ToString "N")
        )

    try
        let nestedFile = System.IO.Path.Combine(tempDir, "nested", "file.txt")
        FileOps.createParentDirectory nestedFile
        Assert.True(System.IO.Directory.Exists(System.IO.Path.GetDirectoryName nestedFile))
    finally
        if System.IO.Directory.Exists tempDir then
            System.IO.Directory.Delete(tempDir, true)

[<Fact>]
let ``fileName extracts filename from absolute path`` () =
    let result = FileOps.fileName "/home/user/document.txt"
    Assert.Equal("document.txt", result)

[<Fact>]
let ``fileName extracts filename from relative path`` () =
    let result = FileOps.fileName "src/main.fs"
    Assert.Equal("main.fs", result)

[<Fact>]
let ``fileName extracts filename from bare filename`` () =
    let result = FileOps.fileName "readme.md"
    Assert.Equal("readme.md", result)

[<Fact>]
let ``fileNameWithoutExtension strips the last file extension from a filename`` () =
    let result = FileOps.fileNameWithoutExtension "/home/user/document.txt"
    Assert.Equal("document", result)

[<Fact>]
let ``fileNameWithoutExtension preserves inner dots while stripping the final extension`` () =
    let result = FileOps.fileNameWithoutExtension "archive.tar.gz"
    Assert.Equal("archive.tar", result)

[<Fact>]
let ``fileNameWithoutExtension returns whole name for extensionless file`` () =
    let result = FileOps.fileNameWithoutExtension "README"
    Assert.Equal("README", result)

[<Fact>]
let ``relativePath computes a relative path from base to a nested target`` () =
    let basePath = "/home/user"
    let targetPath = "/home/user/documents/file.txt"
    let result = FileOps.relativePath basePath targetPath
    Assert.Equal("documents/file.txt", result)

[<Fact>]
let ``relativePath uses parent directory navigation when target is outside base path`` () =
    let basePath = "/home/user/project/src"
    let targetPath = "/home/user/docs/readme.md"
    let result = FileOps.relativePath basePath targetPath
    Assert.Equal("../../docs/readme.md", result)

[<Fact>]
let ``relativePath returns a single dot when target path equals base path`` () =
    let basePath = "/home/user"
    let targetPath = "/home/user"
    let result = FileOps.relativePath basePath targetPath
    Assert.Equal(".", result)
