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

**Cost**: Atomic write via temp+rename (`path.jsonl.tmp` → `path.jsonl`), so a crash during save leaves the original file intact. Corrupt data from a previous failed write is detected by `parseLoadingLines`, which reports the exact line number. Session loading validates each line and fails on the first corrupt entry.

---

## Tool Execution: Parallel but Shared State

**Choice**: Multiple tool calls in a single LLM response are executed in parallel via `Async.Parallel`, but they share the same file system and workspace.

**Why**: Parallel execution reduces latency when the LLM requests multiple independent operations (e.g., reading several files). The shared workspace is acceptable because tool calls in a single response are typically independent reads.

**Cost**: Write operations in parallel could conflict (e.g., two `write_file` calls to the same path). In practice, LLMs rarely issue parallel write operations, and the agent does not attempt to prevent it.

---

## Safety vs Flexibility: `move_file` with Overwrite Guard

**Choice**: `move_file` requires an explicit `overwrite=true` boolean to replace an existing destination. When overwriting, the original destination content is backed up to `.agents/trash/<timestamp>_<filename>` before the move.

**Why**: `run_command mv` lacks an overwrite guard and bypasses workspace boundary checks. The `overwrite=false` default prevents accidental data loss when the LLM is unaware of a conflicting file. The trash backup enables future `/undo` functionality and provides a safety net even when overwrite is requested.

**Cost**: The LLM must decide upfront whether to overwrite. If it assumes `overwrite=false` and the move fails, it must retry with `overwrite=true`. Backup to `.agents/trash/` is best-effort only — a crash between the trash write and the final move could lose both copies. The trash path is a fixed relative path (`.agents/trash/`), so concurrent sessions may collide on timestamp filenames (pathological but possible).

---

## Safety vs Idempotency: `create_directory` with `exist_ok` Guard

**Choice**: `create_directory` requires an explicit `exist_ok=true` boolean to silently succeed when the directory already exists. Without it (`exist_ok=false`, the default), the tool returns an error.

**Why**: `mkdir -p` semantics (always idempotent) can hide bugs where the LLM accidentally targets an existing directory that already contains unrelated files. The `exist_ok=false` default forces the LLM to acknowledge the directory's existence. The pattern mirrors `move_file`'s `overwrite` guard, keeping the tool design language consistent.

**Cost**: The LLM must decide upfront whether to allow an existing directory. If it forgets `exist_ok=true` on a retry, it gets a second error. The check is only on the leaf directory — intermediate parent directories are always created as needed (`.NET Directory.CreateDirectory` behavior). The `exist_ok` flag adds an extra parameter that LLMs must learn to use correctly.

---

## Safety vs Permanence: `delete_file` with Trash Backup

**Choice**: `delete_file` moves the file to `.agents/trash/<timestamp>_<filename>` instead of permanently deleting it. There is no `force` flag — trash backup is always performed.

**Why**: Accidental permanent deletion is unrecoverable. The trash backup uses the same mechanism as `move_file`'s overwrite guard, keeping the tool design consistent. The `.agents/trash/` directory can be manually cleaned by the user or purged by a future `/purge` command.

**Cost**: The file is physically moved (not copied), so deletion and backup are the same operation — no extra disk I/O. However, the trash directory grows unboundedly until manually cleaned. A crash during the trash move leaves the file in the original location (safe), but after a successful move the original path is empty and the file is only in trash. Currently no automatic purge policy exists; the user must run `rm -rf .agents/trash/` or a future purge tool to reclaim space.
