# F# Coding Agent

A lightweight, command-line AI coding agent implemented in F#. By leveraging LLMs (Large Language Models), it can autonomously read/write files and execute commands based on natural language instructions.

## Key Features

- **ReAct (Reasoning and Acting) Architecture**: Implements a loop where the LLM reasons about the task and interacts with the environment (file system, shell) using tools.
- **Lightweight & No External Dependencies**: Built entirely with F# standard features and `.NET 10` libraries like `System.Text.Json` and `HttpClient`. It does not require any large third-party SDKs.
- **OpenAI API Compatible**: Supports models like OpenAI's `gpt-4o` and utilizes Function Calling (Tools) to execute operations.
- **Workspace Sandbox Security**: 
  - Restricts all file and directory system operations (`readFile`, `writeFile`, `listDirectory`, `grepSearch`, `patchFile`, `readFileLines`, `findFiles`) to reside strictly within the workspace (the agent's startup current directory).
  - Restricts the execution working directory (`cwd`) of `runCommand` to the workspace, preventing unauthorized execution and access outside the allowed sandbox.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Architecture

- `Program.fs`: Entry point for the agent and the interactive REPL (Read-Eval-Print Loop).
- `Agent.fs`: Controls the ReAct loop and tool routing.
- `LlmClient.fs`: Handles HTTP requests to the LLM and JSON serialization/deserialization.
- `Tools.fs`: Provides physical operations and sandbox checks the agent can perform (file reading/writing, executing shell commands).

## Setup & Execution

1. Set your OpenAI API key as an environment variable:
    ```bash
    export OPENAI_API_KEY="your_api_key_here"
    ```

2. (Optional) Set the `OPENAI_MODEL` if you want to use a different model (defaults to `gpt-4o`):
    ```bash
    export OPENAI_MODEL="gpt-4o-mini"
    ```

3. Run the application:
    ```bash
    dotnet run --project coding-agent
    ```

## Usage

Once started, you will see a `>` prompt. Type your instructions for the agent here.

### Special Commands

- `/clear`: Clear the session conversational context and restart fresh.
- `/exit`: Exit the application.

### Example

```text
🚀 F# Coding Agent started! Type '/exit' or '/clear'.

> Check the list of files in the current directory
🤖 Thinking... Done.
🛠️  [Tool] Executing list_directory: 
🤖 Thinking... Done.

🤖 The following files are present in the current directory:
- Agent.fs
- LlmClient.fs
- Program.fs
- Tools.fs
- coding-agent.fsproj

> Create a file named HelloWorld.fs and write F# code that prints "Hello World"
🤖 Thinking... Done.
🛠️  [Tool] Executing write_file: HelloWorld.fs
🤖 Thinking... Done.

🤖 Created `HelloWorld.fs`.
```
