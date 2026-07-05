# Common Gotchas

## MockFileSystem is Pure In-Memory (No Disposal Needed)

The `MockFileSystem` in `TestHelpers.fs` is a pure in-memory mock — it has no external resources like temp files or handles. It does **not** implement `IDisposable`, so `use` has no effect.

```fsharp
// Both are fine — MockFileSystem has no external resources
let mock = MockFileSystem().FileSystem   // ✅ works
let fs = mock.FileSystem                  // ✅ works
```

`mockAgentConfig()` creates a fresh `MockFileSystem()` on each call, so tests are isolated by default. For tests that need custom file layouts, create a dedicated `MockFileSystem()` instance.

**Key Files**: `coding-agent.test/TestHelpers.fs`

---

## Handler Functions Return Async, Not Result Directly

**Problem**: Tool handlers in `AgentToolCall.fs` return `Async<Result<string, string>>`, not `Result<string, string>`. Forgetting to unwrap the Async causes type mismatches.

**Solution**: Use `Async.RunSynchronously` to execute handlers in tests (all tests in this project follow this pattern):

```fsharp
// ❌ WRONG - type mismatch
let result = AgentToolCall.handleReadFile config args
// result: Async<Result<string, string>>

// ✅ CORRECT - unwrap async with synchronous execution
let result = AgentToolCall.handleReadFile config args |> Async.RunSynchronously
// result: Result<string, string>
```

All tests in the project use `async { } |> Async.RunSynchronously` (not `task { }` / `Async.StartAsTask`).

**Key Files**: `coding-agent/AgentToolCall.fs`, `coding-agent.test/AgentToolCallTests.fs`

---

## JsonElement Arguments Must Come From JsonDocument

**Problem**: Handler functions expect `JsonElement` arguments, but creating these directly is error-prone. Using raw strings or incorrect types causes runtime failures.

**Solution**: Parse JSON strings using `JsonDocument.Parse` to get properly typed `JsonElement` values:

```fsharp
// ❌ WRONG - JsonElement doesn't have a public constructor
let args = JsonElement() // Compilation error

// ✅ CORRECT - parse from JSON string
use doc = System.Text.Json.JsonDocument.Parse """{"file_path": "test.txt"}"""
let args = doc.RootElement
let result = AgentToolCall.handleReadFile config args |> Async.RunSynchronously
```

**Key Files**: `coding-agent.test/AgentToolCallTests.fs`

---

## Tool Registration: Single Array, Not Separate Maps

**Current pattern**: Tools are registered via a single `ToolRegistration` array. The handler is embedded in the record, and `toolHandlers` dispatch map is built automatically:

```fsharp
// In AgentToolCall.fs

// 1. Define the handler function
let handleMyTool config (root: System.Text.Json.JsonElement) =
    async { return Ok "result" }

// 2. Create a ToolRegistration record with definition + handler + readOnly
let myToolReg =
    { toolName = MyTool
      definition = { ``type`` = "function"; ``function`` = { name = "my_tool"; ... } }
      handler = handleMyTool
      readOnly = true }

// 3. Add to the toolRegistrations array
let toolRegistrations: ToolRegistration array =
    [| readFileReg; writeFileReg; myToolReg; ... |]

// toolHandlers map is derived automatically:
// toolRegistrations |> Array.map (fun r -> ToolName.toString r.toolName, r.handler) |> Map.ofArray
```

**Gotcha**: The `toolHandlers` map is built from `toolRegistrations` at module initialization. Adding a `ToolRegistration` with a duplicate name silently overwrites the previous entry. Use unique names across all registrations.

**Key Files**: `coding-agent/AgentToolCall.fs`

---

## `move_file` Trash Backup is Best-Effort Only

`move_file` backs up the existing destination to `.agents/trash/<timestamp>_<filename>` **before** overwriting. The backup uses `fileSystem.moveFile` internally, so it is subject to the same failure modes as any file operation. A crash between the backup `moveFile` (dest → trash) and the actual `moveFile` (source → dest) leaves the file in the trash but **not** at the intended destination. This is an acceptable risk given that:

- The LLM explicitly opted into overwriting (`overwrite=true`).
- The original content is preserved in the trash and can be recovered manually.
- Full atomicity would require a `copy` + `delete` sequence or filesystem-level transactions (overkill for this use case).

To manually recover from a crash: check `.agents/trash/` for the timestamped backup.

**Key Files**: `coding-agent/Tools.fs` (`moveFile` function)

---

## `/undo` Only Reverts the Latest Operation

**Problem**: Each `/undo` call reverts only the most recent operation recorded in `.agents/trash/_manifest.jsonl`. Calling `/undo` twice in a row reverts two separate operations (popping entries one at a time). There is no `/undo N` or `/undo list` command.

**Implication**: If the LLM performs 3 write operations, then `/undo` reverts only the 3rd write. The user must call `/undo` again to revert the 2nd, and again for the 1st. The manifest is consumed as a LIFO stack.

**Key Files**: `coding-agent/Tools.fs` (`Tools.undo`)

---

## `/undo` write_file Snapshot is Full Content, Not a Diff

**Problem**: `write_file` saves the entire old file content inline in the manifest before overwriting. For large files, this causes the manifest to grow significantly.

**Implication**: After many write_file operations on large files, the manifest may accumulate substantial content. There is no automatic deduplication or purging. The manifest can be manually cleaned by deleting `.agents/trash/_manifest.jsonl` (this also clears undo history).

**Key Files**: `coding-agent/Tools.fs` (`snapshot` function)

---

## `/undo` Requires Trash Files to Be Intact

**Problem**: `delete_file` moves the file to `.agents/trash/<timestamp>_<relativePathWithUnderscores>` and records the trash path in the manifest. If the trash file is manually deleted or moved before `/undo` is called, the undo fails with a file-not-found error from `fileSystem.moveFile`.

**Implication**: Manual cleanup of `.agents/trash/` will break future undo operations for deleted files. Only clean the trash if you are certain no outstanding undo is needed.

**Key Files**: `coding-agent/Tools.fs` (`revertDeleteEntry`, `deleteFile`)

---

## `/undo` move_file Overwrite Restore Order

**Problem**: When undoing a `move_file` that overwrote an existing destination (`overwrite=true`), the revert logic first moves the file back to the source position, then restores the original destination content from trash (if any). This order must be maintained:

1. `fileSystem.moveFile destPath sourcePath` (restore source)
2. `fileSystem.moveFile destOldTrashPath destPath` (restore dest from trash, if overwritten)

**Why**: If dest is restored from trash before moving back to source, the source would receive the original dest content (from trash) instead of its own content. The MockFileSystem's `moveFile` removes the source key from the map after the second move, so the order determines which content survives.

**Key Files**: `coding-agent/Tools.fs` (`revertMoveEntry`)

---

## Parallel Test Execution Requires Unique Suffixes

**Problem**: xUnit runs tests in parallel by default. Tests that create files or directories with the same names can interfere with each other.

**Solution**: Use unique suffixes (typically the test function name) for any file system resources:

```fsharp
// ❌ WRONG - parallel tests may conflict
[<Fact>]
let ``test something`` () =
    let path = "test.txt"
    File.WriteAllText(path, "content")

// ✅ CORRECT - unique path per test
[<Fact>]
let ``test something`` () =
    let path = "test-something.txt"
    File.WriteAllText(path, "content")
```

For data-driven tests (`[<Theory>]` + `[<InlineData>]`), each inline scenario also needs a unique path to avoid parallel interference:

```fsharp
// Each InlineData scenario uses a distinct file path
[<Theory>]
[<InlineData("scenario-a", "content A")>]
[<InlineData("scenario-b", "content B")>]
let ``test with unique paths`` (suffix, content) =
    let path = $"test-{suffix}.txt"
    // use path...
```

**Best Practice**: Use descriptive names, `Guid.NewGuid()`, or a combination as suffixes for maximum uniqueness.

**Key Files**: All test files in `coding-agent.test/`

---

## Sandbox Fallback Mode Has No Real Isolation

**Problem**: When `bwrap` is unavailable, the agent falls back to running commands with `ulimit` only. This provides resource limits but NO filesystem or process isolation.

**Implication**: In fallback mode, commands can:
- Read/write any file the user has access to
- Execute any program on the system
- Access network resources

**Mitigation**: The `CommandSafety` module still validates commands against a deny-list, but this is not a security boundary against determined adversaries.

**Detection**: Check `config.sandboxMode` to determine the isolation level:

```fsharp
match config.sandboxMode with
| BwrapSandbox -> printfn "OS-level isolation enabled"
| FallbackOnly -> printfn "Warning: Running in fallback mode (no isolation)"
```

**Key Files**: `coding-agent/Sandbox.fs`, `coding-agent/Agent.fs`

---

## Session Persistence Uses JSONL, Not JSON

**Problem**: Sessions are stored as JSON Lines format (one JSON object per line), not as a single JSON array. Attempting to parse the entire file as JSON fails.

**Solution**: Parse each line independently:

```fsharp
// ❌ WRONG - JSONL is not a JSON array
let json = File.ReadAllText(sessionPath)
let messages = JsonSerializer.Deserialize<ChatMessage list>(json) // Fails

// ✅ CORRECT - parse line by line
let lines = File.ReadAllLines(sessionPath)
let messages =
    lines
    |> Array.map (fun line -> JsonSerializer.Deserialize<ChatMessage>(line))
    |> Array.toList
```

**Key Files**: `coding-agent/Session.fs`

---

## Message Truncation Preserves System Prompt

**Problem**: `truncateMessages` keeps only the last `maxHistory` messages, but the system prompt is always preserved as the first message regardless of history size.

**Implication**: After truncation, the message list structure is:
```
[system prompt] + [last maxHistory messages]
```

Not:
```
[last maxHistory messages including system prompt]
```

**Key Files**: `coding-agent/AgentLoop.fs` (see `truncateMessages` function)

---

## Auto-Confirm Mode Affects All Tools Globally

**Problem**: Setting `autoConfirm = All` bypasses confirmation for ALL tools, including destructive operations like `write_file` and `run_command`.

**Risk**: In automated/CI environments, this can lead to unintended file modifications or command execution without human oversight.

**Mitigation**: Use `ReadsOnly` mode for safer automation:

```fsharp
// Auto-confirm only read-only tools (safe)
{ config with autoConfirm = ReadsOnly }

// Auto-confirm all tools (risky - use only in trusted environments)
{ config with autoConfirm = All }

// Manual confirmation for all tools (safest)
{ config with autoConfirm = Off }
```

**Key Files**: `coding-agent/AgentToolCall.fs` (see `confirmToolCall` function)

---

## F# Strict Indentation Breaks Nested Records

**Problem**: Nested record update expressions must be properly indented. Putting `{ with ... }` on the same line as a field assignment causes compilation errors.

**Solution**: Place nested record updates on their own indented line:

```fsharp
// ❌ WRONG - compilation error
let config =
    { mockAgentConfig with
        tools = { mockAgentConfig.tools with readFile = newReader } }

// ✅ CORRECT - proper indentation
let config =
    { mockAgentConfig with
        tools =
            { mockAgentConfig.tools with
                readFile = newReader } }
```

**Key Files**: All test files using `mockAgentConfig`

---

## `grep_search` Regex Timeout Applies Per-Line, Not Per-File

**Problem**: The 5-second regex timeout (`Regex.InitTimeout`) applies individually to each line's `Regex.IsMatch` call. A single line that triggers catastrophic backtracking will timeout, but other lines in the same file are still processed normally. This means a large file with one pathological line may still produce partial results rather than failing outright.

**Implication**: A timeout on one line does NOT abort the entire search. The tool continues processing remaining lines. Test assertions should account for this behavior — expect partial output rather than an all-or-nothing error.

**Key Files**: `coding-agent/Tools.fs` (`searchInFile` function)

---

## `grep_search` `maxFileSizeBytes` Skips Files Silently

**Problem**: Files larger than `maxFileSizeBytes` are skipped with a warning message ("⚠️  Warning: Skipped oversized file"), not an error. The grep search continues to search remaining files. This means the LLM may receive partial results without being explicitly informed that some files were skipped — the warning is embedded in the results output.

**Implication**: When `maxFileSizeBytes` is small, large files in the search directory are silently omitted from results. The only indication is the warning line in the output. Tests should verify warning messages are present when oversized files are expected.

**Key Files**: `coding-agent/Tools.fs` (`searchInFile` function)

---

## `grep_search` Invalid Regex Returns Warning, Not Error

**Problem**: When `is_regex=true` and the pattern fails to compile (invalid regex syntax), the tool returns a warning message ("⚠️  Invalid regex pattern: ...") as a successful result, not an error. This is intentional — the LLM can see the warning and retry with a corrected pattern.

**Implication**: Tests for invalid regex should expect `Ok` (success) containing a warning message, not `Error`. The handler function never receives an error from the invalid pattern path, so handler-level error handling for regex syntax is unnecessary.

**Key Files**: `coding-agent/Tools.fs` (`grepSearch` function)

---

## `patch_file` Regex Mode Replaces Only the First Match

**Problem**: When `is_regex=true`, `patch_file` uses `Regex.Replace(content, replacement, 1)` which replaces only the first occurrence of the pattern. This differs from plain-text mode, which requires exactly one match (error if zero or multiple).

**Implication**: If the pattern matches multiple times, the tool succeeds but appends a warning (`⚠️ Warning: Pattern matched N times, only first occurrence was replaced.`). The LLM may not notice this warning in the success message and assume all occurrences were replaced. After a regex patch, the LLM should re-read the file or verify before applying additional patches.

**Key Files**: `coding-agent/Tools.fs` (`patchFileRegex` function)

---

## `patch_file` Regex Timeout Applies to Entire Match Operation

**Problem**: Unlike `grep_search` where the timeout applies per-line, `patch_file`'s 5-second timeout applies to `Regex.Matches(content)` and `Regex.Replace(content, replacement, 1)` — the entire content at once. A single pathological pattern on a large file can timeout the entire operation.

**Implication**: If a regex timeout occurs, it does NOT crash the tool — the `try/with` wrapper catches the `RegexMatchTimeoutException` and falls through to treating the regex as non-matching. The result may be a "not found" error or incomplete replacement. Tests should verify behavior with patterns that are known to be performant.

**Key Files**: `coding-agent/Tools.fs` (`patchFileRegex` function)

---

## `patch_file` Regex Mode Ignores `ignore_case` — Use `(?i)` Instead

**Problem**: Unlike `grep_search` which has a separate `ignore_case` boolean flag, `patch_file`'s regex mode does NOT have an `ignore_case` parameter. The regex is always compiled with `RegexOptions.None`.

**Implication**: To perform case-insensitive regex matching in `patch_file`, the LLM must use the inline `(?i)` syntax in the pattern (e.g., `(?i)hello` matches "hello", "HELLO", "Hello", etc.). This is a deliberate design choice to minimize the parameter surface, but may trip up LLMs familiar with `grep_search`'s `ignore_case` flag.

**Key Files**: `coding-agent/Tools.fs` (`patchFileRegex` function)
