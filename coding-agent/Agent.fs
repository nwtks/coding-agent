namespace CodingAgent

type AutoConfirmMode =
    | Off
    | All
    | ReadsOnly

type AgentConfig =
    { llmClientConfig: LlmClientConfig
      tools: Tools
      sessionStore: SessionStore
      fileSystem: FileSystem
      write: string -> unit
      writeLine: string -> unit
      readLine: unit -> string
      confirmToolCall: AgentConfig -> LlmClient.ToolCall -> bool
      systemPrompt: string
      maxHistory: int
      autoConfirm: AutoConfirmMode
      commandTimeoutMs: int
      maxToolCallIterations: int
      maxFileSizeBytes: int64
      maxOutputBytes: int
      sandboxMode: Sandbox.SandboxMode }
