# Architecture

## Overview

The F# Coding Agent is a command-line AI coding assistant that uses a **ReAct (Reasoning + Acting) loop** to autonomously solve coding tasks. The LLM reasons about a task, calls tools, observes results, and iterates until the task is complete.

```
User Input
    │
    ▼
┌──────────────────────────────────────────────┐
│  AgentLoop.repl                              │
│  (Interactive REPL: commands & queries)      │
│                                              │
│  /exit /clear /autoconfirm /save /load       │
│  or natural language query                   │
└──────────────┬───────────────────────────────┘
               │ Query
               ▼
┌──────────────────────────────────────────────┐
│  AgentInstruction.processInstruction         │
│  (ReAct loop driver)                         │
│                                              │
│  Loop:                                       │
│    1. Send messages + tool defs to LLM       │
│    2. If response has tool_calls → execute   │
│    3. Append tool results → continue loop    │
│    4. If no tool_calls → return response     │
│    5. Max iterations → force stop            │
└──────────────┬───────────────────────────────┘
               │ Tool calls
               ▼
┌──────────────────────────────────────────────┐
│  AgentToolCall                               │
│  (Tool dispatch layer)                       │
│                                              │
│  • JSON argument parsing & validation        │
│  • Confirmation prompts (auto/manual)        │
│  • Routes to handler functions               │
│  • 11 registered tools (toolRegistrations)   │
└──────────────┬───────────────────────────────┘
               │ Delegates to
               ▼
┌──────────────────────────────────────────────┐
│  Tools (business logic)                      │
│  • File I/O with sandbox checks              │
│  • Shell command execution                   │
│  • Text search & file pattern search         │
│  • Undo log management (snapshot/revert)     │
└──────────────────────────────────────────────┘
```

## Module Responsibilities

| Module | Responsibility |
|--------|---------------|
| `Program` | Entry point: CLI arg parsing, config assembly, REPL startup |
| `Agent` | Core type definitions: `AutoConfirmMode`, `AgentConfig` |
| `AgentLoop` | REPL loop, command handlers (`/exit`, `/clear`, `/autoconfirm`, `/save`, `/load`, `/token`, `/undo`), `AGENTS.md` loading, session init, message truncation |
| `AgentInstruction` | ReAct loop driver: `processInstruction`, `instructionLoop`, tool-result accumulation, usage tracking |
| `AgentToolCall` | `ToolName` DU (11 variants with `toString`/`fromString`), tool registrations, JSON argument parsing, confirmation logic, `executeToolCall` dispatch, handler functions |
| `Tools` | Tool business logic: file I/O, shell execution, search, line truncation, undo log (manifest/snapshot/revert) — all with workspace sandbox enforcement |
| `FileOps` | `FileSystem` record (file/directory operations abstraction), `defaultFileSystem` implementation, symlink resolution |
| `CommandSafety` | Command validation: regex deny-list, shell expansion detection, environment sanitization, process start info assembly |
| `Sandbox` | OS-level isolation: `SandboxMode` DU, `bwrap` detection, `ulimit` wrapping, bwrap argument construction |
| `LlmClient` | OpenAI-compatible HTTP client: message types, serialization, exponential-backoff retry with jitter, API response parsing |
| `Session` | Session persistence: save/load/list in JSONL format under `.agents/sessions/` |

## Key Types

| Type | Description |
|------|-------------|
| `AgentConfig` | Central configuration: LLM client config, tools, session store, file system, interactive utils, runtime config (sub-records) |
| `RuntimeConfig` | Sub-record: systemPrompt, maxHistory, autoConfirm, commandTimeoutMs, maxToolCallIterations, maxFileSizeBytes, maxOutputBytes, sandboxMode |
| `InteractiveUtils` | Sub-record: write, writeLine, readLine, confirmToolCall |
| `Tools` | Record of 12 tool functions: readFile, writeFile, runCommand, listDirectory, grepSearch, patchFile, readFileLines, findFiles, moveFile, createDirectory, deleteFile, undo |
| `FileSystem` | Abstraction over System.IO: 21 fields covering file read/write, directory ops, path resolution, workspace boundary checks, moveFile, createDirectory, deleteFile |
| `FileMetadata` | { Length, CreationTime } |
| `UndoEntry` | { ts, op, path, oldContent, oldExists, trashPath, sourcePath, destPath, destOverwritten, destOldTrashPath } — manifest entry for /undo (serialized as plain record for System.Text.Json) |
| `SessionStore` | { saveSession, loadSession, listSessions, sessionPath, timestampedSessionName } |
| `ToolRegistration` | { toolName, definition, handler, readOnly } — binds a ToolName DU to its JSON schema and handler function |
| `ToolName` | DU: ReadFile \| WriteFile \| RunCommand \| ListDirectory \| GrepSearch \| PatchFile \| ReadFileLines \| FindFiles \| MoveFile \| CreateDirectory \| DeleteFile (with toString/fromString) |
| `AutoConfirmMode` | Off \| All \| ReadsOnly |
| `SandboxMode` | BwrapSandbox \| FallbackOnly |
| `LlmClientConfig` | { apiKey, model, endpoint, maxRetries, timeoutSeconds } |
| `LlmClient.ChatMessage` | { role, content, name, tool_call_id, tool_calls } |
| `LlmClientHandle` | IDisposable wrapper around HttpClient with PostAsync member |
| `LlmClient.ToolCall` | { id, type, function: FunctionCall } |
| `LlmClient.FunctionCall` | { name, arguments } |
| `LlmClient.FunctionDef` | { name, description, parameters } |
| `LlmClient.ToolDef` | { type, function: FunctionDef } |
| `ResponseAction` | Continue of ChatMessage list \| Stop of string × ChatMessage list |
| `LoopState` | { messages, promptTokens, completionTokens, iterationCount, result } |
| `LoopResult` | InProgress \| Completed(content, messages, pt, ct) \| Failed(err, pt, ct) |
| `ReplAction` | Continue \| Exit \| Clear \| ShowUsage \| Undo \| AutoConfirm of AgentConfig \| Load of ChatMessage list \| Query of string |

## Data Flow

### Single Query Lifecycle

1. `AgentLoop.repl` reads user input from the console
2. Slash commands are handled directly; natural language becomes a `Query`
3. `replAsync` appends a user message and calls `AgentInstruction.processInstruction`
4. `processInstruction` receives both the original messages (as fallback on failure) and the user-appended messages, initializes `LoopState` with `InProgress`, and enters `instructionLoop`
5. Each iteration:
   - Sends current messages + tool definitions to the LLM via `LlmClient.sendChatRequest`
   - On success, calls `processResponse` to handle the response
   - If the response contains `tool_calls`: executes each tool via `AgentToolCall.executeToolCall`, appends tool result messages, returns `Continue`
   - If no tool calls: returns `Stop` with the final content
   - If max iterations exceeded: returns `Failed`
6. Token usage is accumulated across iterations
7. Back in `repl`, messages are truncated to `maxHistory` and the loop continues

### Tool Execution Flow

1. `executeToolCall` looks up the tool by name in the `toolHandlers` map
2. `confirmToolCall` checks auto-confirm mode — prompts user if manual confirmation is needed
3. The handler function (e.g., `handleReadFile`) parses JSON arguments using `getRequiredStringProperty`
4. The handler delegates to `config.tools.*` (e.g., `config.tools.readFile`)
5. `Tools.readFile` resolves the path, checks workspace boundaries, checks file size, then calls `fileSystem.readFile`
6. Result bubbles up as `Result<string, string>` → formatted into a `ChatMessage` with role `"tool"`

## Security Model

The agent uses a **multi-layer defense** strategy for command execution:

```
Layer 1: CommandSafety.validateCommand
  ├── Regex deny-list (mkfs, rm -rf /, fork bomb, etc.)
  ├── Shell expansion detection ($(), backticks, hex/octal escapes)
  └── Simple dangerous command patterns

Layer 2: CommandSafety.sanitizeEnvironment
  ├── Allow-list only safe environment variables
  └── Set TERM=dumb, CODING_AGENT_SANDBOX=1

Layer 3: Sandbox (SandboxMode)
  ├── BwrapSandbox: OS-level isolation via Bubblewrap
  │   ├── Read-only bind mounts for system dirs
  │   ├── Writable bind for workspace only
  │   ├── Namespace isolation (user, pid, ipc, uts, cgroup)
  │   └── Shared network (--share-net)
  └── FallbackOnly: bash -c with ulimit only

Layer 4: ulimit resource limits
  ├── Virtual memory: 2GB (-v 2097152)
  ├── File size: 1GB (-f 1048576)
  └── CPU time: 120s (-t 120)

Layer 5: Runtime limits
  ├── Command timeout (configurable, default 120s)
  ├── Output size limit (default 1MB)
  ├── Line truncation (100K chars per line via `truncateLine` + `maxLineLength`)
  └── Max tool call iterations (default 25)
```

All file tools enforce **workspace sandbox**: paths are resolved (including symlinks up to 64 levels) and verified to be within the workspace root before any I/O operation.

## Configuration

`AgentConfig` is assembled in `Program.newAgentConfig` from:

- **Environment variables**: `OPENAI_API_KEY`, `OPENAI_MODEL` (default: `gpt-4o`), `OPENAI_API_BASE`
- **CLI flags**: `--auto-confirm`, `--auto-confirm-reads`, `--load <name>`
- **Hardcoded defaults**:
  - `maxRetries`: 3
  - `timeoutSeconds`: 120
  - `commandTimeoutMs`: 120000
  - `maxFileSizeBytes`: 100 MB
  - `maxOutputBytes`: 1 MB
  - `maxLineLength`: 100000 chars (per-line truncation for command output)
  - `maxDisplay`: 100 results (for `grep_search`/`find_files` result truncation)
  - `maxHistory`: 20 messages
  - `maxToolCallIterations`: 25

The `AGENTS.md` file (if present in the working directory) is loaded at startup and appended to the system prompt.

## Build & Quality

- **Build system**: `Directory.Build.props` + `Directory.Build.targets`
- **Test framework**: xUnit v3 with `[<Fact>]` and `[<Theory>]`/`[<InlineData>]`
- **Coverage**: Coverlet (cobertura format), target ≥ 90% line coverage
- **Complexity**: `scripts/check-complexity.fsx` enforces cyclomatic complexity ≤ 15 (warning ≥ 10)
- **Target**: .NET 10.0 (`net10.0`)
