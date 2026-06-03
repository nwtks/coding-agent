module CodingAgent.TestHelpers

open CodingAgent

let makeSuccessResponse body =
    let response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
    response.Content <- new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json")
    response

let makeErrorResponse statusCode reason body =
    let response = new System.Net.Http.HttpResponseMessage(statusCode)
    response.ReasonPhrase <- reason
    response.Content <- new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json")
    response

type MockFileSystem() =
    let mutable files = Map.empty<string, string>
    let mutable dirs = Set.empty<string>

    member _.AddFile path content = files <- files.Add(path, content)

    member _.AddDir path = dirs <- dirs.Add path

    member _.GetFile path = files.TryFind path

    member _.FileSystem =
        { readFile =
            fun path ->
                match files.TryFind path with
                | Some content -> content
                | None -> System.IO.FileNotFoundException path |> raise
          writeFile = fun path content -> files <- files.Add(path, content)
          readLines =
            fun path ->
                match files.TryFind path with
                | Some content -> content.Split '\n'
                | None -> System.IO.FileNotFoundException path |> raise
          writeLines =
            fun path lines ->
                if path.Contains '\x00' then
                    System.ArgumentException "Illegal characters in path" |> raise

                files <- files.Add(path, System.String.Join('\n', lines))
          existsFile = fun path -> files.ContainsKey path
          existsDir = fun path -> dirs.Contains path
          mkdir =
            fun path ->
                let dir = System.IO.Path.GetDirectoryName path

                if not (System.String.IsNullOrWhiteSpace dir) then
                    dirs <- dirs.Add dir
          files =
            fun dir ->
                files
                |> Map.filter (fun k _ -> System.IO.Path.GetDirectoryName k = dir)
                |> Map.toArray
                |> Array.map fst
          dirs =
            fun dir ->
                dirs
                |> Set.filter (fun d -> System.IO.Path.GetDirectoryName d = dir)
                |> Set.toArray
          searchFiles =
            fun dir pattern ->
                files
                |> Map.filter (fun k _ ->
                    k.StartsWith dir
                    && (pattern = "*"
                        || System.Text.RegularExpressions.Regex.IsMatch(
                            System.IO.Path.GetFileName k,
                            "^"
                            + System.Text.RegularExpressions.Regex
                                .Escape(pattern)
                                .Replace("\\*", ".*")
                                .Replace("\\?", ".")
                            + "$"
                        )))
                |> Map.toSeq
                |> Seq.map fst
          fileInfo =
            fun path ->
                let content =
                    match files.TryFind path with
                    | Some c -> c
                    | None -> ""

                let tempPath = System.IO.Path.GetTempFileName()
                System.IO.File.WriteAllText(tempPath, content)
                System.IO.FileInfo tempPath
          fileName = fun path -> System.IO.Path.GetFileName path
          fileNameWithoutExtension = fun path -> System.IO.Path.GetFileNameWithoutExtension path
          relativePath = fun relative path -> System.IO.Path.GetRelativePath(relative, path)
          workingDir =
            fun dir ->
                if System.String.IsNullOrWhiteSpace dir then
                    System.Environment.CurrentDirectory
                else
                    dir
          isPathInWorkspace =
            fun path -> not (path.StartsWith "/etc/" || path.StartsWith "/usr/" || path.StartsWith "/var/")
          workspaceRoot = System.Environment.CurrentDirectory }

let mockSessionStore () =
    let mutable store = Map.empty<string, LlmClient.ChatMessage list>

    { saveSession =
        fun filePath messages ->
            store <- Map.add filePath messages store
            Ok()
      loadSession =
        fun filePath ->
            match Map.tryFind filePath store with
            | Some msgs -> Ok msgs
            | None -> Error(sprintf "Session file not found: %s" filePath)
      listSessions =
        fun () ->
            store
            |> Map.keys
            |> Seq.map (fun k -> sprintf "  %s" (System.IO.Path.GetFileNameWithoutExtension k))
            |> Seq.sort
      sessionPath = fun name -> sprintf ".agents/sessions/%s.jsonl" name
      timestampedSessionName = fun () -> "20250101-000000" }
