module CodingAgent.TestHelpers

open Xunit
open CodingAgent

let validChatResponseJson =
    """{"id":"chatcmpl-123","choices":[{"index":0,"message":{"role":"assistant","content":"Hello!"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}"""

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
                | Some content -> content.Split([| "\r\n"; "\n" |], System.StringSplitOptions.None) |> Array.toSeq
                | None -> System.IO.FileNotFoundException path |> raise
          writeLines = fun path lines -> files <- files.Add(path, System.String.Join("\n", lines))
          existsFile = fun path -> files.ContainsKey path
          existsDir = fun path -> dirs.Contains path
          createParentDirectory =
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

                let length = System.Text.Encoding.UTF8.GetByteCount content |> int64

                { Length = length
                  CreationTime = System.DateTime.UtcNow }
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
          resolvePath = fun path -> path
          workspaceRoot = System.Environment.CurrentDirectory
          moveFile =
            fun source dest ->
                match files.TryFind source with
                | Some content ->
                    files <- files.Add(dest, content)
                    files <- files.Remove source
                | None -> raise (System.IO.FileNotFoundException source)
          createDirectory =
            fun path ->
                dirs <- dirs.Add path
                let parent = System.IO.Path.GetDirectoryName path

                if not (System.String.IsNullOrWhiteSpace parent) then
                    dirs <- dirs.Add parent
          deleteFile = fun path -> files <- files.Remove path }

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
            | None -> Error($"Session file not found: {filePath}")
      listSessions =
        fun () ->
            store
            |> Map.keys
            |> Seq.map (fun k -> $"  {System.IO.Path.GetFileNameWithoutExtension k}")
            |> Seq.sort
      sessionPath = fun name -> $".agents/sessions/{name}.jsonl"
      timestampedSessionName = fun () -> "20250102-040506" }

let mockAgentConfig () =
    { llmClientConfig =
        { apiKey = ""
          model = ""
          endpoint = ""
          maxRetries = 0
          timeoutSeconds = 30 }
      tools =
        { readFile =
            fun path ->
                if path.Contains "nonexistent" || path.Contains "non_existent" then
                    Error $"File '{path}' not found."
                else
                    Ok $"Content of {path}"
          writeFile = fun path _ -> Ok $"Successfully wrote to '{path}'."
          runCommand = fun cmd cwd -> async { return Ok $"Output of {cmd} in {cwd}" }
          listDirectory = fun path -> Ok $"Contents of directory '{path}':"
          grepSearch =
            fun query isRegex ignoreCase path ->
                Ok $"Matches for '{query}' in '{path}' (regex: {isRegex}, ic: {ignoreCase})"
          patchFile = fun path _ _ -> Ok $"Patched '{path}'"
          readFileLines = fun path startLine endLine -> Ok $"Lines {startLine}-{endLine} of {path}"
          findFiles = fun pattern path -> Ok $"Matches for '{pattern}' in '{path}'"
          moveFile = fun source dest overwrite -> Ok $"Moved '{source}' to '{dest}' (overwrite: {overwrite})"
          createDirectory = fun path existOk -> Ok $"Created directory '{path}' (exist_ok: {existOk})"
          deleteFile = fun path -> Ok $"Deleted '{path}'" }
      sessionStore = mockSessionStore ()
      fileSystem = (MockFileSystem()).FileSystem
      interactive =
        { write = ignore
          writeLine = ignore
          readLine = fun () -> ""
          confirmToolCall = fun _ _ _ -> true }
      runtimeConfig =
        { systemPrompt = "You are helpful"
          maxHistory = 20
          autoConfirm = Off
          commandTimeoutMs = 30000
          maxToolCallIterations = 25
          maxFileSizeBytes = 0L
          maxOutputBytes = 1000000
          sandboxMode = Sandbox.FallbackOnly } }

let withEnvVar key value f =
    let old = System.Environment.GetEnvironmentVariable key
    System.Environment.SetEnvironmentVariable(key, value)

    try
        f ()
    finally
        System.Environment.SetEnvironmentVariable(key, old)

let withEnvVars pairs f =
    let olds =
        pairs
        |> List.map (fun (key, _) -> key, System.Environment.GetEnvironmentVariable key)

    pairs
    |> List.iter (fun (key, value) -> System.Environment.SetEnvironmentVariable(key, value))

    try
        f ()
    finally
        olds
        |> List.iter (fun (key, old) -> System.Environment.SetEnvironmentVariable(key, old))

let withTempDir prefix f =
    let guid = System.Guid.NewGuid().ToString "N"

    let tempDir =
        System.IO.Path.Combine(System.Environment.CurrentDirectory, $"{prefix}_{guid}")

    System.IO.Directory.CreateDirectory tempDir |> ignore

    try
        f tempDir
    finally
        if System.IO.Directory.Exists tempDir then
            System.IO.Directory.Delete(tempDir, true)

let assertOk (result: Result<'a, string>) : 'a =
    match result with
    | Ok value -> value
    | Error err ->
        Assert.Fail $"Expected Ok, but got Error: {err}"
        Unchecked.defaultof<'a>

let assertError (result: Result<'a, string>) : string =
    match result with
    | Error msg -> msg
    | Ok _ ->
        Assert.Fail "Expected Error, but got Ok"
        ""
