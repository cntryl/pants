# Repository Guidelines

## Project Structure & Module Organization

`Pants.slnx` contains the .NET 10 projects. All projects share the `Cntryl.Pants` root namespace. Core interfaces and public contracts live in `src/Pants`, grouped by domain (`Cloud`, `Storage`, `Transactions`, `Observability`, `Runtime`, `Scan`, `Time`, `Exceptions`); each domain's implementation is internal and lives under that domain's own `Internal/` subfolder (e.g. `Cloud/Internal`, `Storage/Internal`). Idiomatic service registration belongs in `src/Pants.DependencyInjection`. Tests are in `test/Pants.Tests`, mirroring the same domain folders plus `Contracts/` (parity/contract tests), `Compatibility/` (Midge fixtures), and `Support/` (failpoint handlers and test doubles). Benchmarks are in `bench/Pants.Benches`, and documentation in `docs`. Keep Markdown under `docs/*`, except this root guide. Do not add CLI or standalone tooling projects without explicit agreement.

## Build, Test, and Development Commands

- `dotnet restore --locked-mode` restores exactly the versions recorded in package lock files.
- `dotnet build` builds the complete solution; analyzers run and warnings fail the build.
- `dotnet test` runs the xUnit suite.
- `dotnet test --configuration Release` validates optimized builds.
- `dotnet format Pants.slnx` applies repository formatting rules.
- `dotnet format Pants.slnx --verify-no-changes --no-restore` is the formatting CI check.

Run formatting, a Release build, and relevant tests before submitting changes.

## Coding Style & Naming Conventions

Follow `.editorconfig` and modern C# conventions: four-space C# indentation, two-space project/JSON indentation, LF endings, file-scoped namespaces, braces, and nullable annotations. Prefer `var` for locals and omit redundant accessibility modifiers such as the default `private`; retain modifiers that change the contract. Use PascalCase for types and public members, camelCase for parameters and locals, and `_camelCase` for fields. Prefer immutable records for public data contracts and interfaces at public boundaries. Keep one top-level type per file and organize implementation details into focused internal directories. Async methods end in `Async` and accept an optional trailing `CancellationToken`.

## Testing Guidelines

Use xUnit `[Fact]` and `[Theory]` tests with behavior-oriented names such as `ShouldRecoverAtomicCommitFromMidgeWalAfterReopen`. Add focused regression coverage for every observable change. Persistence changes require reopen/recovery tests and must preserve the pinned Midge FORMAT, WAL, SST, manifest, and lease contracts. Tests must be deterministic and clean up temporary storage through `TemporaryDirectory`.

## Commit & Pull Request Guidelines

History currently uses concise, imperative subjects, for example `Initial Pants implementation`. Keep commits focused and avoid generated `bin`, `obj`, coverage, or benchmark artifacts. Pull requests should explain behavior and compatibility impact, list validation commands, and link relevant issues. Call out persisted-format, durability, recovery, public API, or dependency changes explicitly. Screenshots are unnecessary unless a future user-facing interface is affected.
