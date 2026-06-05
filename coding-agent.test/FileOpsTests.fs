module CodingAgent.FileOpsTests

open Xunit
open CodingAgent

[<Fact>]
let ``workingDir falls back to current directory for null input`` () =
    let result = FileOps.workingDir null
    Assert.Equal(System.Environment.CurrentDirectory, result)

[<Fact>]
let ``workingDir falls back to current directory for empty input`` () =
    let result = FileOps.workingDir ""
    Assert.Equal(System.Environment.CurrentDirectory, result)

[<Fact>]
let ``workingDir falls back to current directory for whitespace input`` () =
    let result = FileOps.workingDir "   "
    Assert.Equal(System.Environment.CurrentDirectory, result)

[<Fact>]
let ``workingDir passes through absolute path unchanged`` () =
    let inputPath = "/tmp/test"
    let result = FileOps.workingDir inputPath
    Assert.Equal(inputPath, result)

[<Fact>]
let ``workingDir passes through relative path unchanged`` () =
    let inputPath = "src/main"
    let result = FileOps.workingDir inputPath
    Assert.Equal(inputPath, result)

[<Fact>]
let ``resolveSymlinks leaves non-symlink path unchanged`` () =
    let tempDir =
        System.IO.Path.Combine(
            System.Environment.CurrentDirectory,
            sprintf "resolve_test_%s" (System.Guid.NewGuid().ToString())
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
            sprintf "chain_test_%s" (System.Guid.NewGuid().ToString())
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
let ``resolveSymlinks detects circular symlinks without hanging`` () =
    let tempDir =
        System.IO.Path.Combine(
            System.Environment.CurrentDirectory,
            sprintf "circular_test_%s" (System.Guid.NewGuid().ToString())
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

[<Fact>]
let ``isPathInWorkspace allows path inside workspace`` () =
    let wsPath =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "test_temp")

    Assert.True(FileOps.isPathInWorkspace wsPath)

[<Fact>]
let ``isPathInWorkspace rejects empty string`` () =
    Assert.False(FileOps.isPathInWorkspace "")

[<Fact>]
let ``isPathInWorkspace rejects path outside workspace`` () =
    Assert.False(FileOps.isPathInWorkspace "/etc/passwd")

[<Fact>]
let ``isPathInWorkspace blocks symlink resolving outside workspace`` () =
    let tempDir =
        System.IO.Path.Combine(
            System.Environment.CurrentDirectory,
            sprintf "symlink_test_%s" (System.Guid.NewGuid().ToString())
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
let ``isPathInWorkspace allows symlink pointing inside workspace`` () =
    let tempDir =
        System.IO.Path.Combine(
            System.Environment.CurrentDirectory,
            sprintf "symlink_in_test_%s" (System.Guid.NewGuid().ToString())
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
let ``createParentDirectory creates parent directory hierarchy`` () =
    let tempDir =
        System.IO.Path.Combine(
            System.Environment.CurrentDirectory,
            sprintf "mkdir_test_%s" (System.Guid.NewGuid().ToString())
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
let ``fileNameWithoutExtension strips last extension from filename`` () =
    let result = FileOps.fileNameWithoutExtension "/home/user/document.txt"
    Assert.Equal("document", result)

[<Fact>]
let ``fileNameWithoutExtension keeps inner dots when stripping last extension`` () =
    let result = FileOps.fileNameWithoutExtension "archive.tar.gz"
    Assert.Equal("archive.tar", result)

[<Fact>]
let ``fileNameWithoutExtension returns whole name for extensionless file`` () =
    let result = FileOps.fileNameWithoutExtension "README"
    Assert.Equal("README", result)

[<Fact>]
let ``relativePath computes relative path to nested file`` () =
    let basePath = "/home/user"
    let targetPath = "/home/user/documents/file.txt"
    let result = FileOps.relativePath basePath targetPath
    Assert.Equal("documents/file.txt", result)

[<Fact>]
let ``relativePath navigates up directories when target is outside base`` () =
    let basePath = "/home/user/project/src"
    let targetPath = "/home/user/docs/readme.md"
    let result = FileOps.relativePath basePath targetPath
    Assert.Equal("../../docs/readme.md", result)

[<Fact>]
let ``relativePath returns dot when target path equals base path`` () =
    let basePath = "/home/user"
    let targetPath = "/home/user"
    let result = FileOps.relativePath basePath targetPath
    Assert.Equal(".", result)
