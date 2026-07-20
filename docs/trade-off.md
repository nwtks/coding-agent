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

**Choice**: `move_file` requires an explicit `overwrite=true` boolean to replace an existing destination. When overwriting, the original destination content is backed up to `.agents/trash/<timestamp>_<relativePathWithUnderscores>` before the move, and an undo log entry is appended to `.agents/trash/_manifest.jsonl`.

**Why**: `run_command mv` lacks an overwrite guard and bypasses workspace boundary checks. The `overwrite=false` default prevents accidental data loss when the LLM is unaware of a conflicting file. The trash backup enables `/undo` and provides a safety net even when overwrite is requested.

**Cost**: The LLM must decide upfront whether to overwrite. If it assumes `overwrite=false` and the move fails, it must retry with `overwrite=true`. Backup to `.agents/trash/` is best-effort only — a crash between the trash write and the final move leaves the file in trash but not at the intended destination (recoverable manually).

---

## Safety vs Idempotency: `create_directory` with `exist_ok` Guard

**Choice**: `create_directory` requires an explicit `exist_ok=true` boolean to silently succeed when the directory already exists. Without it (`exist_ok=false`, the default), the tool returns an error.

**Why**: `mkdir -p` semantics (always idempotent) can hide bugs where the LLM accidentally targets an existing directory that already contains unrelated files. The `exist_ok=false` default forces the LLM to acknowledge the directory's existence. The pattern mirrors `move_file`'s `overwrite` guard, keeping the tool design language consistent.

**Cost**: The LLM must decide upfront whether to allow an existing directory. If it forgets `exist_ok=true` on a retry, it gets a second error. The check is only on the leaf directory — intermediate parent directories are always created as needed (`.NET Directory.CreateDirectory` behavior). The `exist_ok` flag adds an extra parameter that LLMs must learn to use correctly.

---

## Safety vs Reversibility: `/undo` with Manifest-Based Undo Log

**Choice**: Four write operations (`write_file`, `patch_file`, `move_file`, `delete_file`) record an `UndoEntry` in `.agents/trash/_manifest.jsonl` before execution. `/undo` pops the latest entry and applies the reverse transformation.

**Why**: LLMs can make destructive changes. An undo log provides a safety net without requiring user confirmation on every write (which would defeat autonomy).

**Behavior per operation**:
- `write_file` / `patch_file` — snapshot the full pre-write content inline in the manifest. Undo restores it (or deletes the file if it was newly created).
- `delete_file` — moves the file to `.agents/trash/` and records the trash path. Undo moves it back.
- `move_file` — records source/dest and any overwritten-dest backup. Undo moves the file back to source, then restores the overwritten destination from trash.

**Cost**:
- **Only the latest operation is undoable per `/undo` call**. Multiple calls pop entries LIFO, one at a time.
- **Snapshots are full content, not diffs** — large files cause the manifest to grow. No compression or TTL-based purging yet.
- **Trash must remain intact** — manual cleanup of `.agents/trash/` removes undo history for delete/move operations.
- **No locking on manifest writes** — `appendManifestEntry` uses a read-modify-write over `writeLines`. Concurrent writes could race; in practice the LLM rarely issues parallel writes in one turn.
- **No `/undo N` or `/undo list`** — only the latest entry is reverted per call.

---

## Safety vs Flexibility: `grep_search` with Regex and Case-Insensitive Flags

**Choice**: `grep_search` accepts `is_regex` (default `false`) and `ignore_case` (default `true`) as optional boolean flags, plus a `maxFileSizeBytes` safety limit. When `is_regex=true`, patterns are compiled with a 5-second timeout and invalid regex syntax returns a warning message instead of an error.

**Why**: Plain-text search (`grep -F` equivalent) is the safe default — no regex injection, no ReDoS risk. Regex support is explicitly opt-in via `is_regex=true`, so the LLM must consciously enable it. The 5-second regex timeout prevents catastrophic backtracking on pathological inputs. Invalid regex patterns return a user-facing warning rather than crashing the tool, allowing the LLM to recover gracefully.

**Cost**: Two extra parameters (`is_regex`, `ignore_case`) add complexity to the tool definition. The LLM must consciously set `is_regex=true` for regex patterns. Regex timeout is per-line — a single slow line could timeout while the rest of the file would have completed. `ignore_case` without regex uses `IndexOf(OrdinalIgnoreCase)` (correct for most locales).

---

## Safety vs Flexibility: `patch_file` with Regex Mode

**Choice**: `patch_file` accepts an optional `is_regex` boolean flag (default `false`). When `is_regex=true`, the `target` is treated as a regex pattern with a 5-second timeout and `maxFileSizeBytes` safety guard. Only the first match is replaced (`Regex.Replace` with `count=1`), and multiple matches produce a warning. Invalid regex syntax returns a warning message (not an error), matching `grep_search` behavior.

**Why**: Plain-text matching (`String.Replace` with `Ordinal` comparison) is the safe default — no regex injection, no ReDoS risk. Regex support is explicitly opt-in. The 5-second timeout prevents catastrophic backtracking. Replacing only the first match minimizes surprise (the LLM sees the result and can decide to patch remaining occurrences). The warning-for-invalid-regex pattern keeps the tool resilient — the LLM can retry with a corrected pattern.

**Cost**: The `is_regex` flag adds a parameter the LLM must learn. Regex mode returns `Error` for zero matches (consistent with plain mode), while invalid regex syntax returns `Ok` with a warning (consistent with `grep_search` — this inconsistency is a subtlety to handle). The `ignore_case` flag was deliberately omitted (use inline `(?i)` in the pattern instead), which may surprise users accustomed to `grep_search`'s `ignore_case`.

---

## Safety vs Permanence: `delete_file` with Trash Backup

**Choice**: `delete_file` moves the file to `.agents/trash/<timestamp>_<relativePathWithUnderscores>` instead of permanently deleting it, and records an undo entry in `.agents/trash/_manifest.jsonl`. There is no `force` flag — trash backup is always performed.

**Why**: Accidental permanent deletion is unrecoverable. The trash backup uses the same mechanism as `move_file`'s overwrite guard, keeping the tool design consistent and enabling `/undo`.

**Cost**: The file is physically moved (not copied), so deletion and backup are the same operation — no extra disk I/O. The trash directory grows unboundedly until manually cleaned; no automatic purge exists yet. The user must run `rm -rf .agents/trash/` to reclaim space (this also removes undo history).
