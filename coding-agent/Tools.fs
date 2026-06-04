namespace CodingAgent

module Tools =
    let resolvePathInWorkspace (fileSystem: FileSystem) filePath =
        let resolved = fileSystem.resolvePath filePath

        if not (fileSystem.isPathInWorkspace resolved) then
            sprintf "Error: Access denied. File '%s' is outside the workspace." filePath
            |> Error
        else
            Ok resolved

    let withExistingFile (fileSystem: FileSystem) filePath operation =
        try
            match resolvePathInWorkspace fileSystem filePath with
            | Error err -> Error err
            | Ok resolvedPath ->
                if not (fileSystem.existsFile resolvedPath) then
                    sprintf "Error: File '%s' not found." filePath |> Error
                else
                    operation filePath resolvedPath
        with ex ->
            sprintf "Failed operating on file '%s': %s" filePath ex.Message |> Error

    let withExistingDir (fileSystem: FileSystem) dirPath operation =
        try
            let path = fileSystem.workingDir dirPath

            match resolvePathInWorkspace fileSystem path with
            | Error err -> Error err
            | Ok resolvedPath ->
                if not (fileSystem.existsDir resolvedPath) then
                    sprintf "Error: Directory '%s' not found." path |> Error
                else
                    operation path resolvedPath
        with ex ->
            sprintf "Failed operating on directory '%s': %s" dirPath ex.Message |> Error

    let checkFileSize (fileSystem: FileSystem) resolvedPath maxFileSizeBytes =
        if maxFileSizeBytes > 0L then
            let info = fileSystem.fileInfo resolvedPath

            if info.Length > maxFileSizeBytes then
                sprintf
                    "Error: File '%s' is too large (%d bytes). Maximum allowed size is %d bytes."
                    resolvedPath
                    info.Length
                    maxFileSizeBytes
                |> Error
                |> Some
            else
                None
        else
            None

    let readFile fileSystem maxFileSizeBytes filePath =
        withExistingFile fileSystem filePath (fun _ resolvedPath ->
            match checkFileSize fileSystem resolvedPath maxFileSizeBytes with
            | Some err -> err
            | None -> fileSystem.readFile resolvedPath |> Ok)

    let writeFile fileSystem maxFileSizeBytes filePath content =
        try
            let sizeError =
                if maxFileSizeBytes > 0L then
                    let contentBytes = System.Text.Encoding.UTF8.GetByteCount(content: string)

                    if int64 contentBytes > maxFileSizeBytes then
                        sprintf
                            "Error: Content too large (%d bytes). Maximum allowed size is %d bytes."
                            contentBytes
                            maxFileSizeBytes
                        |> Error
                        |> Some
                    else
                        None
                else
                    None

            match sizeError with
            | Some err -> err
            | None ->
                match resolvePathInWorkspace fileSystem filePath with
                | Error err -> Error err
                | Ok resolvedPath ->
                    fileSystem.mkdir resolvedPath
                    fileSystem.writeFile resolvedPath content
                    sprintf "Successfully wrote to '%s'." filePath |> Ok
        with ex ->
            sprintf "Failed writing to file '%s': %s" filePath ex.Message |> Error

    let readStreamLines (reader: System.IO.StreamReader) maxOutputBytes (token: System.Threading.CancellationToken) =
        task {
            let sb = System.Text.StringBuilder 4096
            let mutable finished = false
            let mutable hasContent = false
            let mutable totalBytes = 0

            try
                while not finished && not token.IsCancellationRequested do
                    let! line = reader.ReadLineAsync token

                    if isNull line then
                        finished <- true
                    else
                        let lineBytes = System.Text.Encoding.UTF8.GetByteCount(line)

                        if totalBytes + lineBytes > maxOutputBytes then
                            if hasContent then
                                sb.AppendLine() |> ignore

                            sb.Append "\n... [Output truncated. Maximum allowed size exceeded.]" |> ignore
                            finished <- true
                        else
                            if hasContent then
                                sb.AppendLine() |> ignore

                            sb.Append line |> ignore
                            hasContent <- true
                            totalBytes <- totalBytes + lineBytes
            with
            | :? System.OperationCanceledException
            | :? System.Threading.Tasks.TaskCanceledException -> ()

            return sb.ToString()
        }

    let formatCommandResult output error =
        [| if not (System.String.IsNullOrWhiteSpace output) then
               sprintf "Output:\n%s\n" output
           if not (System.String.IsNullOrWhiteSpace error) then
               sprintf "Error:\n%s\n" error |]
        |> String.concat ""

    let runCommand fileSystem sandboxMode workspaceRoot maxOutputBytes timeoutMs commandLine cwd =
        withExistingDir fileSystem cwd (fun _ resolvedWd ->
            match CommandSafety.validateCommand commandLine with
            | Ok() ->
                let pipeline =
                    task {
                        use p = new System.Diagnostics.Process()
                        p.StartInfo <- Sandbox.sandboxedStartInfo sandboxMode workspaceRoot commandLine resolvedWd
                        CommandSafety.sanitizeEnvironment p.StartInfo
                        p.Start() |> ignore
                        use cts = new System.Threading.CancellationTokenSource(int timeoutMs)
                        let token = cts.Token
                        let outputTask = readStreamLines p.StandardOutput maxOutputBytes token
                        let errorTask = readStreamLines p.StandardError maxOutputBytes token

                        try
                            do! p.WaitForExitAsync token
                            let! output = outputTask
                            let! error = errorTask
                            let result = formatCommandResult output error

                            if p.ExitCode = 0 then
                                return Ok result
                            else
                                return sprintf "Command exited with code %d.\n%s" p.ExitCode result |> Error
                        with :? System.OperationCanceledException ->
                            if not p.HasExited then
                                p.Kill true

                            try
                                let timeout = System.TimeSpan.FromSeconds 1.0
                                let! _ = System.Threading.Tasks.Task.WhenAll(outputTask, errorTask).WaitAsync timeout

                                return Error "Error: Command timed out."
                            with _ ->
                                return Error "Error: Command timed out."
                    }

                pipeline.GetAwaiter().GetResult()
            | Error err -> Error err)

    let listDirectory fileSystem directoryPath =
        withExistingDir fileSystem directoryPath (fun path resolvedPath ->
            let dirLines =
                fileSystem.dirs resolvedPath
                |> Array.map (fun d -> fileSystem.fileName d |> sprintf "[DIR]  %s")

            let fileLines =
                fileSystem.files resolvedPath
                |> Array.map (fun f ->
                    let info = fileSystem.fileInfo f
                    sprintf "[FILE] %s (%d bytes)" (fileSystem.fileName f) info.Length)

            Array.concat [| [| sprintf "Contents of directory '%s':" path |]; dirLines; fileLines |]
            |> String.concat "\n"
            |> Ok)

    let isIgnored fileSystem filePath =
        let relativePath = fileSystem.relativePath fileSystem.workspaceRoot filePath

        relativePath.Split System.IO.Path.DirectorySeparatorChar
        |> Array.exists (fun part -> part = ".git" || part = "bin" || part = "obj" || part = "node_modules")

    let searchInFile fileSystem (query: string) path file =
        try
            fileSystem.readLines file
            |> Seq.mapi (fun idx line -> idx + 1, line)
            |> Seq.filter (fun (_, line) -> line.Contains(query, System.StringComparison.OrdinalIgnoreCase))
            |> Seq.map (fun (lineNum, line) ->
                let relativePath = fileSystem.relativePath path file
                sprintf "%s:%d: %s" relativePath lineNum (line.Trim()))
        with _ ->
            Seq.empty

    let grepSearch fileSystem query directoryPath =
        withExistingDir fileSystem directoryPath (fun path resolvedPath ->
            let maxDisplay = 100

            let allMatches =
                fileSystem.searchFiles resolvedPath "*"
                |> Seq.filter (isIgnored fileSystem >> not)
                |> Seq.collect (searchInFile fileSystem query resolvedPath)
                |> Seq.truncate (maxDisplay + 1)
                |> Seq.toList

            if List.isEmpty allMatches then
                sprintf "No matches found for '%s' in directory '%s'." query path |> Ok
            else
                let exceedsLimit = allMatches.Length > maxDisplay
                let displayLines = allMatches |> List.truncate maxDisplay |> String.concat "\n"

                if exceedsLimit then
                    sprintf
                        "Found matches for '%s' in directory '%s' (showing first %d of more than %d):\n%s"
                        query
                        path
                        maxDisplay
                        maxDisplay
                        displayLines
                    |> Ok
                else
                    sprintf "Found matches for '%s' in directory '%s':\n%s" query path displayLines
                    |> Ok)

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
                sprintf "Error: Target content to patch not found in file '%s'." filePath
                |> Error
            elif occurrences > 1 then
                sprintf
                    "Error: Target content found %d times in file '%s'. Target must be unique. Provide more context (surrounding lines) to uniquely identify the section to patch."
                    occurrences
                    filePath
                |> Error
            else
                content.Replace(target, replacement) |> fileSystem.writeFile resolvedPath
                sprintf "Successfully patched file '%s'." filePath |> Ok)

    let checkLineRange startLine endLine =
        if startLine < 1 then
            sprintf "Error: start_line must be >= 1, but got %d." startLine |> Error |> Some
        elif endLine < 1 then
            sprintf "Error: end_line must be >= 1, but got %d." endLine |> Error |> Some
        elif startLine > endLine then
            Error "Error: start_line cannot be greater than end_line." |> Some
        else
            None

    let readFileLines fileSystem maxFileSizeBytes filePath startLine endLine =
        withExistingFile fileSystem filePath (fun _ resolvedPath ->
            match checkFileSize fileSystem resolvedPath maxFileSizeBytes with
            | Some err -> err
            | None ->
                match checkLineRange startLine endLine with
                | Some err -> err
                | None ->
                    let skipCount = startLine - 1
                    let takeCount = endLine - startLine + 1

                    fileSystem.readLines resolvedPath
                    |> Seq.skip skipCount
                    |> Seq.truncate takeCount
                    |> String.concat "\n"
                    |> Ok)

    let findFiles fileSystem pattern directoryPath =
        withExistingDir fileSystem directoryPath (fun path resolvedPath ->
            let maxDisplay = 100

            let allFiles =
                fileSystem.searchFiles resolvedPath pattern
                |> Seq.filter (isIgnored fileSystem >> not)
                |> Seq.map (fun f -> fileSystem.relativePath resolvedPath f)
                |> Seq.truncate (maxDisplay + 1)
                |> Seq.toList

            if List.isEmpty allFiles then
                sprintf "No files matching pattern '%s' found in '%s'." pattern path |> Ok
            else
                let exceedsLimit = allFiles.Length > maxDisplay
                let displayLines = allFiles |> List.truncate maxDisplay |> String.concat "\n"

                if exceedsLimit then
                    sprintf
                        "Found matches for pattern '%s' in '%s' (showing first %d of more than %d):\n%s"
                        pattern
                        path
                        maxDisplay
                        maxDisplay
                        displayLines
                    |> Ok
                else
                    sprintf "Found matches for pattern '%s' in '%s':\n%s" pattern path displayLines
                    |> Ok)
