module CodingAgent.SandboxTests

open Xunit
open CodingAgent

[<Fact>]
let ``detectSandboxMode returns a valid mode without throwing`` () =
    let mode = Sandbox.detectSandboxMode ()

    match mode with
    | Sandbox.BwrapSandbox -> Assert.True true
    | Sandbox.FallbackOnly -> Assert.True true

[<Fact>]
let ``wrapWithUlimit prepends ulimit constraints`` () =
    let cmd = "echo hello"
    let wrapped = Sandbox.wrapWithUlimit cmd
    Assert.StartsWith("ulimit -v 2097152 -f 1048576 -t 120;", wrapped)
    Assert.EndsWith("echo hello", wrapped)

[<Fact>]
let ``makeBwrapArgs includes namespace isolation and bind mount flags`` () =
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

[<Fact>]
let ``sandboxedStartInfo configures bwrap for BwrapSandbox mode`` () =
    let psi =
        Sandbox.sandboxedStartInfo Sandbox.BwrapSandbox "/tmp/work" "echo test" "/tmp/work"

    Assert.Equal("bwrap", psi.FileName)
    Assert.Contains("--unshare-pid", psi.ArgumentList)
    Assert.True psi.RedirectStandardOutput
    Assert.True psi.RedirectStandardError
    Assert.False psi.UseShellExecute

[<Fact>]
let ``sandboxedStartInfo configures bash for FallbackOnly mode`` () =
    let psi =
        Sandbox.sandboxedStartInfo Sandbox.FallbackOnly "/tmp/work" "echo test" "/tmp/work"

    Assert.Equal("bash", psi.FileName)
    Assert.Equal("-c", psi.ArgumentList.[0])
    Assert.Contains("ulimit", psi.ArgumentList.[1])
    Assert.True psi.RedirectStandardOutput
    Assert.True psi.RedirectStandardError
    Assert.False psi.UseShellExecute
