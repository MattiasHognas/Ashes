# Development

This guide covers everything needed to build, test, and develop Ashes
locally.

------------------------------------------------------------------------

## Prerequisites

| Tool | Version | Notes |
|------|---------|-------|
| .NET SDK | 10.0+ | Specified in `global.json` |
| Node.js | 20.19+, 22.13+, or 24+ | VS Code extension only |
| pnpm | (managed via corepack) | VS Code extension only |
| bash | any | Scripts and LLVM provisioning |

------------------------------------------------------------------------

## Repository Layout

```
Ashes.slnx                 Solution file
src/
  Ashes.Frontend/           Lexer, parser, AST
  Ashes.Semantics/          Binding, type inference, IR, optimizer
  Ashes.Backend/            LLVM codegen and native linker
  Ashes.Formatter/          Canonical source formatting
  Ashes.Cli/                CLI orchestration (compile, run, repl, test, fmt)
  Ashes.Lsp/                Language server
  Ashes.Dap/                Debug adapter
  Ashes.TestRunner/         End-to-end .ash test execution
  Ashes.Tests/              Compiler unit tests
  Ashes.Lsp.Tests/          LSP unit tests
docs/                       Specifications and references
examples/                   Example .ash programs
tests/                      End-to-end .ash test suite
scripts/                    Development and CI helper scripts
vscode-extension/           VS Code extension source
```

------------------------------------------------------------------------

## Building

Build the entire solution:

```sh
dotnet build Ashes.slnx
```

Release build:

```sh
dotnet build Ashes.slnx --configuration Release
```

------------------------------------------------------------------------

## LLVM Native Libraries

The backend requires native LLVM libraries. Download them before running
any backend or end-to-end tests:

```sh
# All platforms (linux-x64, linux-arm64, win-x64):
bash scripts/download-llvm-native.sh --all

# Current architecture only:
bash scripts/download-llvm-native.sh

# Specific architecture:
bash scripts/download-llvm-native.sh 22 arm64
```

On Windows, run the bash script from WSL:

```sh
# Download all supported runtimes, including win-x64:
bash scripts/download-llvm-native.sh --all
```

The libraries are placed under `runtimes/{linux-x64,linux-arm64,win-x64}/`
and are automatically copied to build output by `Ashes.Backend.csproj`.

------------------------------------------------------------------------

## Running the Compiler

```sh
# Compile and run a program:
dotnet run --project src/Ashes.Cli -- run hello.ash

# Run an inline expression:
dotnet run --project src/Ashes.Cli -- run --expr "Ashes.IO.print(40 + 2)"

# Compile to a native executable:
dotnet run --project src/Ashes.Cli -- compile hello.ash -o hello

# Cross-compile:
dotnet run --project src/Ashes.Cli -- compile --target linux-arm64 hello.ash -o hello

# REPL:
dotnet run --project src/Ashes.Cli -- repl
```

------------------------------------------------------------------------

## Testing

### Compiler Unit Tests

```sh
dotnet run --project src/Ashes.Tests -- --no-progress
```

Filter by test class:

```sh
dotnet run --project src/Ashes.Tests -- --no-progress --treenode-filter "/*/*/ClassName/**"
```

### LSP Unit Tests

```sh
dotnet run --project src/Ashes.Lsp.Tests -- --no-progress
```

### End-to-End Tests

```sh
dotnet run --project src/Ashes.Cli -- test tests
```

### Full Verification

The `scripts/verify.sh` script runs the complete validation pipeline:
build, format check, unit tests, publish, format examples/tests, run
examples, and run end-to-end tests:

```sh
bash scripts/verify.sh
```

------------------------------------------------------------------------

## Formatting

### `.ash` Files

All `.ash` source files must be canonically formatted. After creating or
modifying any `.ash` file:

```sh
# Format a file or directory (prints formatted output):
dotnet run --project src/Ashes.Cli -- fmt path/to/file.ash

# Format in place:
dotnet run --project src/Ashes.Cli -- fmt examples -w
```

### C# Code

```sh
dotnet format Ashes.slnx
```

------------------------------------------------------------------------

## VS Code Extension

### Local Development

Build and install the extension locally:

```sh
bash scripts/install-vscode-extension-local.sh
```

By default this publishes the compiler, language server, and debug
adapter for the current RID only, then packages and installs the VSIX.
Use `--skip-install` to package without installing:

```sh
bash scripts/install-vscode-extension-local.sh --skip-install
```

Useful options:

```sh
# Publish all supported bundled RIDs (win-x64, linux-x64, linux-arm64):
bash scripts/install-vscode-extension-local.sh --all-rids

# Force a clean pnpm dependency reinstall before building:
bash scripts/install-vscode-extension-local.sh --force-install-dependencies

# Override the VS Code CLI to use for install:
bash scripts/install-vscode-extension-local.sh --code-command code-insiders

# When running from bash on Windows/WSL and targeting the Windows VS Code build:
bash scripts/install-vscode-extension-local.sh --target-rid win-x64
```

On Windows, run the same script from WSL. Use `--target-rid win-x64` when
you need to bundle binaries for the Windows VS Code build.

### Extension Only (No Publish)

To work on the extension TypeScript code without re-publishing .NET
projects:

```sh
cd vscode-extension
pnpm install --frozen-lockfile --force
pnpm run compile
pnpm run lint
pnpm run format:check
```

------------------------------------------------------------------------

## Publishing

Build self-contained executables for distribution:

```sh
bash scripts/publish.sh
```

On Windows, run the same command from WSL.

------------------------------------------------------------------------

## Development Rules

1. **Spec first.** Update `docs/LANGUAGE_SPEC.md` before implementing any
   new syntax or semantic rule.
2. **Layer discipline.** Respect the project dependency graph
   (Frontend → Semantics → Backend). Runtime behaviour never goes in
   Frontend; the LSP must not depend on Backend.
3. **Test every invariant.** Ship tests that prove new guarantees.
4. **Format `.ash` files.** Run `ashes fmt` after creating or modifying
   any `.ash` source.
5. **No `[NotInParallel]` in tests.** This attribute is banned. Fix the
   root cause instead.

See [docs/ARCHITECTURE.md](ARCHITECTURE.md) for the full dependency graph
and compiler internals.
