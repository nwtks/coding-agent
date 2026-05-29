# AGENTS.md

## Setup Commands

```bash
dotnet build    # Build the project
dotnet test     # Run all unit tests with coverage report
```

## Project Structure

```
coding-agent/
├── coding-agent/           # Main source
│   ├── Program.fs          # Entry point: CLI arg parsing, config assembly, REPL startup
│   ├── Agent.fs            # ReAct loop, tool routing, REPL, confirmation logic
│   ├── LlmClient.fs        # OpenAI-compatible HTTP client, JSON serialization
│   └── Tools.fs            # Tool implementations with workspace sandbox enforcement
└── coding-agent.test/      # Unit tests (xUnit)
    ├── AgentTests.fs        # Tests for Agent.fs
    ├── LlmClientTests.fs    # Tests for LlmClient.fs
    └── ToolsTests.fs        # Tests for Tools.fs
```

## Code Style

- Use idiomatic F# and functional programming patterns.
- Prefer immutable data, `Result<'T, string>` for error handling, and pipeline operators (`|>`).
- Keep `Agent.fs` and `Tools.fs` decoupled: `Agent.fs` should not reference `Tools.fs` directly — wire them in `Program.fs`.
- Follow safe path checking practices for all filesystem and shell operations to avoid directory traversal. Use `Tools.isPathInWorkspace` before any file/directory access.
- `AgentConfig` is the central configuration record — extend it when adding new injectable behaviors (e.g., confirmation strategies).

## Testing

- Ensure new features have accompanying unit tests in the `coding-agent.test` project.
- Maintain high unit test coverage (at least line ~80%).
- Use `mockConfig` in `AgentTests.fs` as the base for test configurations and override only what the test requires.
- Tests for tool behavior should go in `ToolsTests.fs`; tests for the ReAct loop, REPL, and confirmation logic go in `AgentTests.fs`.
- Run `dotnet test` to verify all tests pass before committing.

## Adding New Tools

1. Implement the tool function in `Tools.fs` with sandbox checks.
2. Add the function signature to `ToolImplementations` in `Agent.fs`.
3. Add the JSON tool definition to `Agent.toolsDefinition`.
4. Add the dispatch case to `Agent.executeToolCall`.
5. Wire the implementation in `Program.fs` → `agentConfig.tools`.
6. Add unit tests in both `ToolsTests.fs` and `AgentTests.fs`.

## Environment Variables

| Variable          | Default                                       | Description                    |
|-------------------|-----------------------------------------------|--------------------------------|
| `OPENAI_API_KEY`  | *(required)*                                  | API key for LLM access         |
| `OPENAI_MODEL`    | `gpt-4o`                                      | Model name to use              |
| `OPENAI_API_BASE` | `https://api.openai.com/v1/chat/completions`  | Endpoint URL (OpenAI-compatible)|
