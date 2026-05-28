namespace CodingAgent

module Tools =
    let workspaceRoot = System.IO.Path.GetFullPath System.Environment.CurrentDirectory

    let isPathInWorkspace path =
        if System.String.IsNullOrWhiteSpace path then
            true
        else
            try
                let fullPath = System.IO.Path.GetFullPath path
                fullPath.StartsWith(workspaceRoot, System.StringComparison.OrdinalIgnoreCase)
            with _ ->
                true

    let readFile filePath =
        try
            if not (isPathInWorkspace filePath) then
                Error "Error: Access denied. File is outside the workspace."
            elif not (System.IO.File.Exists filePath) then
                sprintf "Error: File '%s' not found." filePath |> Error
            else
                System.IO.File.ReadAllText(filePath, System.Text.Encoding.UTF8) |> Ok
        with ex ->
            sprintf "Error reading file '%s': %s" filePath ex.Message |> Error

    let mkdir (path: string) =
        let dir = System.IO.Path.GetDirectoryName path

        if
            not (System.String.IsNullOrWhiteSpace dir)
            && not (System.IO.Directory.Exists dir)
        then
            System.IO.Directory.CreateDirectory dir |> ignore

    let writeFile filePath content =
        try
            if not (isPathInWorkspace filePath) then
                Error "Error: Access denied. File is outside the workspace."
            else
                mkdir filePath
                System.IO.File.WriteAllText(filePath, content |> string, System.Text.Encoding.UTF8)
                sprintf "Successfully wrote to '%s'." filePath |> Ok
        with ex ->
            sprintf "Error writing to file '%s': %s" filePath ex.Message |> Error

    let workingDir dir =
        if System.String.IsNullOrWhiteSpace dir then
            System.Environment.CurrentDirectory
        else
            dir

    let processStartInfo commandLine cwd =
        let laucher cmd =
            let isWindows =
                System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform
                    System.Runtime.InteropServices.OSPlatform.Windows

            if isWindows then
                "cmd.exe", sprintf "/c \"%s\"" cmd
            else
                "bash", sprintf "-c \"%s\"" (cmd.Replace("\"", "\\\""))

        let fileName, arguments = laucher commandLine
        let startInfo = System.Diagnostics.ProcessStartInfo()
        startInfo.FileName <- fileName
        startInfo.Arguments <- arguments
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.UseShellExecute <- false
        startInfo.CreateNoWindow <- true
        startInfo.WorkingDirectory <- cwd
        startInfo

    let runCommand commandLine cwd =
        let formatResult output error =
            (if not (System.String.IsNullOrWhiteSpace output) then
                 "Output:\n" + output + "\n"
             else
                 "")
            + if not (System.String.IsNullOrWhiteSpace error) then
                  "Error:\n" + error + "\n"
              else
                  ""

        try
            let wd = workingDir cwd

            if not (isPathInWorkspace wd) then
                Error "Error: Access denied. Working directory is outside the workspace."
            else
                use p = new System.Diagnostics.Process()
                p.StartInfo <- processStartInfo commandLine wd
                p.Start() |> ignore
                let outputTask = p.StandardOutput.ReadToEndAsync()
                let errorTask = p.StandardError.ReadToEndAsync()
                let completed = p.WaitForExit 60000

                if completed then
                    let result = formatResult outputTask.Result errorTask.Result

                    if p.ExitCode = 0 then
                        Ok result
                    else
                        sprintf "Command exited with code %d.\n%s" p.ExitCode result |> Error
                else
                    p.Kill()
                    Error "Error: Command timed out."
        with ex ->
            sprintf "Error executing command: %s" ex.Message |> Error

    let listDirectory directoryPath =
        let formatPath (p: string) = System.IO.Path.GetFileName p

        try
            let path = workingDir directoryPath

            if not (isPathInWorkspace path) then
                Error "Error: Access denied. Directory is outside the workspace."
            elif not (System.IO.Directory.Exists path) then
                sprintf "Error: Directory '%s' not found." path |> Error
            else
                let dirLines =
                    System.IO.Directory.GetDirectories path
                    |> Array.map (fun d -> formatPath d |> sprintf "[DIR]  %s")

                let fileLines =
                    System.IO.Directory.GetFiles path
                    |> Array.map (fun f ->
                        let info = System.IO.FileInfo f
                        sprintf "[FILE] %s (%d bytes)" (formatPath f) info.Length)

                Array.concat [| [| sprintf "Contents of directory '%s':" path |]; dirLines; fileLines |]
                |> String.concat "\n"
                |> Ok
        with ex ->
            sprintf "Error listing directory '%s': %s" directoryPath ex.Message |> Error

    let isIgnored filePath =
        let relativePath = System.IO.Path.GetRelativePath(workspaceRoot, filePath)

        relativePath.Split System.IO.Path.DirectorySeparatorChar
        |> Array.exists (fun part -> part = ".git" || part = "bin" || part = "obj" || part = "node_modules")

    let searchInFile (query: string) path file =
        try
            System.IO.File.ReadLines(file, System.Text.Encoding.UTF8)
            |> Seq.mapi (fun idx line -> idx + 1, line)
            |> Seq.filter (fun (_, line) -> line.Contains(query, System.StringComparison.OrdinalIgnoreCase))
            |> Seq.map (fun (lineNum, line) ->
                let relativePath = System.IO.Path.GetRelativePath(path, file)
                sprintf "%s:%d: %s" relativePath lineNum (line.Trim()))
        with _ ->
            Seq.empty

    let grepSearch query directoryPath =
        try
            let path = workingDir directoryPath

            if not (isPathInWorkspace path) then
                Error "Error: Access denied. Directory is outside the workspace."
            elif not (System.IO.Directory.Exists path) then
                sprintf "Error: Directory '%s' not found." path |> Error
            else
                let matches =
                    System.IO.Directory.EnumerateFiles(path, "*", System.IO.SearchOption.AllDirectories)
                    |> Seq.filter (isIgnored >> not)
                    |> Seq.collect (searchInFile query path)
                    |> Seq.truncate 100

                if Seq.isEmpty matches then
                    sprintf "No matches found for '%s' in directory '%s'." query path |> Ok
                else
                    sprintf "Found matches for '%s' in directory '%s':\n%s" query path (String.concat "\n" matches)
                    |> Ok
        with ex ->
            sprintf "Error searching directory '%s': %s" directoryPath ex.Message |> Error

    let patchFile filePath (target: string) replacement =
        try
            if not (isPathInWorkspace filePath) then
                Error "Error: Access denied. File is outside the workspace."
            elif not (System.IO.File.Exists filePath) then
                sprintf "Error: File '%s' not found." filePath |> Error
            else
                let content = System.IO.File.ReadAllText(filePath, System.Text.Encoding.UTF8)

                if content.Contains target then
                    let newContent = content.Replace(target, replacement)
                    System.IO.File.WriteAllText(filePath, newContent, System.Text.Encoding.UTF8)
                    sprintf "Successfully patched file '%s'." filePath |> Ok
                else
                    sprintf "Error: Target content to patch not found in file '%s'." filePath
                    |> Error
        with ex ->
            sprintf "Error patching file '%s': %s" filePath ex.Message |> Error

    let readFileLines filePath startLine endLine =
        try
            if not (isPathInWorkspace filePath) then
                Error "Error: Access denied. File is outside the workspace."
            elif not (System.IO.File.Exists filePath) then
                sprintf "Error: File '%s' not found." filePath |> Error
            elif startLine > endLine then
                Error "Error: start_line cannot be greater than end_line."
            else
                let actualStart = max 1 startLine

                System.IO.File.ReadLines(filePath, System.Text.Encoding.UTF8)
                |> Seq.indexed
                |> Seq.filter (fun (idx, _) ->
                    let lineNum = idx + 1
                    lineNum >= actualStart && lineNum <= endLine)
                |> Seq.map snd
                |> String.concat "\n"
                |> Ok
        with ex ->
            sprintf "Error reading file '%s': %s" filePath ex.Message |> Error

    let findFiles pattern directoryPath =
        try
            let path = workingDir directoryPath

            if not (isPathInWorkspace path) then
                Error "Error: Access denied. Directory is outside the workspace."
            elif not (System.IO.Directory.Exists path) then
                sprintf "Error: Directory '%s' not found." path |> Error
            else
                let files =
                    System.IO.Directory.EnumerateFiles(path, pattern, System.IO.SearchOption.AllDirectories)
                    |> Seq.filter (isIgnored >> not)
                    |> Seq.map (fun f -> System.IO.Path.GetRelativePath(path, f))

                if Seq.isEmpty files then
                    sprintf "No files matching pattern '%s' found in '%s'." pattern path |> Ok
                else
                    sprintf "Found matches for pattern '%s' in '%s':\n%s" pattern path (String.concat "\n" files)
                    |> Ok
        with ex ->
            sprintf "Error searching files in '%s': %s" directoryPath ex.Message |> Error
