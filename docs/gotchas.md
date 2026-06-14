# Common Gotchas

## MockFileSystem is Pure In-Memory (No Disposal Needed)

The `MockFileSystem` in `TestHelpers.fs` is a pure in-memory mock — it has no external resources like temp files or handles. It does **not** implement `IDisposable`, so `use` has no effect.

```fsharp
// Both are fine — MockFileSystem has no external resources
let mock = MockFileSystem().FileSystem   // ✅ works
let fs = mock.FileSystem                  // ✅ works
```

**Shared singleton caution**: `mockAgentConfig.fileSystem` uses a module-level singleton `(MockFileSystem()).FileSystem`. Tests that modify state via `AddFile`/`AddDir` on this shared instance can affect other tests. Use a fresh `MockFileSystem()` per test if isolation is needed.

**Key Files**: `coding-agent.test/TestHelpers.fs`

---

## Handler Functions Return Async, Not Result Directly

**Problem**: Tool handlers in `AgentToolCall.fs` return `Async<Result<string, string>>`, not `Result<string, string>`. Forgetting to unwrap the Async causes type mismatches.

**Solution**: Use `Async.RunSynchronously` or `Async.StartAsTask` to execute handlers in tests:

```fsharp
// ❌ WRONG - type mismatch
let result = AgentToolCall.handleReadFile config args
// result: Async<Result<string, string>>

// ✅ CORRECT - unwrap async
let result = AgentToolCall.handleReadFile config args |> Async.RunSynchronously
// result: Result<string, string>

// ✅ ALSO CORRECT - convert to Task for async tests
task {
    let! result = AgentToolCall.handleReadFile config args |> Async.StartAsTask
    // result: Result<string, string>
}
```

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
let handleMyTool config (root: JsonElement) =
    async { return Ok "result" }

// 2. Create a ToolRegistration record with definition + handler + readOnly
let myToolReg =
    { definition = { ``type`` = "function"; ``function`` = { name = "my_tool"; ... } }
      handler = handleMyTool
      readOnly = true }

// 3. Add to the toolRegistrations array
let toolRegistrations: ToolRegistration array =
    [| readFileReg; writeFileReg; myToolReg; ... |]

// toolHandlers map is derived automatically:
// toolRegistrations |> Array.map (fun r -> r.definition.function.name, r.handler) |> Map.ofArray
```

**Gotcha**: The `toolHandlers` map is built from `toolRegistrations` at module initialization. Adding a `ToolRegistration` with a duplicate name silently overwrites the previous entry. Use unique names across all registrations.

**Key Files**: `coding-agent/AgentToolCall.fs`

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
    let path = sprintf "test-%s.txt" suffix
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
