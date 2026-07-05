namespace CodingAgent

type Tools =
    { readFile: string -> Result<string, string>
      writeFile: string -> string -> Result<string, string>
      runCommand: string -> string -> Async<Result<string, string>>
      listDirectory: string -> Result<string, string>
      grepSearch: string -> string -> Result<string, string>
      patchFile: string -> string -> string -> Result<string, string>
      readFileLines: string -> int -> int -> Result<string, string>
      findFiles: string -> string -> Result<string, string>
      moveFile: string -> string -> bool -> Result<string, string> }

module Tools =
    let resolvePathInWorkspace (fileSystem: FileSystem) filePath =
        let resolved = fileSystem.resolvePath filePath

        if not (fileSystem.isPathInWorkspace resolved) then
            $"Access denied. File '{filePath}' is outside the workspace." |> Error
        else
            Ok resolved

    let withExistingFile (fileSystem: FileSystem) filePath operation =
        try
            match resolvePathInWorkspace fileSystem filePath with
            | Ok resolvedPath ->
                if not (fileSystem.existsFile resolvedPath) then
                    $"File '{filePath}' not found." |> Error
                else
                    operation filePath resolvedPath
            | Error err -> Error err
        with ex ->
            $"Failed operating on file '{filePath}': {ex.Message}" |> Error

    let withExistingDir (fileSystem: FileSystem) dirPath operation =
        try
            let path = fileSystem.workingDir dirPath

            match resolvePathInWorkspace fileSystem path with
            | Ok resolvedPath ->
                if not (fileSystem.existsDir resolvedPath) then
                    $"Directory '{path}' not found." |> Error
                else
                    operation path resolvedPath
            | Error err -> Error err
        with ex ->
            $"Failed operating on directory '{dirPath}': {ex.Message}" |> Error

    let checkFileSize (fileSystem: FileSystem) resolvedPath maxFileSizeBytes =
        if maxFileSizeBytes > 0L then
            let info = fileSystem.fileInfo resolvedPath

            if info.Length > maxFileSizeBytes then
                $"File '{resolvedPath}' is too large ({info.Length} bytes). Maximum allowed size is {maxFileSizeBytes} bytes."
                |> Error
            else
                Ok()
        else
            Ok()

    let readFile fileSystem maxFileSizeBytes filePath =
        withExistingFile fileSystem filePath (fun _ resolvedPath ->
            match checkFileSize fileSystem resolvedPath maxFileSizeBytes with
            | Ok() -> fileSystem.readFile resolvedPath |> Ok
            | Error err -> Error err)

    let checkContentSize maxFileSizeBytes (content: string) =
        if maxFileSizeBytes > 0L then
            let contentBytes = System.Text.Encoding.UTF8.GetByteCount content

            if int64 contentBytes > maxFileSizeBytes then
                $"Content too large ({contentBytes} bytes). Maximum allowed size is {maxFileSizeBytes} bytes."
                |> Error
                |> Some
            else
                None
        else
            None

    let writeFile fileSystem maxFileSizeBytes filePath content =
        try
            match checkContentSize maxFileSizeBytes content with
            | Some err -> err
            | None ->
                match resolvePathInWorkspace fileSystem filePath with
                | Ok resolvedPath ->
                    fileSystem.createParentDirectory resolvedPath
                    fileSystem.writeFile resolvedPath content
                    $"Successfully wrote to '{filePath}'." |> Ok
                | Error err -> Error err
        with ex ->
            $"Failed writing to file '{filePath}': {ex.Message}" |> Error

    let defaultMaxLineLength = 100000

    let truncateLine (line: string) maxLineLength =
        let ellipsis = "... [line truncated]"

        if maxLineLength > 0 && line.Length > maxLineLength then
            line.Substring(0, maxLineLength) + ellipsis
        else
            line

    let appendLineToBuilder (sb: System.Text.StringBuilder) (line: string) firstLine =
        if not firstLine then
            sb.AppendLine() |> ignore

        sb.Append line |> ignore

    let exceedsOutputLimit totalBytes lineBytes maxOutputBytes = totalBytes + lineBytes > maxOutputBytes

    let appendTruncationMessage (sb: System.Text.StringBuilder) firstLine =
        appendLineToBuilder sb "\n... [Output truncated. Maximum allowed size exceeded.]" firstLine
        sb.ToString()

    let rec readLoop
        (reader: System.IO.StreamReader)
        (sb: System.Text.StringBuilder)
        (token: System.Threading.CancellationToken)
        maxOutputBytes
        maxLineLength
        firstLine
        totalBytes
        =
        async {
            if token.IsCancellationRequested then
                return sb.ToString()
            else
                let! line = reader.ReadLineAsync(token).AsTask() |> Async.AwaitTask

                if isNull line then
                    return sb.ToString()
                else
                    let effectiveLine = truncateLine line maxLineLength
                    let lineBytes = System.Text.Encoding.UTF8.GetByteCount effectiveLine

                    if exceedsOutputLimit totalBytes lineBytes maxOutputBytes then
                        return appendTruncationMessage sb firstLine
                    else
                        appendLineToBuilder sb effectiveLine firstLine
                        return! readLoop reader sb token maxOutputBytes maxLineLength false (totalBytes + lineBytes)
        }

    let readStreamLines
        (reader: System.IO.StreamReader)
        maxOutputBytes
        maxLineLength
        (token: System.Threading.CancellationToken)
        =
        async {
            let sb = System.Text.StringBuilder 4096

            try
                return! readLoop reader sb token maxOutputBytes maxLineLength false 0
            with :? System.OperationCanceledException ->
                return sb.ToString()
        }

    let formatCommandResult output error =
        [| if not (System.String.IsNullOrWhiteSpace output) then
               $"Output:\n{output}\n"
           if not (System.String.IsNullOrWhiteSpace error) then
               $"Error:\n{error}\n" |]
        |> String.concat ""

    let validateCommandDir fileSystem commandLine cwd =
        withExistingDir fileSystem cwd (fun _ resolvedWd ->
            match CommandSafety.validateCommand commandLine with
            | Ok() -> Ok resolvedWd
            | Error err -> Error err)

    let createProcess sandboxMode workspaceRoot commandLine resolvedWd =
        let p = new System.Diagnostics.Process()
        p.StartInfo <- CommandSafety.processStartInfo sandboxMode workspaceRoot commandLine resolvedWd
        p.Start() |> ignore
        p

    let killProcess (p: System.Diagnostics.Process) =
        if not p.HasExited then
            try
                p.Kill()
            with _ ->
                ()

            try
                p.WaitForExit 1000 |> ignore
            with _ ->
                ()

            if not p.HasExited then
                try
                    p.Kill true
                with _ ->
                    ()

    let executeProcess (p: System.Diagnostics.Process) maxOutputBytes (token: System.Threading.CancellationToken) =
        async {
            let outputAsync =
                readStreamLines p.StandardOutput maxOutputBytes defaultMaxLineLength token

            let errorAsync =
                readStreamLines p.StandardError maxOutputBytes defaultMaxLineLength token

            let! outputHandle = outputAsync |> Async.StartChild
            let! errorHandle = errorAsync |> Async.StartChild
            do! p.WaitForExitAsync token |> Async.AwaitTask
            let! output = outputHandle
            let! error = errorHandle
            return struct (output, error, p.ExitCode)
        }

    let runCommand fileSystem maxOutputBytes timeoutMs sandboxMode workspaceRoot commandLine cwd =
        async {
            match validateCommandDir fileSystem commandLine cwd with
            | Ok resolvedWd ->
                let p = createProcess sandboxMode workspaceRoot commandLine resolvedWd
                use cts = new System.Threading.CancellationTokenSource(int timeoutMs)
                let token = cts.Token
                token.Register(fun () -> killProcess p) |> ignore

                try
                    let! struct (output, error, exitCode) = executeProcess p maxOutputBytes token
                    let result = formatCommandResult output error

                    if exitCode = 0 then
                        return Ok result
                    else
                        return $"Command exited with code {exitCode}.\n{result}" |> Error
                with :? System.OperationCanceledException ->
                    return Error "Command timed out."
            | Error err -> return Error err
        }

    let formatDirectoryListing path dirLines fileLines =
        Array.concat [| [| $"Contents of directory '{path}':" |]; dirLines; fileLines |]
        |> String.concat "\n"
        |> Ok

    let listDirectory fileSystem directoryPath =
        withExistingDir fileSystem directoryPath (fun path resolvedPath ->
            let dirLines =
                fileSystem.dirs resolvedPath
                |> Array.map (fun d -> $"[DIR]  {fileSystem.fileName d}")

            fileSystem.files resolvedPath
            |> Array.map (fun f ->
                let info = fileSystem.fileInfo f
                $"[FILE] {fileSystem.fileName f} ({info.Length} bytes)")
            |> formatDirectoryListing path dirLines)

    let isIgnoredPart part =
        part = ".git" || part = "bin" || part = "obj" || part = "node_modules"

    let isIgnored fileSystem filePath =
        let relativePath = fileSystem.relativePath fileSystem.workspaceRoot filePath

        relativePath.Split System.IO.Path.DirectorySeparatorChar
        |> Array.exists isIgnoredPart

    let searchInFile fileSystem (query: string) path file =
        try
            fileSystem.readLines file
            |> Seq.mapi (fun idx line -> idx + 1, line)
            |> Seq.filter (fun (_, line) -> line.Contains(query, System.StringComparison.OrdinalIgnoreCase))
            |> Seq.map (fun (lineNum, line) ->
                let relativePath = fileSystem.relativePath path file
                $"{relativePath}:{lineNum}: {truncateLine (line.Trim()) defaultMaxLineLength}")
        with ex ->
            let relativePath =
                try
                    fileSystem.relativePath path file
                with _ ->
                    file

            seq { $"⚠️  Warning: Skipped unreadable file '{relativePath}': {ex.Message}" }

    let formatGrepResults query path maxDisplay warnings matches =
        let truncatedMatches = matches |> List.truncate maxDisplay
        let exceedsLimit = matches.Length > maxDisplay
        let parts = System.Collections.Generic.List<string>()

        if not (List.isEmpty warnings) then
            warnings |> String.concat "\n" |> parts.Add

        if List.isEmpty matches then
            $"No matches found for '{query}' in directory '{path}'." |> parts.Add
        elif exceedsLimit then
            $"Found matches for '{query}' in directory '{path}' (showing first {maxDisplay} of more than {maxDisplay}):"
            |> parts.Add
        else
            $"Found matches for '{query}' in directory '{path}':" |> parts.Add

        if not (List.isEmpty truncatedMatches) then
            truncatedMatches |> String.concat "\n" |> parts.Add

        parts |> String.concat "\n" |> Ok

    let grepSearch fileSystem maxDisplay query directoryPath =
        withExistingDir fileSystem directoryPath (fun path resolvedPath ->
            let rawResults =
                fileSystem.searchFiles resolvedPath "*"
                |> Seq.filter (isIgnored fileSystem >> not)
                |> Seq.collect (searchInFile fileSystem query resolvedPath)

            let warnings = rawResults |> Seq.filter (fun s -> s.StartsWith "⚠️") |> Seq.toList

            rawResults
            |> Seq.filter (fun s -> not (s.StartsWith "⚠️"))
            |> Seq.truncate (maxDisplay + 1)
            |> Seq.toList
            |> formatGrepResults query path maxDisplay warnings)

    [<TailCall>]
    let rec countOccurrences (text: string) pattern idx count =
        let nextIdx = text.IndexOf(pattern, idx, System.StringComparison.Ordinal)

        if nextIdx < 0 then
            count
        else
            countOccurrences text pattern (nextIdx + pattern.Length) (count + 1)

    let patchFile fileSystem filePath target replacement =
        withExistingFile fileSystem filePath (fun filePath resolvedPath ->
            let content = fileSystem.readFile resolvedPath
            let occurrences = countOccurrences content target 0 0

            if occurrences = 0 then
                $"Target content to patch not found in file '{filePath}'." |> Error
            elif occurrences > 1 then
                $"Target content found {occurrences} times in file '{filePath}'. Target must be unique. Provide more context (surrounding lines) to uniquely identify the section to patch."
                |> Error
            else
                content.Replace(target, replacement) |> fileSystem.writeFile resolvedPath
                $"Successfully patched file '{filePath}'." |> Ok)

    let checkLineRange startLine endLine =
        if startLine < 1 then
            $"start_line must be >= 1, but got {startLine}." |> Error
        elif endLine < 1 then
            $"end_line must be >= 1, but got {endLine}." |> Error
        elif startLine > endLine then
            Error "start_line cannot be greater than end_line."
        else
            Ok()

    let readFileLines fileSystem maxFileSizeBytes filePath startLine endLine =
        withExistingFile fileSystem filePath (fun _ resolvedPath ->
            match checkFileSize fileSystem resolvedPath maxFileSizeBytes with
            | Ok() ->
                match checkLineRange startLine endLine with
                | Ok() ->
                    let skipCount = startLine - 1
                    let takeCount = endLine - startLine + 1

                    fileSystem.readLines resolvedPath
                    |> Seq.mapi (fun i x -> i, x)
                    |> Seq.skipWhile (fun (i, _) -> i < skipCount)
                    |> Seq.map snd
                    |> Seq.truncate takeCount
                    |> String.concat "\n"
                    |> Ok
                | Error err -> Error err
            | Error err -> Error err)

    let formatFindResults pattern path maxDisplay allFiles =
        if List.isEmpty allFiles then
            $"No files matching pattern '{pattern}' found in '{path}'." |> Ok
        else
            let exceedsLimit = allFiles.Length > maxDisplay
            let displayLines = allFiles |> List.truncate maxDisplay |> String.concat "\n"

            if exceedsLimit then
                $"Found matches for pattern '{pattern}' in '{path}' (showing first {maxDisplay} of more than {maxDisplay}):\n{displayLines}"
                |> Ok
            else
                $"Found matches for pattern '{pattern}' in '{path}':\n{displayLines}" |> Ok

    let findFiles fileSystem maxDisplay pattern directoryPath =
        withExistingDir fileSystem directoryPath (fun path resolvedPath ->
            fileSystem.searchFiles resolvedPath pattern
            |> Seq.filter (isIgnored fileSystem >> not)
            |> Seq.map (fun f -> fileSystem.relativePath resolvedPath f)
            |> Seq.truncate (maxDisplay + 1)
            |> Seq.toList
            |> formatFindResults pattern path maxDisplay)

    let trashDir = ".agents/trash"

    let moveFile fileSystem source destination overwrite =
        try
            match withExistingFile fileSystem source (fun _ resolvedPath -> Ok resolvedPath) with
            | Error e -> Error e
            | Ok sourcePath ->
                match resolvePathInWorkspace fileSystem destination with
                | Error e -> Error e
                | Ok destPath ->
                    if fileSystem.existsFile destPath && not overwrite then
                        $"Destination '{destination}' already exists. Set overwrite=true to replace."
                        |> Error
                    else
                        if fileSystem.existsFile destPath && overwrite then
                            let timestamp = System.DateTime.UtcNow.ToString "yyyyMMdd-HHmmss"
                            let fileName = fileSystem.fileName destPath
                            let trashPath = System.IO.Path.Combine(trashDir, $"{timestamp}_{fileName}")
                            fileSystem.createParentDirectory trashPath
                            fileSystem.moveFile destPath trashPath

                        fileSystem.moveFile sourcePath destPath
                        $"Successfully moved '{source}' to '{destination}'." |> Ok
        with ex ->
            $"Failed to move file '{source}' to '{destination}': {ex.Message}" |> Error
