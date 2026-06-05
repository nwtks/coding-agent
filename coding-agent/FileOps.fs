namespace CodingAgent

type FileMetadata =
    { Length: int64
      CreationTime: System.DateTime }

type FileSystem =
    { readFile: string -> string
      writeFile: string -> string -> unit
      readLines: string -> string seq
      writeLines: string -> string array -> unit
      existsFile: string -> bool
      existsDir: string -> bool
      createParentDirectory: string -> unit
      files: string -> string array
      dirs: string -> string array
      searchFiles: string -> string -> string seq
      fileInfo: string -> FileMetadata
      fileName: string -> string
      fileNameWithoutExtension: string -> string
      relativePath: string -> string -> string
      workingDir: string -> string
      isPathInWorkspace: string -> bool
      resolvePath: string -> string
      workspaceRoot: string }

module FileOps =
    let workspaceRoot = System.IO.Path.GetFullPath System.Environment.CurrentDirectory

    let workingDir dirPath =
        if System.String.IsNullOrWhiteSpace dirPath then
            System.Environment.CurrentDirectory
        else
            dirPath

    let resolveOneSymlink path =
        if System.IO.File.Exists path then
            let fi = System.IO.FileInfo path
            let target = fi.ResolveLinkTarget false
            if isNull target then None else Some target.FullName
        elif System.IO.Directory.Exists path then
            let di = System.IO.DirectoryInfo path
            let target = di.ResolveLinkTarget false
            if isNull target then None else Some target.FullName
        else
            None

    [<TailCall>]
    let rec loopResolveSymlinks maxDepth depth visited path =
        if depth >= maxDepth then
            path
        else
            match resolveOneSymlink path with
            | Some target when not (Set.contains target visited) ->
                loopResolveSymlinks maxDepth (depth + 1) (Set.add target visited) target
            | _ -> path

    let resolveSymlinks path =
        let fullPath = System.IO.Path.GetFullPath path
        loopResolveSymlinks 64 0 (Set.empty.Add fullPath) fullPath

    let isPathInWorkspace path =
        if System.String.IsNullOrWhiteSpace path then
            false
        else
            try
                let root =
                    workspaceRoot.TrimEnd System.IO.Path.DirectorySeparatorChar
                    + string System.IO.Path.DirectorySeparatorChar

                let fullPath = resolveSymlinks path |> System.IO.Path.GetFullPath

                fullPath = workspaceRoot
                || fullPath.StartsWith(root, System.StringComparison.OrdinalIgnoreCase)
            with _ ->
                false

    let createParentDirectory (path: string) =
        let dir = System.IO.Path.GetDirectoryName path

        if
            not (System.String.IsNullOrWhiteSpace dir)
            && not (System.IO.Directory.Exists dir)
        then
            System.IO.Directory.CreateDirectory dir |> ignore

    let existsFile filePath = System.IO.File.Exists filePath
    let existsDir dirPath = System.IO.Directory.Exists dirPath

    let files dirPath = System.IO.Directory.GetFiles dirPath

    let dirs dirPath =
        System.IO.Directory.GetDirectories dirPath

    let searchFiles dirPath pattern =
        System.IO.Directory.EnumerateFiles(dirPath, pattern, System.IO.SearchOption.AllDirectories)

    let fileInfo filePath =
        let fi = System.IO.FileInfo filePath

        { Length = fi.Length
          CreationTime = fi.CreationTime }

    let fileName (path: string) = System.IO.Path.GetFileName path

    let fileNameWithoutExtension (path: string) =
        System.IO.Path.GetFileNameWithoutExtension path

    let relativePath relative path =
        System.IO.Path.GetRelativePath(relative, path)

    let readFile filePath =
        System.IO.File.ReadAllText(filePath, System.Text.Encoding.UTF8)

    let writeFile filePath (content: string) =
        System.IO.File.WriteAllText(filePath, content, System.Text.Encoding.UTF8)

    let readLines filePath =
        System.IO.File.ReadLines(filePath, System.Text.Encoding.UTF8)

    let writeLines filePath (lines: string array) =
        System.IO.File.WriteAllLines(filePath, lines, System.Text.Encoding.UTF8)

    let defaultFileSystem =
        { readFile = readFile
          writeFile = writeFile
          readLines = readLines
          writeLines = writeLines
          existsFile = existsFile
          existsDir = existsDir
          createParentDirectory = createParentDirectory
          files = files
          dirs = dirs
          searchFiles = searchFiles
          fileInfo = fileInfo
          fileName = fileName
          fileNameWithoutExtension = fileNameWithoutExtension
          relativePath = relativePath
          workingDir = workingDir
          isPathInWorkspace = isPathInWorkspace
          resolvePath = resolveSymlinks
          workspaceRoot = workspaceRoot }
