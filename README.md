# F# Coding Agent

A lightweight, command-line AI coding agent implemented in F#. It uses an LLM to autonomously read and write files, execute shell commands, and solve coding tasks expressed in natural language.

## Features

- **ReAct Architecture**: Implements a Reasoning + Acting loop — the LLM reasons about a task and calls tools iteratively until the task is complete.
- **Zero External Dependencies**: Built entirely on F# and .NET standard libraries (`System.Text.Json`, `HttpClient`). No third-party SDKs required.
- **OpenAI-Compatible**: Works with OpenAI's `gpt-4o` and any API-compatible endpoint (e.g. Azure OpenAI, local models via Ollama).
- **Workspace Sandbox**: Multi-layer defense using `bwrap` (Bubblewrap) for OS-level isolation, regex-based command deny-list, shell expansion detection, environment variable sanitization, output size limits, and `ulimit` for resource control.
- **Retry Logic**: Automatic exponential-backoff retries for transient API errors (HTTP 429, 502, 503, 504).
- **Interactive REPL**: Multi-turn conversation with `/clear` to reset context and `/exit` to quit.
- **AGENTS.md Support**: Automatically loads project-specific instructions from `AGENTS.md` at startup to give the agent context about the codebase.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Setup & Execution

1. **Set your API key:**
    ```bash
    export OPENAI_API_KEY="your_api_key_here"
    ```

2. **(Optional) Configure model and endpoint:**
    ```bash
    export OPENAI_MODEL="gpt-4o-mini"        # defaults to gpt-4o
    export OPENAI_API_BASE="https://..."     # defaults to OpenAI's endpoint
    ```

3. **Run:**
    ```bash
    dotnet run --project coding-agent
    ```

## Usage

After startup, a `>` prompt accepts natural language instructions. The agent will invoke tools as needed and report its reasoning.

### Special Commands

| Command              | Description                                          |
|----------------------|------------------------------------------------------|
| `/clear`             | Reset the conversation context (keeps session)       |
| `/exit`              | Exit the agent (auto-saves session)                  |
| `/autoconfirm on`    | Enable auto-confirm for ALL tools                    |
| `/autoconfirm off`   | Disable auto-confirm (manual prompts)                |
| `/autoconfirm reads` | Auto-confirm read-only tools only                    |
| `/save [name]`       | Save current session (name defaults to timestamp)    |
| `/load`              | List available saved sessions                        |
| `/load <name>`       | Load a previously saved session                      |

### Available Tools

| Tool              | Description                                                  |
|-------------------|--------------------------------------------------------------|
| `read_file`       | Read the full contents of a file                             |
| `read_file_lines` | Read a specific line range from a file (1-indexed)           |
| `write_file`      | Write content to a file (creates or overwrites)              |
| `patch_file`      | Replace an exact text block within a file (must be unique)   |
| `list_directory`  | List files and subdirectories in a directory                 |
| `find_files`      | Search for files by name pattern (e.g. `*.fs`)               |
| `grep_search`     | Search file contents recursively for a query string          |
| `run_command`     | Execute a shell command (sandboxed via bwrap on Linux)       |

All tools enforce workspace sandbox restrictions.

### CLI Flags

| Flag                       | Description                                                 |
|----------------------------|-------------------------------------------------------------|
| `--auto-confirm`           | Auto-confirm ALL tool calls without prompting               |
| `--auto-confirm-reads`     | Auto-confirm read-only tools only                           |
| `--load <name>`            | Load a previously saved session at startup                  |

### Session Persistence

Sessions are saved in JSON Lines (`.jsonl`) format under `.agents/sessions/`. Sessions are **auto-saved on `/exit`** with a timestamped filename.

### Example Session

```text
🚀 F# Coding Agent started! Type '/exit' or '/clear'.

> Add a docstring to every function in Tools.fs
🤖 Thinking... Done.
🛠️  [Tool] Executing read_file: Tools.fs
  ✅ [Success]
🤖 Thinking... Done.
🛠️  [Tool] Executing patch_file: Tools.fs
  ✅ [Success]
🤖 Thinking... Done.

🤖 Done! I've added XML doc comments to all 10 functions in Tools.fs.

> Run the tests and report any failures
🤖 Thinking... Done.
🛠️  [Tool] Executing run_command: dotnet test (cwd: ...)
  ✅ [Success]
🤖 Thinking... Done.

🤖 All 303 tests passed. No failures.
```

## Architecture

```
coding-agent/
├── Program.fs          Entry point: CLI arg parsing, config assembly, REPL startup
├── Agent.fs            Type definitions: AutoConfirmMode, AgentConfig
├── AgentLoop.fs        ReAct REPL loop, AGENTS.md loading, session init, command handlers
├── AgentInstruction.fs ReAct loop driver: processInstruction, instructionLoop, tool-result accumulation
├── AgentToolCall.fs    ToolRegistration type, confirmToolCall, executeToolCall, toolRegistrations array
├── CommandSafety.fs    Regex deny-list, shell expansion detection, environment sanitization
├── FileOps.fs          FileMetadata, FileSystem record type, and defaultFileSystem implementation
├── LlmClient.fs        LlmClientHandle, OpenAI HTTP client with exponential-backoff retry logic
├── Sandbox.fs          SandboxMode, bwrap OS isolation (sandboxedStartInfo), ulimit wrapping
├── Session.fs          SessionStore record type, session save/load/list in JSONL format
└── Tools.fs            Tools record type and module: file I/O, shell, search with sandbox enforcement
```

## Development

```bash
dotnet build   # Build
dotnet test    # Run tests with coverage report
```
