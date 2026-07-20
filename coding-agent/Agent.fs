namespace CodingAgent

type AutoConfirmMode =
    | Off
    | All
    | ReadsOnly

type RuntimeConfig =
    { systemPrompt: string
      maxHistory: int
      autoConfirm: AutoConfirmMode
      maxToolCallIterations: int
      sandboxMode: Sandbox.SandboxMode }

type InteractiveUtils =
    { write: string -> unit
      writeLine: string -> unit
      readLine: unit -> string
      confirmToolCall: InteractiveUtils -> RuntimeConfig -> LlmClient.ToolCall -> bool }

type AgentConfig =
    { llmClientConfig: LlmClientConfig
      tools: Tools
      sessionStore: SessionStore
      fileSystem: FileSystem
      interactive: InteractiveUtils
      runtimeConfig: RuntimeConfig }
