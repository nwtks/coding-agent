namespace CodingAgent

type SessionStore =
    { saveSession: string -> LlmClient.ChatMessage list -> Result<unit, string>
      loadSession: string -> Result<LlmClient.ChatMessage list, string>
      listSessions: unit -> string seq
      sessionPath: string -> string
      timestampedSessionName: unit -> string }

module Session =
    let jsonOptions =
        System.Text.Json.JsonSerializerOptions(PropertyNamingPolicy = null)

    let serializeMessage msg =
        System.Text.Json.JsonSerializer.Serialize(msg, jsonOptions)

    let deserializeMessage (json: string) =
        try
            System.Text.Json.JsonSerializer.Deserialize<LlmClient.ChatMessage>(json, jsonOptions)
            |> Ok
        with ex ->
            sprintf "Failed to deserialize message: %s" ex.Message |> Error

    let save fileSystem filePath messages =
        try
            fileSystem.createParentDirectory filePath

            let tempPath = filePath + ".tmp"

            messages
            |> List.map serializeMessage
            |> Array.ofList
            |> fileSystem.writeLines tempPath

            fileSystem.moveFile tempPath filePath
            Ok()
        with ex ->
            sprintf "Failed to save session: %s" ex.Message |> Error

    [<TailCall>]
    let rec parseLoadingLines index results =
        function
        | [] -> List.rev results |> Ok
        | line :: rest ->
            match deserializeMessage line with
            | Ok msg -> rest |> parseLoadingLines (index + 1) (msg :: results)
            | Error err -> sprintf "Corrupt session data at line %d: %s" (index + 1) err |> Error

    let load fileSystem filePath =
        try
            if not (fileSystem.existsFile filePath) then
                sprintf "Session file not found: %s" filePath |> Error
            else
                fileSystem.readLines filePath
                |> Seq.filter (System.String.IsNullOrWhiteSpace >> not)
                |> Seq.toList
                |> parseLoadingLines 1 []
        with ex ->
            sprintf "Failed to load session: %s" ex.Message |> Error

    let list fileSystem sessionsDir =
        fun () ->
            try
                if fileSystem.existsDir sessionsDir then
                    fileSystem.searchFiles sessionsDir "*.jsonl"
                    |> Seq.map (fun f ->
                        let name = fileSystem.fileNameWithoutExtension f
                        let info = fileSystem.fileInfo f
                        sprintf "  %s  (%s)" name (info.CreationTime.ToString "yyyy-MM-dd HH:mm"))
                    |> Seq.sort
                else
                    [||]
            with _ ->
                [||]

    let pathForName sessionsDir name =
        System.IO.Path.Combine(sessionsDir, sprintf "%s.jsonl" name)

    let timestampedName () =
        System.DateTime.Now.ToString "yyyyMMdd-HHmmss"
