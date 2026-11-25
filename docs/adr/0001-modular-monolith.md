# ADR 0001: Modular monolith, CLI-first

**Status:** Accepted

## Context
The spec requires a CLI and a local dashboard sharing the same data, with no unnecessary infrastructure.

## Decision
- One `ModelBench.Core` class library holds all logic, organized by namespace folder.
- `ModelBench.Cli` and `ModelBench.Web` are thin hosts over Core.
- One SQLite file is shared by both hosts.
- There are no message queues, no separate services and no DI container outside Web.
- Extension points are limited to five contracts: `IBenchmarkFamily`, `ISimulatedEnvironment`, `IGrader`, `IModelProvider` and `ISandbox`. Each is registered in a static registry.

## Consequences
- Simple to build, test and run (`dotnet run`).
- A web-started run executes in the web process. Concurrent CLI and Web runs share SQLite. This is acceptable because writes are per-attempt, and the connection uses WAL with a busy timeout.
