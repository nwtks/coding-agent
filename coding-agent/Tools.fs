namespace CodingAgent

type CommandResult =
    { ExitCode: int
      Output: string
      Error: string }

module Tools =
    let readFile filePath =
        try
            if System.IO.File.Exists filePath then
                System.IO.File.ReadAllText filePath |> Ok
            else
                sprintf "Error: File '%s' not found." filePath |> Error
        with ex ->
            sprintf "Error reading file '%s': %s" filePath ex.Message |> Error

    let writeFile (filePath: string) (content: string) =
        try
            let dir = System.IO.Path.GetDirectoryName filePath

            if
                not (System.String.IsNullOrWhiteSpace dir)
                && not (System.IO.Directory.Exists dir)
            then
                System.IO.Directory.CreateDirectory dir |> ignore

            System.IO.File.WriteAllText(filePath, content)
            sprintf "Successfully wrote to '%s'." filePath |> Ok
        with ex ->
            sprintf "Error writing to file '%s': %s" filePath ex.Message |> Error

    let runCommand (commandLine: string) cwd =
        try
            let isWindows =
                System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform
                    System.Runtime.InteropServices.OSPlatform.Windows

            let fileName, arguments =
                if isWindows then
                    "cmd.exe", sprintf "/c \"%s\"" commandLine
                else
                    "bash", sprintf "-c \"%s\"" (commandLine.Replace("\"", "\\\""))

            let workingDir =
                if System.String.IsNullOrWhiteSpace cwd then
                    System.Environment.CurrentDirectory
                else
                    cwd

            let startInfo = System.Diagnostics.ProcessStartInfo()
            startInfo.FileName <- fileName
            startInfo.Arguments <- arguments
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.UseShellExecute <- false
            startInfo.CreateNoWindow <- true
            startInfo.WorkingDirectory <- workingDir

            use p = new System.Diagnostics.Process()
            p.StartInfo <- startInfo
            p.Start() |> ignore

            let outputTask = p.StandardOutput.ReadToEndAsync()
            let errorTask = p.StandardError.ReadToEndAsync()

            let completed = p.WaitForExit 30000

            if not completed then
                p.Kill()
                Error "Error: Command timed out."
            else
                let output = outputTask.Result
                let error = errorTask.Result

                let resultStr =
                    (if not (System.String.IsNullOrWhiteSpace output) then
                         "Output:\n" + output + "\n"
                     else
                         "")
                    + if not (System.String.IsNullOrWhiteSpace error) then
                          "Error:\n" + error + "\n"
                      else
                          ""

                if p.ExitCode <> 0 then
                    sprintf "Command exited with code %d.\n%s" p.ExitCode resultStr |> Error
                else
                    Ok resultStr
        with ex ->
            sprintf "Error executing command: %s" ex.Message |> Error

    let listDirectory directoryPath =
        try
            let path =
                if System.String.IsNullOrWhiteSpace directoryPath then
                    System.Environment.CurrentDirectory
                else
                    directoryPath

            if System.IO.Directory.Exists path then
                let dirs = System.IO.Directory.GetDirectories path
                let files = System.IO.Directory.GetFiles path

                let formatPath (p: string) = System.IO.Path.GetFileName p

                let dirLines = dirs |> Array.map (fun d -> sprintf "[DIR]  %s" (formatPath d))

                let fileLines =
                    files
                    |> Array.map (fun f ->
                        let info = System.IO.FileInfo f
                        sprintf "[FILE] %s (%d bytes)" (formatPath f) info.Length)

                let result =
                    Array.concat [| [| sprintf "Contents of directory '%s':" path |]; dirLines; fileLines |]
                    |> String.concat "\n"

                Ok result
            else
                sprintf "Error: Directory '%s' not found." path |> Error
        with ex ->
            sprintf "Error listing directory '%s': %s" directoryPath ex.Message |> Error
