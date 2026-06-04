namespace CodingAgent

module CommandSafety =
    let safeEnvVars =
        set
            [ "PATH"
              "HOME"
              "USER"
              "LOGNAME"
              "SHELL"
              "PWD"
              "TMP"
              "TMPDIR"
              "TEMP"
              "EDITOR"
              "PAGER"
              "GIT_PAGER"
              "LANG"
              "LC_ALL"
              "LC_CTYPE"
              "LC_MESSAGES"
              "LC_MONETARY"
              "LC_NUMERIC"
              "LC_TIME"
              "NODE_ENV"
              "DOTNET_CLI_TELEMETRY_OPTOUT"
              "DOTNET_SKIP_FIRST_TIME_EXPERIENCE"
              "DOTNET_NOLOGO"
              "DOTNET_ROOT"
              "DOTNET_ENVIRONMENT"
              "DOTNET_RUNNING_IN_CONTAINER"
              "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"
              "DOTNET_SYSTEM_NET_HTTP_SOCKETSHTTPHANDLER_HTTP2UNENCRYPTEDSUPPORT"
              "ASPNETCORE_ENVIRONMENT"
              "ASPNETCORE_URLS" ]

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

    let whitespaceRegex =
        System.Text.RegularExpressions.Regex(@"\s+", System.Text.RegularExpressions.RegexOptions.Compiled)

    let normalizeCommand (command: string) =
        command.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ")
        |> fun s -> whitespaceRegex.Replace(s, " ").Trim()

    let isEscapedQuote (line: string) idx = idx > 0 && line.[idx - 1] = '\\'

    [<TailCall>]
    let rec findCommentIdx (line: string) idx inSingle inDouble =
        if idx >= line.Length then
            None
        else
            match line.[idx] with
            | '\'' when not inDouble && not (isEscapedQuote line idx) ->
                findCommentIdx line (idx + 1) (not inSingle) false
            | '"' when not inSingle && not (isEscapedQuote line idx) ->
                findCommentIdx line (idx + 1) false (not inDouble)
            | '#' when
                not inSingle
                && not inDouble
                && (idx = 0 || System.Char.IsWhiteSpace line.[idx - 1])
                ->
                Some idx
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

    let shellExpansionRegexes =
        [ @"\$\(" // $(command) substitution
          @"`[^`]+`" // backtick substitution (paired backticks with content)
          @"\\x[0-9a-fA-F]{2}" // hex escape sequences used to construct characters
          @"\\[0-7]{1,3}" // octal escape sequences
          @"\$'\\" ] // $'...' ANSI-C quoting with escapes
        |> List.map (fun p ->
            System.Text.RegularExpressions.Regex(
                p,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                ||| System.Text.RegularExpressions.RegexOptions.Compiled
            ))

    let containsShellExpansion (command: string) =
        shellExpansionRegexes |> List.exists (fun regex -> regex.IsMatch command)

    let dangerousCommandPatterns = [ ":(){:|:&};:" ]

    let simpleDangerousCommands =
        [ "mkfs"
          "chown"
          "sudo"
          "su"
          "passwd"
          "useradd"
          "userdel"
          "groupadd"
          "shutdown"
          "reboot"
          "halt"
          "poweroff"
          "iptables"
          "nohup"
          "crontab"
          "at"
          "nft"
          "eval"
          "exec" ]
        |> List.map (fun cmd -> sprintf @"(?:^|\s)%s(?:\s|$|;|&&|\||>|#)" cmd)

    let complexDangerousRegexes =
        [ @"(?:^|\s)cd\s+/(?:\s|$|;|&&|\||>|#)"
          @"(?:^|\s)cd\s+/root(?:\s|$|;|&&|\||>|#)"
          @"(?:^|\s)rm\s+.*-r.*\s/\*?(?:\s|$|;|&&|\||>|#)"
          @"(?:^|\s)rm\s+-\w*r\w*\s+/\*?(?:\s|$|;|&&|\||>|#)"
          @"(?:^|\s)rm\s+/\*?\s+-"
          @"(?:^|\s)rm\s+.*-r.*\s~/*?(?:\s|$|;|&&|\||>|#)"
          @"(?:^|\s)chmod\s+(?:-R\s+)?777\s+/"
          @"(?:^|\s)dd\s+if="
          @"(?:^|\s)dd\s+.*of=/dev"
          @"(?:^|\s)init\s+[06](?:\s|$|;|&&|\||>|#)"
          @"\|\s*(?:sh|bash|zsh)(?:\s|$|;|&&|\||>|#)"
          @"(?:^|;\s*)\|"
          @"(?:^|\s)n?cat\s+.*-l"
          @">\s*/(?:dev/sda|etc/)"
          @"(?:^|\s)mv\s+/\*\s+"
          @"(?:^|\s)python[23]?\s+.*-c(?:\s|$)"
          @"(?:^|\s)ruby\s+.*-e(?:\s|$)"
          @"(?:^|\s)perl\s+.*-e(?:\s|$)"
          @"(?:^|\s)node\s+.*-e(?:\s|$)"
          @"(?:^|\s)php\s+.*-r(?:\s|$)"
          @"(?:^|\s)bash\s+.*-c(?:\s|$)"
          @"(?:^|\s)sh\s+.*-c(?:\s|$)" ]

    let dangerousCommandRegexes =
        simpleDangerousCommands @ complexDangerousRegexes
        |> List.map (fun p ->
            System.Text.RegularExpressions.Regex(
                p,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                ||| System.Text.RegularExpressions.RegexOptions.Compiled
            ))

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
                        dangerousCommandRegexes |> List.tryFind (fun regex -> regex.IsMatch normalized)

                    match matchesRegex with
                    | Some regex ->
                        sprintf "Error: Command matches dangerous pattern: '%s'" (regex.ToString())
                        |> Error
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
