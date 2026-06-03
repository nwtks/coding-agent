module CodingAgent.FileOpsTests

open Xunit
open CodingAgent

[<Fact>]
let ``isPathInWorkspace returns false for empty string`` () =
    Assert.False(FileOps.isPathInWorkspace "")

[<Fact>]
let ``isPathInWorkspace returns false for path outside workspace`` () =
    Assert.False(FileOps.isPathInWorkspace "/etc/passwd")

[<Fact>]
let ``isPathInWorkspace returns true for path inside workspace`` () =
    let wsPath =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, "test_temp")

    Assert.True(FileOps.isPathInWorkspace wsPath)

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
