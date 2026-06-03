module CodingAgent.FileOpsTests

open Xunit
open CodingAgent

[<Fact>]
let ``resolveSymlinks returns original path when no symlink exists`` () =
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
let ``resolveSymlinks follows symlink chain correctly`` () =
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
let ``resolveSymlinks detects circular symlinks without infinite loop`` () =
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
let ``isPathInWorkspace returns true for path inside workspace`` () =
    let wsPath =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "test_temp")

    Assert.True(FileOps.isPathInWorkspace wsPath)

[<Fact>]
let ``isPathInWorkspace returns false for empty string`` () =
    Assert.False(FileOps.isPathInWorkspace "")

[<Fact>]
let ``isPathInWorkspace returns false for path outside workspace`` () =
    Assert.False(FileOps.isPathInWorkspace "/etc/passwd")

[<Fact>]
let ``isPathInWorkspace blocks symlink pointing outside workspace`` () =
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
        p.WaitForExit(5000) |> ignore

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
        p.WaitForExit(5000) |> ignore

        if System.IO.File.Exists symlinkPath then
            let result = FileOps.isPathInWorkspace symlinkPath
            Assert.True(result, "Symlink to file inside workspace should be allowed")
        else
            ()
    finally
        if System.IO.Directory.Exists tempDir then
            System.IO.Directory.Delete(tempDir, true)

[<Fact>]
let ``mkdir creates directory for valid file path`` () =
    let tempDir =
        System.IO.Path.Combine(
            System.Environment.CurrentDirectory,
            sprintf "mkdir_test_%s" (System.Guid.NewGuid().ToString())
        )

    try
        let nestedFile = System.IO.Path.Combine(tempDir, "nested", "file.txt")
        FileOps.mkdir nestedFile
        Assert.True(System.IO.Directory.Exists(System.IO.Path.GetDirectoryName nestedFile))
    finally
        if System.IO.Directory.Exists tempDir then
            System.IO.Directory.Delete(tempDir, true)
