# Design Trade-offs

## Security vs Usability: Sandbox Fallback

**Choice**: Run without OS-level isolation when `bwrap` is unavailable (FallbackOnly mode) instead of refusing to execute commands.

**Why**: The agent must work on Windows and Linux. `bwrap` is Linux-only, and requiring it would exclude most development environments. A warning is printed at startup when fallback mode is active.

**Risk**: In fallback mode, command safety relies entirely on regex-based deny-lists and `ulimit` — these are not a security boundary against a determined adversary.

---

## Security vs Functionality: Shared Network

**Choice**: `bwrap` sandbox uses `--share-net` (network access is NOT isolated).

**Why**: The agent needs network access to run package managers (`npm install`, `dotnet restore`), test servers, and HTTP-based tools. Full network isolation would severely limit usefulness.

**Risk**: Sandboxed commands can reach any network endpoint. The sandbox does not prevent data exfiltration if a malicious command is executed.

---

## Testability vs Simplicity: FileSystem Abstraction

**Choice**: All file I/O goes through the `FileSystem` record (a dependency-injection pattern) instead of calling `System.IO` directly.

**Why**: Enables `MockFileSystem` in tests — no real disk I/O needed. Every file-related module (`Tools`, `Session`, `AgentLoop`) receives its file system dependency through `AgentConfig`.

**Cost**: Extra indirection. `defaultFileSystem` wraps ~20 `System.IO` methods. Test mocks must implement the same surface.

---

## Mixed Sync/Async Tool Signatures

**Choice**: File tools (`readFile`, `writeFile`, `patchFile`, etc.) return `Result<string, string>` synchronously. Shell commands (`runCommand`) return `Async<Result<string, string>>`.

**Why**: File I/O is fast and blocking; wrapping it in `Async` adds ceremony without benefit. Shell commands can run for up to 120 seconds and need cooperative cancellation, so they are async.

**Cost**: Handler functions in `AgentToolCall` must bridge the two — sync tools are wrapped in `async { return ... }`, while `runCommand` uses `return!`.

---

## Safety vs Speed: Auto-Confirm Model

**Choice**: Three modes — `Off` (manual prompt for every tool), `All` (skip all prompts), `ReadsOnly` (auto-confirm read-only tools, prompt for write/execute).

**Why**: Interactive use needs manual confirmation to prevent accidental file writes or command execution. Automated/CI use needs `All` for hands-off operation. `ReadsOnly` is a middle ground for cautious exploration.

**Cost**: The `readOnly` flag on each `ToolRegistration` must be kept accurate — a misclassified tool silently bypasses user confirmation.

---

## Context Window Management: Message Truncation

**Choice**: Keep only the last `maxHistory` (default 20) messages plus the system prompt. Older messages are dropped after each query.

**Why**: LLM context windows are finite. Accumulating all tool results across many turns would eventually exceed limits and increase costs. Truncation keeps the agent responsive over long sessions.

**Cost**: The agent loses older context. In complex multi-turn tasks, it may forget earlier decisions. `dropTrailingTool` prevents dropping messages in the middle of a tool-call/response pair.

---

## Retry Strategy: Exponential Backoff with Jitter

**Choice**: Retry up to `maxRetries` (default 3) times on HTTP 429/502/503/504 and connection failures, with `500ms × 2^retry + random(0,500ms)` delay.

**Why**: Transient API errors are common with hosted LLM endpoints. Exponential backoff avoids thundering-herd problems. Jitter prevents synchronized retries across multiple clients.

**Cost**: A non-retryable error (e.g., 400 Bad Request) is returned immediately — only status codes known to be transient are retried. Connection failures (DNS, timeout) are always retried since they are typically transient.

---

## Error Handling: Result\<string, string\>

**Choice**: All errors are represented as `string` rather than a discriminated union or exception hierarchy.

**Why**: The errors are almost exclusively user-facing messages ("File not found", "Access denied", "Missing required property"). A typed error system would add complexity without improving behavior — the LLM only sees the string.

**Cost**: No programmatic error classification. Callers cannot pattern-match on error types. This is acceptable because the only consumer of errors is the LLM (which reads the string) and the UI (which prints it).

---

## Session Persistence: JSONL Format

**Choice**: Sessions are stored as JSON Lines (one JSON object per line) rather than a single JSON array.

**Why**: JSONL allows line-by-line parsing and partial recovery if the file is truncated mid-write. Each message is independently deserializable. No need to load the entire session into memory to append a message.

**Cost**: No atomic write — a crash during save could produce a truncated file. The `parseLoadingLines` function reports the exact line number of corrupt data. Session loading validates each line and fails on the first corrupt entry.

---

## Tool Execution: Parallel but Shared State

**Choice**: Multiple tool calls in a single LLM response are executed in parallel via `Async.Parallel`, but they share the same file system and workspace.

**Why**: Parallel execution reduces latency when the LLM requests multiple independent operations (e.g., reading several files). The shared workspace is acceptable because tool calls in a single response are typically independent reads.

**Cost**: Write operations in parallel could conflict (e.g., two `write_file` calls to the same path). In practice, LLMs rarely issue parallel write operations, and the agent does not attempt to prevent it.
