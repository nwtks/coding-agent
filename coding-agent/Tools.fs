namespace CodingAgent

module Tools =
    let readFile filePath =
        try
            if System.IO.File.Exists filePath then
                System.IO.File.ReadAllText(filePath, System.Text.Encoding.UTF8) |> Ok
            else
                sprintf "Error: File '%s' not found." filePath |> Error
        with ex ->
            sprintf "Error reading file '%s': %s" filePath ex.Message |> Error

    let writeFile filePath content =
        let mkdir (path: string) =
            let dir = System.IO.Path.GetDirectoryName path

            if
                not (System.String.IsNullOrWhiteSpace dir)
                && not (System.IO.Directory.Exists dir)
            then
                System.IO.Directory.CreateDirectory dir |> ignore

        try
            mkdir filePath
            System.IO.File.WriteAllText(filePath, content |> string, System.Text.Encoding.UTF8)
            sprintf "Successfully wrote to '%s'." filePath |> Ok
        with ex ->
            sprintf "Error writing to file '%s': %s" filePath ex.Message |> Error

    let runCommand commandLine cwd =
        let laucher cmd =
            let isWindows =
                System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform
                    System.Runtime.InteropServices.OSPlatform.Windows

            if isWindows then
                "cmd.exe", sprintf "/c \"%s\"" cmd
            else
                "bash", sprintf "-c \"%s\"" (cmd.Replace("\"", "\\\""))

        let workingDir dir =
            if System.String.IsNullOrWhiteSpace dir then
                System.Environment.CurrentDirectory
            else
                dir

        let processStartInfo cmd dir =
            let fileName, arguments = laucher cmd
            let startInfo = System.Diagnostics.ProcessStartInfo()
            startInfo.FileName <- fileName
            startInfo.Arguments <- arguments
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.UseShellExecute <- false
            startInfo.CreateNoWindow <- true
            startInfo.WorkingDirectory <- workingDir dir
            startInfo

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
            use p = new System.Diagnostics.Process()
            p.StartInfo <- processStartInfo commandLine cwd
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

        let getDir dir =
            if System.String.IsNullOrWhiteSpace dir then
                System.Environment.CurrentDirectory
            else
                dir

        try
            let path = getDir directoryPath

            if System.IO.Directory.Exists path then
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
            else
                sprintf "Error: Directory '%s' not found." path |> Error
        with ex ->
            sprintf "Error listing directory '%s': %s" directoryPath ex.Message |> Error
