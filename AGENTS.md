# Repository Guidelines

## Project Structure & Module Organization

- `src/GuildChest/`: .NET Framework 4.8 Valheim plugin, including RPCs, serialization, Harmony patches, and storage-mod adapters.
- `src/GuildChest.Core/`: game-independent .NET Standard 2.0 leases, revisions, and transfer lifecycle logic.
- `tests/GuildChest.Tests/`: .NET 10 xUnit tests; `tests/test_package.py`: Python packaging tests.
- `tests/GuildChest.NativeSmoke/`: optional BepInEx integration fixture, excluded from releases.
- `scripts/` and `build/`: setup, packaging, and shared assembly references. `docs/` covers architecture, compatibility, and gameplay validation. Root `icon.png` supplies the package icon.

## Build, Test, and Development Commands

Use the VS Code dev container with Docker; `global.json` selects the .NET 10 SDK.

- `bash scripts/dev.sh setup`: download game references and pinned dependencies; initial downloads are roughly 2.2 GB.
- `bash scripts/dev.sh build`: compile the solution in Debug configuration.
- `bash scripts/dev.sh test`: run xUnit and Python `unittest` checks.
- `bash scripts/dev.sh package`: build Release binaries and generate `artifacts/GuildChest-<version>.zip` after validating build receipts.

Gameplay runs in a separately installed Valheim client or server. Override game references with `bash scripts/dev.sh build -p:ValheimManagedDir=/path/to/Managed`.

## Coding Style & Naming Conventions

Follow `.editorconfig`: UTF-8, LF, final newline, and four-space indentation; JSON/YAML use two spaces. C# uses language version 10, nullable references, file-scoped namespaces, PascalCase types/methods, and camelCase locals/private fields. Match surrounding brace and layout conventions. Python uses snake_case. No dedicated formatter or linter is configured.

## Testing Guidelines

Name C# files `*Tests.cs` and use descriptive behavior names with `[Fact]` or `[Theory]`; Python files use `test_*.py` and methods use `test_*`. Add regression coverage for synchronization, transfer, and packaging changes. No numeric coverage threshold is configured. Run `bash scripts/dev.sh test` before submitting. Native/gameplay changes need relevant disposable-world checks from `docs/VALIDATION.md`; native fixtures are outside the normal test command.

## Commit & Pull Request Guidelines

History uses imperative, descriptive subjects such as “Refactor storage compatibility and release packaging,” without a fixed prefix scheme. Keep commits focused. PRs should explain behavior changes, link applicable issues, report automated and gameplay validation, and include screenshots for visible changes.

## Architecture & Configuration

Keep inventory authority on the host; apply client transfers and effects after acknowledgement. Preserve staged-inventory scope restoration. Set release versions in `manifest.json`. Keep downloaded DLLs, `.local/`, generated binaries, and credentials out of Git; retain NuGet lock files.
