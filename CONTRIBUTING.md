# Contributing to IdempotencyGuard.NET

Thanks for your interest in contributing! This document covers everything you need to get started.

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download) or later (the project multi-targets `net8.0` and `net9.0`)
- A C# editor with EditorConfig support (e.g., Visual Studio, Rider, VS Code with C# Dev Kit)

## Getting Started

```bash
git clone <repo-url>
cd idempotency-guard
dotnet build
dotnet test
dotnet run --project samples/IdempotencyGuard.Sample.Api
```

All tests must pass on both `net8.0` and `net9.0` before submitting changes.

## Development Setup

The repository uses centralised configuration to keep things consistent:

- **Directory.Build.props** -- shared build properties across all projects (multi-target frameworks, nullable enabled, `TreatWarningsAsErrors`)
- **Directory.Packages.props** -- central package management so dependency versions are declared in one place
- **.editorconfig** -- enforced formatting rules that your IDE should pick up automatically

## Code Style

The `.editorconfig` at the repo root defines the full set of rules. The key conventions are:

- **File-scoped namespaces** (`namespace Foo;` not `namespace Foo { }`)
- **Nullable reference types enabled** -- annotate all public APIs
- **Private fields** use `_camelCase` (e.g., `private readonly ILogger _logger`)
- **Interfaces** use the `I` prefix (e.g., `IIdempotencyStore`)
- **Implicit usings** are enabled -- avoid redundant `using System;` etc.
- **Zero warnings policy** -- `TreatWarningsAsErrors` is on, so the build fails on any warning

## Testing

The project uses **xUnit**, **FluentAssertions**, and **NSubstitute**.

- Place tests in the corresponding `tests/` project
- Follow the existing naming convention: `{ClassName}Tests.cs`
- Use `[Fact]` for single-case tests and `[Theory]` with `[InlineData]` for parameterised tests
- Run the full suite before submitting:

```bash
dotnet test --verbosity normal
```

## Pull Requests

1. Fork the repository and create a feature branch from `master`
2. Make your changes in small, focused commits
3. Use **lowercase imperative** commit messages (e.g., `add retry logic to Redis store`)
4. Ensure `dotnet build` produces zero warnings and `dotnet test` passes on both target frameworks
5. Open a pull request with a clear description of what changed and why

## Reporting Issues

Bug reports and feature requests are welcome. When filing an issue, please include:

- A clear description of the problem or suggestion
- Steps to reproduce (for bugs)
- Expected vs actual behaviour
- .NET SDK version (`dotnet --version`)

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
