[<Xunit.Collection("EnvironmentVariables")>]
module CodingAgent.Tests.SandboxTests

open Xunit
open CodingAgent
open TestHelpers

[<Fact>]
let ``detectSandboxMode returns either BwrapSandbox or FallbackOnly without throwing`` () =
    let mode = Sandbox.detectSandboxMode ()

    match mode with
    | Sandbox.BwrapSandbox -> Assert.True true
    | Sandbox.FallbackOnly -> Assert.True true

[<Fact>]
let ``wrapWithUlimit prepends virtual memory, file size, and CPU time ulimit constraints`` () =
    let cmd = "echo hello"
    let wrapped = Sandbox.wrapWithUlimit cmd
    Assert.StartsWith("ulimit -v 2097152 -f 1048576 -t 120;", wrapped)
    Assert.EndsWith("echo hello", wrapped)

[<Fact>]
let ``nugetCachePath returns None when HOME is unset`` () =
    withEnvVars [ "HOME", "" ] (fun () ->
        let result = Sandbox.nugetCachePath ()
        Assert.Equal(None, result))

[<Fact>]
let ``nugetCachePath returns None when HOME is set but .nuget does not exist`` () =
    withTempDir "nuget_missing" (fun tempDir ->
        withEnvVars [ "HOME", tempDir ] (fun () ->
            let result = Sandbox.nugetCachePath ()
            Assert.Equal(None, result)))

[<Fact>]
let ``nugetCachePath returns Some when HOME is set and .nuget exists`` () =
    withTempDir "nuget_present" (fun tempDir ->
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(tempDir, ".nuget"))
        |> ignore

        withEnvVars [ "HOME", tempDir ] (fun () ->
            let result = Sandbox.nugetCachePath ()
            Assert.Equal(Some(System.IO.Path.Combine(tempDir, ".nuget")), result)))

[<Fact>]
let ``npmCachePath returns None when HOME is unset`` () =
    withEnvVars [ "HOME", "" ] (fun () ->
        let result = Sandbox.npmCachePath ()
        Assert.Equal(None, result))

[<Fact>]
let ``npmCachePath returns None when HOME is set but .npm does not exist`` () =
    withTempDir "npm_missing" (fun tempDir ->
        withEnvVars [ "HOME", tempDir ] (fun () ->
            let result = Sandbox.npmCachePath ()
            Assert.Equal(None, result)))

[<Fact>]
let ``npmCachePath returns Some when HOME is set and .npm exists`` () =
    withTempDir "npm_present" (fun tempDir ->
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(tempDir, ".npm"))
        |> ignore

        withEnvVars [ "HOME", tempDir ] (fun () ->
            let result = Sandbox.npmCachePath ()
            Assert.Equal(Some(System.IO.Path.Combine(tempDir, ".npm")), result)))

[<Fact>]
let ``makeBwrapArgs includes user/pid/ipc namespace isolation and workspace bind mount`` () =
    let workspace = "/tmp/workspace"
    let cmd = "echo test"
    let cwd = "/tmp/workspace/subdir"

    let args =
        Sandbox.makeBwrapArgs (Some "/tmp/workspace/nuget") (Some "/tmp/workspacenpm") workspace cmd cwd

    let bindIndex = System.Array.IndexOf(args, "--bind")
    Assert.True(bindIndex >= 0)
    Assert.Equal(workspace, args.[bindIndex + 1])

    let chdirIndex = System.Array.IndexOf(args, "--chdir")
    Assert.True(chdirIndex >= 0)
    Assert.Equal(cwd, args.[chdirIndex + 1])
    Assert.Contains("--unshare-user", args)
    Assert.Contains("--unshare-pid", args)
    Assert.Contains("--unshare-ipc", args)
    Assert.Contains("--unshare-uts", args)
    Assert.Contains("--unshare-cgroup", args)
    Assert.Contains("--share-net", args)
    Assert.Contains("--die-with-parent", args)
    Assert.Contains("--new-session", args)
    Assert.Contains("bash", args)
    Assert.Contains("-c", args)
    Assert.Contains(cmd, args)

[<Theory>]
[<InlineData("BwrapSandbox")>]
[<InlineData("FallbackOnly")>]
let ``sandboxedStartInfo configures process correctly for each sandbox mode`` (modeName: string) =
    let mode =
        match modeName with
        | "BwrapSandbox" -> Sandbox.BwrapSandbox
        | "FallbackOnly" -> Sandbox.FallbackOnly
        | _ -> failwith "unknown mode"

    let psi = Sandbox.sandboxedStartInfo mode "/tmp/work" "echo test" "/tmp/work"

    match mode with
    | Sandbox.BwrapSandbox ->
        Assert.Equal("bwrap", psi.FileName)
        Assert.Contains("--unshare-pid", psi.ArgumentList)
    | Sandbox.FallbackOnly ->
        Assert.Equal("bash", psi.FileName)
        Assert.Equal("-c", psi.ArgumentList.[0])
        Assert.Contains("ulimit", psi.ArgumentList.[1])

    Assert.True psi.RedirectStandardOutput
    Assert.True psi.RedirectStandardError
    Assert.False psi.UseShellExecute
