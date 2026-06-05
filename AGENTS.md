# AGENTS.md

## Setup Commands

```bash
dotnet build    # Build the project
dotnet test     # Run all unit tests with coverage report
```

## Project Structure

```
coding-agent/
├── coding-agent/               # Main source
│   ├── Program.fs              # Entry point: CLI arg parsing, config assembly, REPL startup
│   ├── Agent.fs                # Type definitions: AutoConfirmMode, AgentConfig
│   ├── AgentLoop.fs            # ReAct REPL loop, AGENTS.md loading, session init, command handlers
│   ├── AgentInstruction.fs     # LLM response handling, processInstruction, tool-result accumulation
│   ├── AgentToolCall.fs        # Tool definitions, confirmToolCall, executeToolCall, toolRegistrations
│   ├── CommandSafety.fs        # Regex-based command allow/deny list validation
│   ├── FileOps.fs              # FileSystem record type and defaultFileSystem implementation
│   ├── LlmClient.fs            # OpenAI-compatible HTTP client, JSON serialization
│   ├── Sandbox.fs              # bwrap OS isolation, output limits, and process execution
│   ├── Session.fs              # Session save/load with injectable SessionStore
│   └── Tools.fs                # Tool implementations (type Tools) with workspace sandbox enforcement
└── coding-agent.test/          # Unit tests (xUnit)
    ├── TestHelpers.fs           # Shared test helpers: MockFileSystem, mockSessionStore, mockAgentConfig
    ├── AgentInstructionTests.fs # Tests for AgentInstruction.fs
    ├── AgentLoopTests.fs        # Tests for AgentLoop.fs (REPL, commands, loadAgentsMd, etc.)
    ├── AgentToolCallTests.fs    # Tests for AgentToolCall.fs (tool dispatch, confirmToolCall)
    ├── CommandSafetyTests.fs    # Tests for CommandSafety.fs (Regex list validation)
    ├── FileOpsTests.fs          # Tests for FileOps.fs
    ├── LlmClientTests.fs        # Tests for LlmClient.fs
    ├── ProgramTests.fs          # Tests for Program.fs (CLI arg parsing, config assembly)
    ├── SandboxTests.fs          # Tests for Sandbox.fs
    ├── SessionTests.fs          # Tests for Session.fs
    └── ToolsTests.fs            # Tests for Tools.fs
```

## Code Style

- Use idiomatic F# and functional programming patterns.
- Prefer immutable data, `Result<'T, string>` for error handling, and pipeline operators (`|>`).
- Keep `AgentLoop.fs` / `AgentInstruction.fs` / `AgentToolCall.fs` decoupled from `Tools.fs` directly — wire them in `Program.fs`.
- Follow safe path checking practices for all filesystem and shell operations to avoid directory traversal. Use `FileOps.isPathInWorkspace` (exposed via `FileSystem.isPathInWorkspace`) before any file/directory access.
- `AgentConfig` (defined in `Agent.fs`) is the central configuration record — extend it when adding new injectable behaviors (e.g., confirmation strategies).

## Testing

- Ensure new features have accompanying unit tests in the `coding-agent.test` project.
- Maintain high unit test coverage (at least line ~80%).
- Use `mockAgentConfig` in `TestHelpers.fs` as the base for test configurations and override only what the test requires.
- Tests for tool behavior should go in `ToolsTests.fs`; tests for the ReAct loop, REPL, and command handlers go in `AgentLoopTests.fs`.
- Run `dotnet test` to verify all tests pass before committing.

## Adding New Tools

1. Implement the tool function in `Tools.fs` with sandbox checks.
2. Add the function signature to `Tools` in `Tools.fs`.
3. Create a handler function (e.g., `handleToolName`) in `AgentToolCall.fs`.
4. Add a `ToolRegistration` record (with the JSON definition, handler, and readOnly flag) to the `toolRegistrations` array in `AgentToolCall.fs`.
5. Wire the implementation in `Program.fs` → `agentConfig.tools`.
6. Add unit tests in both `ToolsTests.fs` and `AgentToolCallTests.fs`.

## Environment Variables

| Variable          | Default                                       | Description                    |
|-------------------|-----------------------------------------------|--------------------------------|
| `OPENAI_API_KEY`  | *(required)*                                  | API key for LLM access         |
| `OPENAI_MODEL`    | `gpt-4o`                                      | Model name to use              |
| `OPENAI_API_BASE` | `https://api.openai.com/v1/chat/completions`  | Endpoint URL (OpenAI-compatible)|
