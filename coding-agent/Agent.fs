namespace CodingAgent

type ToolImplementations =
    { readFile: string -> Result<string, string>
      writeFile: string -> string -> Result<string, string>
      runCommand: string -> string -> Result<string, string>
      listDirectory: string -> Result<string, string>
      grepSearch: string -> string -> Result<string, string>
      patchFile: string -> string -> string -> Result<string, string>
      readFileLines: string -> int -> int -> Result<string, string>
      findFiles: string -> string -> Result<string, string> }

type AutoConfirmMode =
    | Off
    | All
    | ReadsOnly

type AgentConfig =
    { llmClientConfig: LlmClientConfig
      tools: ToolImplementations
      sessionStore: SessionStore
      fileSystem: FileSystem
      write: string -> unit
      writeLine: string -> unit
      readLine: unit -> string
      confirmToolCall: AgentConfig -> LlmClient.ToolCall -> bool
      systemPrompt: string
      maxHistory: int
      autoConfirm: AutoConfirmMode }
