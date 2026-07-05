namespace CodingAgent

type UndoEntry =
    { ts: string
      op: string
      path: string
      oldContent: string
      oldExists: bool
      trashPath: string
      sourcePath: string
      destPath: string
      destOverwritten: bool
      destOldTrashPath: string }

type Tools =
    { readFile: string -> Result<string, string>
      writeFile: string -> string -> Result<string, string>
      runCommand: string -> string -> Async<Result<string, string>>
      listDirectory: string -> Result<string, string>
      grepSearch: string -> bool -> bool -> string -> Result<string, string>
      patchFile: string -> string -> string -> bool -> Result<string, string>
      readFileLines: string -> int -> int -> Result<string, string>
      findFiles: string -> string -> Result<string, string>
      moveFile: string -> string -> bool -> Result<string, string>
      createDirectory: string -> bool -> Result<string, string>
      deleteFile: string -> Result<string, string>
      undo: unit -> Result<string, string> }

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

    let executeProcess
        (p: System.Diagnostics.Process)
        maxOutputBytes
        maxLineLength
        (token: System.Threading.CancellationToken)
        =
        async {
            let outputAsync =
                readStreamLines p.StandardOutput maxOutputBytes maxLineLength token

            let errorAsync = readStreamLines p.StandardError maxOutputBytes maxLineLength token
            let! outputHandle = outputAsync |> Async.StartChild
            let! errorHandle = errorAsync |> Async.StartChild
            do! p.WaitForExitAsync token |> Async.AwaitTask
            let! output = outputHandle
            let! error = errorHandle
            return struct (output, error, p.ExitCode)
        }

    let runCommand fileSystem maxOutputBytes maxLineLength timeoutMs sandboxMode workspaceRoot commandLine cwd =
        async {
            match validateCommandDir fileSystem commandLine cwd with
            | Ok resolvedWd ->
                let p = createProcess sandboxMode workspaceRoot commandLine resolvedWd
                use cts = new System.Threading.CancellationTokenSource(int timeoutMs)
                let token = cts.Token
                token.Register(fun () -> killProcess p) |> ignore

                try
                    let! struct (output, error, exitCode) = executeProcess p maxOutputBytes maxLineLength token
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

    let searchFilePlain fileSystem maxLineLength (query: string) ignoreCase relativePath file =
        let comparison =
            if ignoreCase then
                System.StringComparison.OrdinalIgnoreCase
            else
                System.StringComparison.Ordinal

        fileSystem.readLines file
        |> Seq.mapi (fun idx line -> idx + 1, line)
        |> Seq.filter (fun (_, line) -> line.Contains(query, comparison))
        |> Seq.map (fun (lineNum, line) -> $"{relativePath}:{lineNum}: {truncateLine (line.Trim()) maxLineLength}")

    let searchFileRegex fileSystem maxLineLength query ignoreCase relativePath file =
        let opts =
            if ignoreCase then
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            else
                System.Text.RegularExpressions.RegexOptions.None

        let timeout = System.TimeSpan.FromSeconds 5.0

        let isValidPattern =
            try
                System.Text.RegularExpressions.Regex.IsMatch("", query, opts) |> ignore
                true
            with :? System.ArgumentException ->
                false

        if not isValidPattern then
            seq { $"⚠️  Warning: Invalid regex pattern '{query}' in file '{relativePath}'." }
        else
            fileSystem.readLines file
            |> Seq.mapi (fun idx line -> idx + 1, line)
            |> Seq.filter (fun (_, line) ->
                try
                    System.Text.RegularExpressions.Regex.IsMatch(line, query, opts, timeout)
                with _ ->
                    false)
            |> Seq.map (fun (lineNum, line) -> $"{relativePath}:{lineNum}: {truncateLine (line.Trim()) maxLineLength}")

    let searchInFile fileSystem maxFileSizeBytes maxLineLength query isRegex ignoreCase path file =
        let relativePath =
            try
                fileSystem.relativePath path file
            with _ ->
                file

        try
            match checkFileSize fileSystem file maxFileSizeBytes with
            | Ok() ->
                if isRegex then
                    searchFileRegex fileSystem maxLineLength query ignoreCase relativePath file
                else
                    searchFilePlain fileSystem maxLineLength query ignoreCase relativePath file
            | Error err -> seq { $"⚠️  Warning: Skipped oversized file '{relativePath}': {err}" }
        with ex ->
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

    let grepSearch fileSystem maxDisplay maxFileSizeBytes maxLineLength query isRegex ignoreCase directoryPath =
        withExistingDir fileSystem directoryPath (fun path resolvedPath ->
            let rawResults =
                fileSystem.searchFiles resolvedPath "*"
                |> Seq.filter (isIgnored fileSystem >> not)
                |> Seq.collect (
                    searchInFile fileSystem maxFileSizeBytes maxLineLength query isRegex ignoreCase resolvedPath
                )

            let warnings = rawResults |> Seq.filter (fun s -> s.StartsWith "⚠️") |> Seq.toList

            rawResults
            |> Seq.filter (fun s -> not (s.StartsWith "⚠️"))
            |> Seq.truncate (maxDisplay + 1)
            |> Seq.toList
            |> formatGrepResults query path maxDisplay warnings)

    let manifestFilePath trashDir =
        System.IO.Path.Combine(trashDir, "_manifest.jsonl")

    let appendManifestEntry fileSystem trashDir entry =
        let manifestPath = manifestFilePath trashDir
        fileSystem.createParentDirectory manifestPath
        let json = System.Text.Json.JsonSerializer.Serialize entry

        let existingLines =
            if fileSystem.existsFile manifestPath then
                fileSystem.readLines manifestPath |> Array.ofSeq
            else
                [||]

        let allLines = Array.append existingLines [| json |]
        fileSystem.writeLines manifestPath allLines

    let readManifest fileSystem trashDir =
        let manifestPath = manifestFilePath trashDir

        try
            if fileSystem.existsFile manifestPath then
                let lines = fileSystem.readLines manifestPath

                let entries =
                    lines
                    |> Seq.choose (fun line ->
                        try
                            System.Text.Json.JsonSerializer.Deserialize<UndoEntry> line |> Some
                        with _ ->
                            None)
                    |> Seq.toList

                Ok entries
            else
                Ok []
        with ex ->
            $"Failed to read undo manifest: {ex.Message}" |> Error

    let writeManifest fileSystem trashDir entries =
        let manifestPath = manifestFilePath trashDir

        let jsonLines =
            entries
            |> List.map (fun e -> System.Text.Json.JsonSerializer.Serialize e)
            |> Array.ofList

        fileSystem.writeLines manifestPath jsonLines

    let trashFileNameFor fileSystem trashDir resolvedPath ts =
        let relative = fileSystem.relativePath fileSystem.workspaceRoot resolvedPath

        let safeName =
            relative.Replace(System.IO.Path.DirectorySeparatorChar, '_').Replace('/', '_')

        System.IO.Path.Combine(trashDir, $"{ts}_{safeName}")

    let snapshot fileSystem trashDir resolvedPath op =
        let ts = System.DateTime.UtcNow.ToString "yyyyMMdd-HHmmss"

        let oldContent =
            if fileSystem.existsFile resolvedPath then
                fileSystem.readFile resolvedPath
            else
                ""

        let oldExists = fileSystem.existsFile resolvedPath

        let entry: UndoEntry =
            { ts = ts
              op = op
              path = resolvedPath
              oldContent = oldContent
              oldExists = oldExists
              trashPath = ""
              sourcePath = ""
              destPath = ""
              destOverwritten = false
              destOldTrashPath = "" }

        appendManifestEntry fileSystem trashDir entry

    let revertWriteEntry (fileSystem: FileSystem) entry =
        if entry.oldExists then
            fileSystem.writeFile entry.path entry.oldContent
            Ok()
        else
            fileSystem.deleteFile entry.path
            Ok()

    let revertDeleteEntry fileSystem entry =
        if fileSystem.existsFile entry.path then
            $"Cannot undo: file already exists at '{entry.path}'." |> Error
        else
            fileSystem.createParentDirectory entry.path
            fileSystem.moveFile entry.trashPath entry.path
            Ok()

    let revertMoveEntry fileSystem entry =
        fileSystem.createParentDirectory entry.sourcePath

        if fileSystem.existsFile entry.sourcePath then
            $"Cannot undo: file already exists at '{entry.sourcePath}'." |> Error
        else
            fileSystem.moveFile entry.destPath entry.sourcePath

            if entry.destOverwritten then
                fileSystem.createParentDirectory entry.destPath
                fileSystem.moveFile entry.destOldTrashPath entry.destPath

            Ok()

    let undo fileSystem trashDir =
        try
            match readManifest fileSystem trashDir with
            | Ok [] -> Ok "Nothing to undo."
            | Ok entries ->
                let last = List.last entries
                let remaining = List.take (entries.Length - 1) entries

                let revertResult =
                    match last.op with
                    | "write"
                    | "patch" -> revertWriteEntry fileSystem last
                    | "delete" -> revertDeleteEntry fileSystem last
                    | "move" -> revertMoveEntry fileSystem last
                    | _ -> Ok()

                match revertResult with
                | Ok() ->
                    writeManifest fileSystem trashDir remaining
                    $"\U0001f649  Undone: {last.op} on {fileSystem.fileName last.path}" |> Ok
                | Error err -> Error err
            | Error err -> Error err
        with ex ->
            $"Failed to undo: {ex.Message}" |> Error

    let writeFile fileSystem trashDir maxFileSizeBytes filePath content =
        try
            match checkContentSize maxFileSizeBytes content with
            | Some err -> err
            | None ->
                match resolvePathInWorkspace fileSystem filePath with
                | Ok resolvedPath ->
                    fileSystem.createParentDirectory resolvedPath
                    snapshot fileSystem trashDir resolvedPath "write"
                    fileSystem.writeFile resolvedPath content
                    $"Successfully wrote to '{filePath}'." |> Ok
                | Error err -> Error err
        with ex ->
            $"Failed writing to file '{filePath}': {ex.Message}" |> Error

    [<TailCall>]
    let rec countOccurrences (text: string) pattern idx count =
        let nextIdx = text.IndexOf(pattern, idx, System.StringComparison.Ordinal)

        if nextIdx < 0 then
            count
        else
            countOccurrences text pattern (nextIdx + pattern.Length) (count + 1)

    let patchFilePlain fileSystem trashDir filePath target replacement =
        withExistingFile fileSystem filePath (fun filePath resolvedPath ->
            let content = fileSystem.readFile resolvedPath
            snapshot fileSystem trashDir resolvedPath "patch"
            let occurrences = countOccurrences content target 0 0

            if occurrences = 0 then
                $"Target content to patch not found in file '{filePath}'." |> Error
            elif occurrences > 1 then
                $"Target content found {occurrences} times in file '{filePath}'. Target must be unique. Provide more context (surrounding lines) to uniquely identify the section to patch."
                |> Error
            else
                content.Replace(target, replacement) |> fileSystem.writeFile resolvedPath
                $"Successfully patched file '{filePath}'." |> Ok)

    let patchFileRegex fileSystem trashDir maxFileSizeBytes filePath pattern replacement =
        withExistingFile fileSystem filePath (fun filePath resolvedPath ->
            match checkFileSize fileSystem resolvedPath maxFileSizeBytes with
            | Ok() ->
                let content = fileSystem.readFile resolvedPath
                snapshot fileSystem trashDir resolvedPath "patch"
                let timeout = System.TimeSpan.FromSeconds 5.0

                try
                    let regex =
                        new System.Text.RegularExpressions.Regex(
                            pattern,
                            System.Text.RegularExpressions.RegexOptions.None,
                            timeout
                        )

                    let matches = regex.Matches content
                    let matchCount = matches.Count

                    if matchCount = 0 then
                        $"Target regex pattern not found in file '{filePath}'." |> Error
                    else
                        let replaceWith: string = replacement
                        let newContent = regex.Replace(content, replaceWith, 1)
                        fileSystem.writeFile resolvedPath newContent
                        let msg = $"Successfully patched file '{filePath}' using regex."

                        if matchCount > 1 then
                            $"{msg} \u26a0\ufe0f Warning: Pattern matched {matchCount} times, only first occurrence was replaced."
                            |> Ok
                        else
                            msg |> Ok
                with :? System.ArgumentException ->
                    $"\u26a0\ufe0f Invalid regex pattern: '{pattern}'" |> Ok
            | Error err -> Error err)

    let patchFile fileSystem trashDir maxFileSizeBytes filePath target replacement isRegex =
        if isRegex then
            patchFileRegex fileSystem trashDir maxFileSizeBytes filePath target replacement
        else
            patchFilePlain fileSystem trashDir filePath target replacement

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

    let moveFile fileSystem trashDir source destination overwrite =
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
                        let mutable destOldTrash = ""

                        if fileSystem.existsFile destPath && overwrite then
                            let timestamp = System.DateTime.UtcNow.ToString "yyyyMMdd-HHmmss"
                            let trashPath = trashFileNameFor fileSystem trashDir destPath timestamp
                            fileSystem.createParentDirectory trashPath
                            fileSystem.moveFile destPath trashPath
                            destOldTrash <- trashPath

                        let ts = System.DateTime.UtcNow.ToString "yyyyMMdd-HHmmss"
                        let sourceExists = fileSystem.existsFile sourcePath

                        let entry: UndoEntry =
                            { ts = ts
                              op = "move"
                              path = sourcePath
                              oldContent = ""
                              oldExists = sourceExists
                              trashPath = ""
                              sourcePath = sourcePath
                              destPath = destPath
                              destOverwritten = destOldTrash <> ""
                              destOldTrashPath = destOldTrash }

                        appendManifestEntry fileSystem trashDir entry
                        fileSystem.moveFile sourcePath destPath
                        $"Successfully moved '{source}' to '{destination}'." |> Ok
        with ex ->
            $"Failed to move file '{source}' to '{destination}': {ex.Message}" |> Error

    let createDirectory fileSystem path existOk =
        try
            match resolvePathInWorkspace fileSystem path with
            | Error e -> Error e
            | Ok resolvedPath ->
                if fileSystem.existsDir resolvedPath && not existOk then
                    $"Directory '{path}' already exists. Set exist_ok=true to make idempotent."
                    |> Error
                else
                    fileSystem.createDirectory resolvedPath
                    $"Successfully created directory '{path}'." |> Ok
        with ex ->
            $"Failed to create directory '{path}': {ex.Message}" |> Error

    let deleteFile fileSystem trashDir filePath =
        try
            match withExistingFile fileSystem filePath (fun _ resolvedPath -> Ok resolvedPath) with
            | Error e -> Error e
            | Ok resolvedPath ->
                let ts = System.DateTime.UtcNow.ToString "yyyyMMdd-HHmmss"
                let destTrashPath = trashFileNameFor fileSystem trashDir resolvedPath ts
                fileSystem.createParentDirectory destTrashPath

                let entry: UndoEntry =
                    { ts = ts
                      op = "delete"
                      path = resolvedPath
                      oldContent = ""
                      oldExists = false
                      trashPath = destTrashPath
                      sourcePath = ""
                      destPath = ""
                      destOverwritten = false
                      destOldTrashPath = "" }

                appendManifestEntry fileSystem trashDir entry
                fileSystem.moveFile resolvedPath destTrashPath
                $"Successfully deleted file '{filePath}'." |> Ok
        with ex ->
            $"Failed to delete file '{filePath}': {ex.Message}" |> Error
