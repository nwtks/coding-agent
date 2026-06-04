namespace CodingAgent

module Sandbox =
    type SandboxMode =
        | BwrapSandbox
        | FallbackOnly

    let mutable private detectedMode: SandboxMode option = None

    let detectSandboxMode () =
        match detectedMode with
        | Some mode -> mode
        | None ->
            let mode =
                try
                    let psi = System.Diagnostics.ProcessStartInfo("bwrap", "--version")
                    psi.RedirectStandardOutput <- true
                    psi.RedirectStandardError <- true
                    psi.UseShellExecute <- false
                    psi.CreateNoWindow <- true
                    use p = System.Diagnostics.Process.Start psi
                    p.WaitForExit()
                    if p.ExitCode = 0 then BwrapSandbox else FallbackOnly
                with _ ->
                    FallbackOnly

            detectedMode <- Some mode
            mode

    let wrapWithUlimit commandLine =
        sprintf "ulimit -v 2097152 -f 1048576 -t 120; %s" commandLine

    let makeBwrapArgs workspaceRoot commandLine cwd =
        let args = System.Collections.Generic.List<string>()

        let binds =
            [ "/usr"; "/bin"; "/lib"; "/lib64"; "/sbin" ]
            |> List.filter System.IO.Directory.Exists

        for path in binds do
            args.Add "--ro-bind"
            args.Add path
            args.Add path

        let rootLinks =
            [ "usr/lib", "/lib"
              "usr/lib64", "/lib64"
              "usr/bin", "/bin"
              "usr/sbin", "/sbin" ]

        for target, link in rootLinks do
            if
                not (System.IO.Directory.Exists link)
                && (System.IO.File.Exists link || System.IO.Directory.Exists link)
            then
                args.Add "--symlink"
                args.Add target
                args.Add link

        let roBinds =
            [ "/etc/alternatives"
              "/etc/resolv.conf"
              "/etc/ssl"
              "/etc/pki"
              "/etc/crypto-policies" ]
            |> List.filter (fun p -> System.IO.File.Exists p || System.IO.Directory.Exists p)

        for path in roBinds do
            args.Add "--ro-bind"
            args.Add path
            args.Add path

        let home = System.Environment.GetEnvironmentVariable("HOME")

        if not (System.String.IsNullOrEmpty(home)) then
            args.Add "--homedir"
            args.Add home
            let nugetCache = System.IO.Path.Combine(home, ".nuget")

            if System.IO.Directory.Exists nugetCache then
                args.Add "--ro-bind"
                args.Add nugetCache
                args.Add nugetCache

            let npmCache = System.IO.Path.Combine(home, ".npm")

            if System.IO.Directory.Exists npmCache then
                args.Add "--ro-bind"
                args.Add npmCache
                args.Add npmCache

        args.Add "--proc"
        args.Add "/proc"
        args.Add "--dev"
        args.Add "/dev"
        args.Add "--tmpfs"
        args.Add "/tmp"
        args.Add "--bind"
        args.Add workspaceRoot
        args.Add workspaceRoot
        args.Add "--chdir"
        args.Add cwd
        args.Add "--unshare-user"
        args.Add "--unshare-pid"
        args.Add "--unshare-ipc"
        args.Add "--unshare-uts"
        args.Add "--unshare-cgroup"
        args.Add "--share-net"
        args.Add "--die-with-parent"
        args.Add "--new-session"
        args.Add "--"
        args.Add "bash"
        args.Add "-c"
        args.Add commandLine
        args.ToArray()

    let sandboxedStartInfo sandboxMode workspaceRoot commandLine cwd =
        let startInfo = System.Diagnostics.ProcessStartInfo()
        let commandWithLimit = wrapWithUlimit commandLine

        match sandboxMode with
        | BwrapSandbox ->
            startInfo.FileName <- "bwrap"
            let args = makeBwrapArgs workspaceRoot commandWithLimit cwd

            for arg in args do
                startInfo.ArgumentList.Add arg
        | FallbackOnly ->
            startInfo.FileName <- "bash"
            startInfo.ArgumentList.Add "-c"
            startInfo.ArgumentList.Add commandWithLimit

        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.UseShellExecute <- false
        startInfo.CreateNoWindow <- true
        startInfo.WorkingDirectory <- cwd
        startInfo
