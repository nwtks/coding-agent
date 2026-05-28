# AGENTS.md

## Setup commands
- Build project: `dotnet build`
- Run tests: `dotnet test`

## Code style
- Use idiomatic F# and functional programming patterns.
- Keep agent core logic decoupled and clean in `Agent.fs` and `Tools.fs`.
- Ensure new features have accompanying unit tests in the `coding-agent.test` project.
- Check and maintain high unit test coverage when implementing new features or refactoring.
- Follow safe path checking practices for all filesystem and shell operations to avoid directory traversal.
