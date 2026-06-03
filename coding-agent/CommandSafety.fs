namespace CodingAgent

module CommandSafety =
    let safeEnvVars =
        set
            [ "PATH"
              "HOME"
              "USER"
              "LOGNAME"
              "SHELL"
              "LANG"
              "LC_ALL"
              "LC_CTYPE"
              "LC_MESSAGES"
              "LC_MONETARY"
              "LC_NUMERIC"
              "LC_TIME"
              "DOTNET_CLI_TELEMETRY_OPTOUT"
              "DOTNET_SKIP_FIRST_TIME_EXPERIENCE"
              "DOTNET_NOLOGO"
              "DOTNET_ROOT" ]

    let sanitizeEnvironment (startInfo: System.Diagnostics.ProcessStartInfo) =
        startInfo.Environment.Clear()

        System.Environment.GetEnvironmentVariables()
        |> Seq.cast<System.Collections.DictionaryEntry>
        |> Seq.iter (fun entry ->
            let key = entry.Key :?> string

            if safeEnvVars.Contains key then
                startInfo.Environment.Add(key, entry.Value :?> string))

        startInfo.Environment.Add("TERM", "dumb")
        startInfo.Environment.Add("CODING_AGENT_SANDBOX", "1")

    let normalizeCommand (command: string) =
        command.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ")
        |> fun s -> System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim()

    [<TailCall>]
    let rec findCommentIdx (line: string) idx inSingle inDouble =
        if idx >= line.Length then
            None
        else
            match line.[idx] with
            | '\'' when not inDouble -> findCommentIdx line (idx + 1) (not inSingle) false
            | '"' when not inSingle -> findCommentIdx line (idx + 1) false (not inDouble)
            | '#' when not inSingle && not inDouble && (idx = 0 || line.[idx - 1] = ' ') -> Some idx
            | _ -> findCommentIdx line (idx + 1) inSingle inDouble

    let stripComments (command: string) =
        command.Split '\n'
        |> Array.map (fun line ->
            match findCommentIdx line 0 false false with
            | Some 0 -> ""
            | Some i -> line.Substring(0, i).TrimEnd()
            | None -> line)
        |> Array.filter (not << System.String.IsNullOrWhiteSpace)
        |> String.concat "\n"

    let shellExpansionPatterns =
        [ @"\$\(" // $(command) substitution
          @"`[^`]+`" // backtick substitution (paired backticks with content)
          @"\\x[0-9a-fA-F]{2}" // hex escape sequences used to construct characters
          @"\\[0-7]{1,3}" // octal escape sequences
          @"\$'\\" ] // $'...' ANSI-C quoting with escapes

    let containsShellExpansion (command: string) =
        shellExpansionPatterns
        |> List.exists (fun p ->
            System.Text.RegularExpressions.Regex.IsMatch(
                command,
                p,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            ))

    let dangerousCommandPatterns =
        [ "cd /"
          "cd /root"
          "cd /home"
          "cd /etc"
          "cd /usr"
          "cd /var"
          "cd /opt"
          "cd /bin"
          "cd /sbin"
          "rm -rf /"
          "rm -rf ~"
          "rm -r /"
          "mkfs"
          ":(){:|:&};:"
          "chmod 777 /"
          "chown"
          "sudo"
          "su "
          "su -"
          "passwd"
          "useradd"
          "userdel"
          "groupadd"
          "dd if="
          "dd of=/dev"
          "shutdown"
          "reboot"
          "halt"
          "poweroff"
          "init 0"
          "init 6"
          "eval "
          "exec "
          "| sh"
          "| bash"
          "| bash -i"
          "| zsh"
          "nc -l"
          "ncat -l"
          "> /dev/sda"
          "mv /* "
          "rm -rf /*"
          "iptables"
          "nft "
          "nohup "
          "crontab"
          "at "
          "> /etc/" ]

    let dangerousCommandRegexes =
        [ @"(?:^|\s)rm\s+.*-r.*\s/" // rm -r ... / (any ordering of flags)
          @"(?:^|\s)rm\s+-\w*r\w*\s+/" // rm -xr / (flags combined)
          @"(?:^|\s)rm\s+/\s+-" // rm / -flags (path before flags)
          @"(?:^|;\s*)\|" ] // pipe at start of a sub-command (after ;)

    let validateCommand command =
        if containsShellExpansion command then
            Error "Error: Command contains shell expansion sequences that may be used to obfuscate dangerous commands."
        else
            let normalized = stripComments command |> normalizeCommand

            if System.String.IsNullOrWhiteSpace normalized then
                Error "Error: Command is empty after removing comments."
            else
                let containsDangerous =
                    dangerousCommandPatterns
                    |> List.tryFind (fun pattern ->
                        normalized.Contains(pattern, System.StringComparison.OrdinalIgnoreCase))

                match containsDangerous with
                | Some pattern ->
                    sprintf "Error: Command contains potentially dangerous pattern: '%s'" pattern
                    |> Error
                | None ->
                    let matchesRegex =
                        dangerousCommandRegexes
                        |> List.tryFind (fun pattern ->
                            System.Text.RegularExpressions.Regex.IsMatch(
                                normalized,
                                pattern,
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                            ))

                    match matchesRegex with
                    | Some pattern -> sprintf "Error: Command matches dangerous pattern: '%s'" pattern |> Error
                    | None -> Ok()

    let processStartInfo commandLine cwd =
        let isWindows =
            System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform
                System.Runtime.InteropServices.OSPlatform.Windows

        let startInfo = System.Diagnostics.ProcessStartInfo()

        if isWindows then
            startInfo.FileName <- "cmd.exe"
            startInfo.ArgumentList.Add "/c"
            startInfo.ArgumentList.Add commandLine
        else
            startInfo.FileName <- "bash"
            startInfo.ArgumentList.Add "-c"
            startInfo.ArgumentList.Add commandLine

        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.UseShellExecute <- false
        startInfo.CreateNoWindow <- true
        startInfo.WorkingDirectory <- cwd
        sanitizeEnvironment startInfo
        startInfo
