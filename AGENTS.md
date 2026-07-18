# Repository Guidelines

## Project Structure & Module Organization

Ashes is a .NET 10 compiler/toolchain for `.ash`, targeting `linux-x64`, `linux-arm64`, `win-x64`, and `win-arm64` (win-arm64 is compile-and-link-only for now). Core projects live under `src/`: `Ashes.Frontend` handles lexer/parser/AST, `Ashes.Semantics` binding/type inference/IR, `Ashes.Backend` LLVM codegen, and `Ashes.Cli` user commands. Tooling includes `Ashes.Formatter`, `Ashes.Lsp`, `Ashes.Dap`, and `Ashes.TestRunner`. Unit tests live in `src/Ashes.*.Tests`; end-to-end `.ash` tests live in `tests/`. Use `lib/Ashes` for stdlib, `examples/` for examples, `docs/md` for authoritative docs, `runtimes/` for payloads, and `vscode-extension/` for extension code.

## Build, Test, and Development Commands

- `dotnet build Ashes.slnx`: build the solution.
- `dotnet run --project src/Ashes.Cli -- test tests`: run `.ash` tests.
- `dotnet run --project src/Ashes.Tests -- --no-progress`: run compiler unit tests.
- `dotnet format Ashes.slnx --verify-no-changes`: verify C# formatting; omit the flag to fix.
- `just ci-quick`: fast pre-commit build/test path.
- `just ci`: full PR-equivalent pipeline.
- `cd vscode-extension && pnpm run compile && pnpm run lint`: check extension changes.

## Coding Style & Naming Conventions

Use 4-space indentation for C# and `.ash`; project XML uses 2 spaces. Nullable analysis and .NET analyzers are enabled, warnings are errors, and `.editorconfig` is authoritative. Prefer explicit C# types over `var`, braces, ordinal string comparisons, and avoid allocation-heavy LINQ. Format changed `.ash` files with `dotnet run --project src/Ashes.Cli -- fmt path -w`.

## Testing Guidelines

Add unit tests near the affected project and `.ash` regression tests under `tests/`. Keep names scenario-focused, for example `pattern_missing_cases_diagnostic.ash`. Run one end-to-end test with `dotnet run --project src/Ashes.Cli -- test tests/foo.ash`. Test fixtures use leading `//` directives such as `expect:`, `expect-compile-error:`, and `fmt-skip:`; see `docs/md/guide/testing.md`. Examples must not use test directives. Do not add `[NotInParallel]`. Backend tests need LLVM assets from `bash scripts/download-llvm-native.sh`.

## Commit & Pull Request Guidelines

Recent history uses concise imperative or conventional-style subjects such as `docs: fix stale references` and `fix: ...`. Keep commits focused. Do not add `Co-Authored-By:` or `Claude-Session:` trailers; the hook rejects them. PRs should summarize changes, list validation, link issues, and include screenshots only for VS Code UI changes.

## Architecture Notes

Read relevant `docs/md` pages before changing behavior; if implementation conflicts with the spec, update the spec first. Respect compiler layering: Frontend -> Semantics -> Backend. LSP and DAP consume compiler logic; they must not duplicate parsing, type inference, lowering, or codegen. For language changes, move through docs/spec, Frontend, Semantics, Backend, Diagnostics, tests/examples, then formatting and verification. Avoid speculative behavior; leave a TODO when semantics are undefined.
