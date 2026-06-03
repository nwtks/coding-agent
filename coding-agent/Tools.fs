namespace CodingAgent

module Tools =
    let readFile fileSystem filePath =
        try
            if not (fileSystem.isPathInWorkspace filePath) then
                Error "Error: Access denied. File is outside the workspace."
            elif not (fileSystem.existsFile filePath) then
                sprintf "Error: File '%s' not found." filePath |> Error
            else
                fileSystem.readFile filePath |> Ok
        with ex ->
            sprintf "Failed reading file '%s': %s" filePath ex.Message |> Error

    let writeFile fileSystem filePath content =
        try
            if not (fileSystem.isPathInWorkspace filePath) then
                Error "Error: Access denied. File is outside the workspace."
            else
                fileSystem.mkdir filePath
                fileSystem.writeFile filePath content
                sprintf "Successfully wrote to '%s'." filePath |> Ok
        with ex ->
            sprintf "Failed writing to file '%s': %s" filePath ex.Message |> Error

    let readStreamLines (reader: System.IO.StreamReader) (token: System.Threading.CancellationToken) =
        task {
            let sb = System.Text.StringBuilder()
            let mutable finished = false

            try
                while not finished && not token.IsCancellationRequested do
                    let! line = reader.ReadLineAsync token

                    if isNull line then
                        finished <- true
                    else
                        sb.AppendLine line |> ignore
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

    let runCommand fileSystem timeoutMs commandLine cwd =
        try
            let wd = fileSystem.workingDir cwd

            if not (fileSystem.isPathInWorkspace wd) then
                Error "Error: Access denied. Working directory is outside the workspace."
            else
                match CommandSafety.validateCommand commandLine with
                | Ok() ->
                    use p = new System.Diagnostics.Process()
                    p.StartInfo <- CommandSafety.processStartInfo commandLine wd
                    p.Start() |> ignore
                    use cts = new System.Threading.CancellationTokenSource(int timeoutMs)
                    let token = cts.Token
                    let outputTask = readStreamLines p.StandardOutput token
                    let errorTask = readStreamLines p.StandardError token

                    try
                        p.WaitForExitAsync(token).GetAwaiter().GetResult()
                        let output = outputTask.GetAwaiter().GetResult()
                        let error = errorTask.GetAwaiter().GetResult()
                        let result = formatCommandResult output error

                        if p.ExitCode = 0 then
                            Ok result
                        else
                            sprintf "Command exited with code %d.\n%s" p.ExitCode result |> Error
                    with :? System.OperationCanceledException ->
                        if not p.HasExited then
                            p.Kill()

                        try
                            System.Threading.Tasks.Task.WhenAll(outputTask, errorTask).Wait 1000 |> ignore
                        with _ ->
                            ()

                        Error "Error: Command timed out."
                | Error err -> Error err
        with ex ->
            sprintf "Failed executing command: %s" ex.Message |> Error

    let listDirectory fileSystem directoryPath =
        try
            let path = fileSystem.workingDir directoryPath

            if not (fileSystem.isPathInWorkspace path) then
                Error "Error: Access denied. Directory is outside the workspace."
            elif not (fileSystem.existsDir path) then
                sprintf "Error: Directory '%s' not found." path |> Error
            else
                let dirLines =
                    fileSystem.dirs path
                    |> Array.map (fun d -> fileSystem.fileName d |> sprintf "[DIR]  %s")

                let fileLines =
                    fileSystem.files path
                    |> Array.map (fun f ->
                        let info = fileSystem.fileInfo f
                        sprintf "[FILE] %s (%d bytes)" (fileSystem.fileName f) info.Length)

                Array.concat [| [| sprintf "Contents of directory '%s':" path |]; dirLines; fileLines |]
                |> String.concat "\n"
                |> Ok
        with ex ->
            sprintf "Failed listing directory '%s': %s" directoryPath ex.Message |> Error

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
        try
            let path = fileSystem.workingDir directoryPath

            if not (fileSystem.isPathInWorkspace path) then
                Error "Error: Access denied. Directory is outside the workspace."
            elif not (fileSystem.existsDir path) then
                sprintf "Error: Directory '%s' not found." path |> Error
            else
                let matches =
                    fileSystem.searchFiles path "*"
                    |> Seq.filter (isIgnored fileSystem >> not)
                    |> Seq.collect (searchInFile fileSystem query path)
                    |> Seq.truncate 100

                if Seq.isEmpty matches then
                    sprintf "No matches found for '%s' in directory '%s'." query path |> Ok
                else
                    sprintf "Found matches for '%s' in directory '%s':\n%s" query path (String.concat "\n" matches)
                    |> Ok
        with ex ->
            sprintf "Failed searching directory '%s': %s" directoryPath ex.Message |> Error

    [<TailCall>]
    let rec countOccurrences (text: string) pattern idx count =
        let nextIdx = text.IndexOf(pattern, idx, System.StringComparison.Ordinal)

        if nextIdx < 0 then
            count
        else
            countOccurrences text pattern (nextIdx + pattern.Length) (count + 1)

    let patchFile fileSystem filePath target replacement =
        try
            if not (fileSystem.isPathInWorkspace filePath) then
                Error "Error: Access denied. File is outside the workspace."
            elif not (fileSystem.existsFile filePath) then
                sprintf "Error: File '%s' not found." filePath |> Error
            else
                let content = fileSystem.readFile filePath
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
                    content.Replace(target, replacement) |> fileSystem.writeFile filePath
                    sprintf "Successfully patched file '%s'." filePath |> Ok
        with ex ->
            sprintf "Failed patching file '%s': %s" filePath ex.Message |> Error

    let readFileLines fileSystem filePath startLine endLine =
        try
            if not (fileSystem.isPathInWorkspace filePath) then
                Error "Error: Access denied. File is outside the workspace."
            elif not (fileSystem.existsFile filePath) then
                sprintf "Error: File '%s' not found." filePath |> Error
            elif startLine > endLine then
                Error "Error: start_line cannot be greater than end_line."
            else
                let actualStart = max 1 startLine
                let skipCount = actualStart - 1
                let takeCount = endLine - actualStart + 1

                fileSystem.readLines filePath
                |> Seq.skip skipCount
                |> Seq.truncate takeCount
                |> String.concat "\n"
                |> Ok
        with ex ->
            sprintf "Failed reading file '%s': %s" filePath ex.Message |> Error

    let findFiles fileSystem pattern directoryPath =
        try
            let path = fileSystem.workingDir directoryPath

            if not (fileSystem.isPathInWorkspace path) then
                Error "Error: Access denied. Directory is outside the workspace."
            elif not (fileSystem.existsDir path) then
                sprintf "Error: Directory '%s' not found." path |> Error
            else
                let files =
                    fileSystem.searchFiles path pattern
                    |> Seq.filter (isIgnored fileSystem >> not)
                    |> Seq.map (fun f -> fileSystem.relativePath path f)

                if Seq.isEmpty files then
                    sprintf "No files matching pattern '%s' found in '%s'." pattern path |> Ok
                else
                    sprintf "Found matches for pattern '%s' in '%s':\n%s" pattern path (String.concat "\n" files)
                    |> Ok
        with ex ->
            sprintf "Failed searching files in '%s': %s" directoryPath ex.Message |> Error
